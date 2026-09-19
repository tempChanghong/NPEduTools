using System.Diagnostics;
using System.Runtime.Versioning;
using NPEduTools.ClassIsland.Admin;
using NPEduTools.Contracts;

namespace NPEduTools.Host;

[SupportedOSPlatform("windows")]
public sealed class ClassroomModeEffects(LaunchService launch, ExamAwareService examAware, RecordingService recording) : IClassroomModeEffects
{
    private readonly string _helper = Path.Combine(AppContext.BaseDirectory, "Admin", "NPEduTools.ClassIsland.Admin.exe");
    private async Task<LaunchSettings> ClassIslandSettingsAsync()
    {
        var response = await launch.HandleAsync(new(Protocol.Version, Guid.NewGuid(), "classisland.config.get"), CancellationToken.None);
        if (response.Outcome != "Succeeded" || response.Launch is not { StorageWarning: null, Settings.ExecutablePath: not null } data)
            throw new InvalidOperationException("请先在 ClassIsland 页面保存程序位置，并处理配置警告。");
        return data.Settings;
    }
    private async Task<AdminStatus> ClassIslandStatusAsync(string path)
    {
        var response = await AdminClient.RunAsync(_helper, "status", path);
        if (response.Outcome != "Succeeded" || response.Status is not { TaskState: "Enabled" or "Disabled" } status)
            throw new InvalidOperationException("ClassIsland 管理员任务不可用。请先创建当前程序的管理员自启动任务；同名冲突任务不会被覆盖。 " + response.Message);
        return status;
    }
    private static bool Ready(ExamAwareStatus state) => state.BridgeState == "Connected" &&
        state.CanSetAutoStart && state.AutoStartRegistered is not null &&
        state.AutoStartChange?.State != "Sending" && state.Quit?.State is not ("Sending" or "AwaitingExit");
    public async Task<ClassroomStartupSnapshot> ObserveAsync(bool connect)
    {
        var ci = await ClassIslandSettingsAsync();
        var task = await ClassIslandStatusAsync(ci.ExecutablePath!);
        var ea = examAware.Snapshot();
        if (string.IsNullOrWhiteSpace(ea.ExecutablePath)) throw new InvalidOperationException("请先在考试看板页面保存 ExamAware2 程序位置。");
        if (!Ready(ea) && connect && ea.BridgeState != "Connected")
        {
            var result = await examAware.HandleAsync(new(Protocol.Version, Guid.NewGuid(), "examaware.start"), CancellationToken.None);
            if (result.Outcome != "Accepted") throw new InvalidOperationException(result.Message);
            var watch = Stopwatch.StartNew();
            while (watch.Elapsed < TimeSpan.FromSeconds(18))
            {
                await Task.Delay(250);
                ea = examAware.Snapshot();
                if (Ready(ea)) break;
            }
        }
        if (!Ready(ea)) throw new InvalidOperationException("ExamAware 桥接未就绪。请检查配对、桥接 0.3.0 或兼容版本及自启动设置权限，再重试。");
        var currentCi = await ClassIslandSettingsAsync();
        if (ci != currentCi) throw new InvalidOperationException("ClassIsland 位置已改变，请重新检查。");
        return new(ci.ExecutablePath!, ci.Revision, task.TaskState == "Enabled",
            ea.ExecutablePath!, ea.Revision, ea.AutoStartRegistered!.Value);
    }
    public async Task SetClassIslandAsync(ClassroomStartupSnapshot expected, bool enabled)
    {
        ClassroomModeService.RequireSamePrograms(expected, await ObserveAsync(false));
        var status = await ClassIslandStatusAsync(expected.ClassIslandPath);
        if ((status.TaskState == "Enabled") == enabled) return;
        var result = await AdminClient.RunAsync(_helper, enabled ? "enable" : "disable", expected.ClassIslandPath, status.Fingerprint);
        if (result.Outcome != "Succeeded" || result.Status?.TaskState != (enabled ? "Enabled" : "Disabled"))
            throw new InvalidOperationException(result.Message);
    }
    public async Task SetExamAwareAsync(ClassroomStartupSnapshot expected, bool enabled)
    {
        ClassroomModeService.RequireSamePrograms(expected, await ObserveAsync(false));
        var id = Guid.NewGuid();
        var response = await examAware.HandleAsync(new(Protocol.Version, id, "examaware.autostart.set",
            ExpectedRevision: expected.ExamAwareRevision, AutoStartEnabled: enabled), CancellationToken.None);
        if (response.Outcome != "Accepted") throw new InvalidOperationException(response.Message);
        var watch = Stopwatch.StartNew();
        while (watch.Elapsed < TimeSpan.FromSeconds(15))
        {
            await Task.Delay(200);
            var state = examAware.Snapshot();
            if (state.AutoStartChange is not { } change || change.RequestId != id)
                throw new InvalidOperationException("ExamAware 自启动操作状态被替换，请核实实际设置。");
            if (change.State == "Sending") continue;
            if (change.State != "Succeeded" || change.Registered != enabled)
                throw new InvalidOperationException(change.Message);
            return;
        }
        throw new InvalidOperationException("ExamAware 自启动设置未得到确认。");
    }
    public Task PauseRecordingAsync() => recording.PauseForClassroomModeAsync();
}


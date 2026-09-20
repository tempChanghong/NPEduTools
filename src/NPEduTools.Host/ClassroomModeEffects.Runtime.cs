using System.Diagnostics;
using NPEduTools.ClassIsland.Admin;
using NPEduTools.Contracts;
using NPEduTools.Core;

namespace NPEduTools.Host;

public sealed partial class ClassroomModeEffects : IClassroomRuntimeActions
{
    public Task RunRuntimeAsync(ClassroomRuntimeIntent intent, Action<string, string> progress) =>
        new ClassroomRuntimeCoordinator(this).RunAsync(intent, progress);

    public async Task ValidateAsync(ClassroomStartupSnapshot expected)
    {
        var ci = await ClassIslandSettingsAsync();
        var ea = examAware.Snapshot();
        ClassroomModeService.RequireSamePrograms(expected, expected with
        {
            ClassIslandPath = ci.ExecutablePath!, ClassIslandRevision = ci.Revision,
            ExamAwarePath = ea.ExecutablePath ?? "", ExamAwareRevision = ea.Revision
        });
        var task = await ClassIslandStatusAsync(expected.ClassIslandPath);
        if ((task.TaskState == "Enabled") != expected.ClassIslandEnabled ||
            (ea.BridgeState == "Connected" && ea.AutoStartRegistered != expected.ExamAwareEnabled))
            throw new InvalidOperationException("自启动设置已改变，请恢复切换前设置后重新选择模式。");
        if (ea.AutoStartChange?.State == "Sending" || ea.Quit?.State is "Sending" or "AwaitingExit")
            throw new InvalidOperationException("ExamAware2 仍有操作待确认，请稍后重试。");
    }
    public async Task PrepareExamAsync(ClassroomStartupSnapshot expected)
    {
        await ValidateAsync(expected);
        ClassroomModeService.RequireSamePrograms(expected, await ObserveAsync(true));
        await examAware.VerifyConnectedProcessAsync(expected.ExamAwarePath, expected.ExamAwareRevision);
    }
    public async Task CloseClassIslandAsync(ClassroomStartupSnapshot expected)
    {
        // Recheck the destination immediately before sending a normal exit to the current application.
        await PrepareExamAsync(expected);
        var task = await ClassIslandStatusAsync(expected.ClassIslandPath);
        if (task.ProcessState == "Stopped") return;
        var result = await AdminClient.RunAsync(_helper, "close", expected.ClassIslandPath, task.Fingerprint);
        if (result.Outcome != "Succeeded" || result.Status?.ProcessState != "Stopped")
            throw new InvalidOperationException(result.Message);
    }
    public async Task CloseExamAsync(ClassroomStartupSnapshot expected)
    {
        await ValidateAsync(expected);
        if (!ExamAwareTarget.IsRunning(expected.ExamAwarePath)) return;
        var deadline = Stopwatch.StartNew();
        while (examAware.Snapshot().BridgeState != "Connected" && deadline.Elapsed < TimeSpan.FromSeconds(8))
            await Task.Delay(200);
        await examAware.VerifyConnectedProcessAsync(expected.ExamAwarePath, expected.ExamAwareRevision);
        var id = Guid.NewGuid();
        var response = await examAware.HandleAsync(new(Protocol.Version, id, "examaware.quit",
            ExpectedRevision: expected.ExamAwareRevision), CancellationToken.None);
        if (response.Outcome != "Accepted") throw new InvalidOperationException(response.Message);
        deadline.Restart();
        while (deadline.Elapsed < TimeSpan.FromSeconds(13))
        {
            await Task.Delay(200);
            var result = examAware.Snapshot().Quit;
            if (result is null || result.RequestId != id) throw new InvalidOperationException("退出操作状态已改变，请核实后重试。");
            if (result.State is "Sending" or "AwaitingExit") continue;
            if (result.State != "Exited") throw new InvalidOperationException(result.Message);
            var childrenDeadline = Stopwatch.StartNew();
            while (ExamAwareTarget.IsRunning(expected.ExamAwarePath) && childrenDeadline.Elapsed < TimeSpan.FromSeconds(3))
                await Task.Delay(150);
            if (ExamAwareTarget.IsRunning(expected.ExamAwarePath))
                throw new InvalidOperationException("ExamAware2 仍有进程或已重新启动，请确认编辑器和放映全部关闭后重试。");
            return;
        }
        throw new InvalidOperationException("尚未确认 ExamAware2 退出。请保存并关闭编辑器、结束放映后重试；未启动 ClassIsland，也未强制结束进程。");
    }
    public async Task StartClassIslandAsync(ClassroomStartupSnapshot expected)
    {
        await ValidateAsync(expected);
        if (ExamAwareTarget.IsRunning(expected.ExamAwarePath))
            throw new InvalidOperationException("ExamAware2 已重新启动，请先处理考试窗口再重试。");
        var status = await ClassIslandStatusAsync(expected.ClassIslandPath);
        if (status.ProcessState != "Administrator")
        {
            var response = await AdminClient.RunAsync(_helper, "launch-mode", expected.ClassIslandPath, status.Fingerprint);
            if (response.Outcome != "Succeeded" || response.Status?.ProcessState != "Administrator")
                throw new InvalidOperationException(response.Message);
        }
        await WaitForClassIslandAsync(expected);
    }
    private async Task WaitForClassIslandAsync(ClassroomStartupSnapshot expected)
    {
        if (reader is null) throw new InvalidOperationException("后台未配置 ClassIsland 就绪检查，请更新后台。");
        var deadline = Stopwatch.StartNew();
        while (deadline.Elapsed < TimeSpan.FromSeconds(15))
        {
            var response = await reader.ReadAsync(new(TimeSpan.FromSeconds(3), TimeSpan.Zero), CancellationToken.None);
            if (response.Outcome == "Succeeded")
            {
                await ValidateAsync(expected);
                var status = await ClassIslandStatusAsync(expected.ClassIslandPath);
                if (status.ProcessState == "Administrator") return;
                throw new InvalidOperationException("ClassIsland 实例或权限已改变，请重新核实。");
            }
            await Task.Delay(300);
        }
        throw new InvalidOperationException("ClassIsland 进程已启动，但尚未就绪。请完成软件内的首次引导或检查弹窗，再点击重试；不会重复启动已运行实例。");
    }
    public async Task VerifyAsync(ClassroomStartupSnapshot expected, string target)
    {
        await ValidateAsync(expected);
        var ci = await ClassIslandStatusAsync(expected.ClassIslandPath);
        if (target == "Exam")
        {
            if (ci.ProcessState != "Stopped") throw new InvalidOperationException("ClassIsland 尚未退出或已重新启动，请检查后重试。");
            await examAware.VerifyConnectedProcessAsync(expected.ExamAwarePath, expected.ExamAwareRevision);
        }
        else
        {
            if (ExamAwareTarget.IsRunning(expected.ExamAwarePath)) throw new InvalidOperationException("ExamAware2 已重新启动，请检查后重试。");
            if (ci.ProcessState != "Administrator") throw new InvalidOperationException("ClassIsland 管理员实例尚未就绪，请重试。");
            await WaitForClassIslandAsync(expected);
        }
    }
}

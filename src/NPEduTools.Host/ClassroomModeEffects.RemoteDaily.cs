using System.Diagnostics;
using Microsoft.Win32;
using NPEduTools.ClassIsland.Admin;
using NPEduTools.Contracts;

namespace NPEduTools.Host;

public sealed partial class ClassroomModeEffects
{
    public async Task<ClassroomStartupSnapshot> ObserveDailyStartupAsync(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (examAware.Snapshot().BridgeState == "Connected") return await ObserveAsync(false);
        var ci = await ClassIslandSettingsAsync();
        var task = await ClassIslandStatusAsync(ci.ExecutablePath!);
        var ea = examAware.Snapshot();
        if (string.IsNullOrWhiteSpace(ea.ExecutablePath)) throw new RemoteExamException("EXAMAWARE_CONFIGURATION_REQUIRED");
        // ExamAware 1.5.2 sets its Electron app name to ExamAware and uses default
        // login-item settings. Read only: bridge remains the sole writer. Inspect both
        // registry views conservatively so a stopped process need not be relaunched
        // merely to verify that its login entry was removed.
        bool registered = false;
        foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            using var user = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, view);
            using var run = user.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", false);
            registered |= run?.GetValue("ExamAware") is not null;
        }
        return new(ci.ExecutablePath!, ci.Revision, task.TaskState == "Enabled",
            ea.ExecutablePath!, ea.Revision, registered);
    }

    public async Task<bool> ClassIslandReadyAsync(CancellationToken token)
    {
        if (reader is null) return false;
        var reply = await reader.ReadAsync(new(TimeSpan.FromSeconds(2), TimeSpan.Zero), token);
        return reply.Outcome == "Succeeded";
    }

    public async Task StartClassIslandRemoteAsync(ClassroomStartupSnapshot expected, Func<Task> beforeDispatch, CancellationToken token)
    {
        await beforeDispatch();
        if (ExamAwareTarget.IsRunning(expected.ExamAwarePath)) throw new RemoteExamException("EXAMAWARE_EXIT_FAILED");
        var actual = await ObserveDailyStartupAsync(token);
        ClassroomModeService.RequireSamePrograms(expected, actual);
        if (!actual.ClassIslandEnabled || actual.ExamAwareEnabled) throw new RemoteExamException("STARTUP_NOT_READY");
        var status = await ClassIslandStatusAsync(expected.ClassIslandPath);
        if (status.ProcessState != "Administrator")
        {
            var response = await AdminClient.RunAsync(_helper, "runtime-launch-mode", expected.ClassIslandPath,
                status.Fingerprint, beforeDispatch);
            if (response.Outcome != "Succeeded" || response.Status?.ProcessState != "Administrator")
                throw new RemoteExamException("CLASSISLAND_NOT_READY", response.Message);
        }
        var deadline = Stopwatch.StartNew();
        while (deadline.Elapsed < TimeSpan.FromSeconds(18))
        {
            await beforeDispatch();
            if (ExamAwareTarget.IsRunning(expected.ExamAwarePath)) throw new RemoteExamException("EXAMAWARE_EXIT_FAILED");
            if (await ClassIslandReadyAsync(token))
            {
                var final = await ClassIslandStatusAsync(expected.ClassIslandPath);
                if (final.ProcessState == "Administrator") return;
                throw new RemoteExamException("CLASSISLAND_NOT_READY");
            }
            await Task.Delay(300, token);
        }
        throw new RemoteExamException("CLASSISLAND_NOT_READY");
    }
}

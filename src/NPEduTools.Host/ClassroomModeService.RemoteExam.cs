using NPEduTools.Contracts;

namespace NPEduTools.Host;

public interface IRemoteClassroomModeEffects
{
    Task ValidateRemoteAsync(CancellationToken token);
    Task SetClassIslandRemoteAsync(ClassroomStartupSnapshot expected, bool enabled, Func<Task> beforeDispatch);
    Task SetExamAwareRemoteAsync(ClassroomStartupSnapshot expected, bool enabled, Func<Task> beforeDispatch);
    Task<ClassroomStartupSnapshot> ObserveDailyStartupAsync(CancellationToken token) =>
        throw new RemoteExamException("MODE_CONTROL_UNAVAILABLE");
    Task<bool> ClassIslandReadyAsync(CancellationToken token) => Task.FromResult(false);
    Task StartClassIslandRemoteAsync(ClassroomStartupSnapshot expected, Func<Task> beforeDispatch, CancellationToken token) =>
        throw new RemoteExamException("MODE_CONTROL_UNAVAILABLE");
}

public sealed partial class ClassroomModeService
{
    internal Task<bool> ObserveClassIslandReadyAsync(CancellationToken token) => effects is IRemoteClassroomModeEffects remote
        ? remote.ClassIslandReadyAsync(token) : Task.FromResult(false);

    internal async Task<bool?> ObserveDailyStartupAsync(CancellationToken token)
    {
        if (effects is not IRemoteClassroomModeEffects remote) return null;
        try
        {
            var current = await remote.ObserveDailyStartupAsync(token);
            return current.ClassIslandEnabled && !current.ExamAwareEnabled;
        }
        catch (Exception error) when (error is InvalidOperationException or IOException or UnauthorizedAccessException or RemoteExamException)
        { return null; }
    }

    internal async Task<long> EnterRemoteDailyAsync(RemoteExamConfiguration expected, long expectedRevision,
        Func<long, Task> check, Func<Func<Task>, Task> closeExam, Action<string> progress, CancellationToken token)
    {
        if (!_runtimeGate.Switching || Busy || _stopping) throw new RemoteExamException("OPERATION_BUSY");
        if (effects is not IRemoteClassroomModeEffects remote) throw new RemoteExamException("MODE_CONTROL_UNAVAILABLE");
        var prior = store.State;
        if (prior.Revision != expectedRevision) throw new RemoteExamException("STATE_CHANGED");
        if (prior.Phase == "Unavailable") throw new RemoteExamException("STORAGE_UNAVAILABLE");
        long ownedRevision = prior.Revision;
        bool touched = false;
        async Task Check()
        {
            token.ThrowIfCancellationRequested();
            if (store.State.Revision != ownedRevision) throw new RemoteExamException("STATE_CHANGED");
            await check(ownedRevision);
        }
        try
        {
            await Check();
            var before = await remote.ObserveDailyStartupAsync(token);
            RequireSamePrograms(before, new(expected.ClassIslandPath, expected.ClassIslandRevision, before.ClassIslandEnabled,
                expected.ExamAwarePath, expected.ExamAwareRevision, before.ExamAwareEnabled));
            store.Save(prior with { Phase = "Switching", AutomaticPaused = true,
                Recovery = prior.Recovery ?? new(prior.Mode, prior.AutomaticPaused, before), Runtime = null,
                Actual = before, CheckedAt = DateTimeOffset.UtcNow, MatchesMode = null, Message = "远程返回日常：正在恢复登录自启动…" });
            ownedRevision = store.State.Revision; touched = true;
            progress("SetStartup");
            var after = await ClassroomStartupCoordinator.ApplyAsync(before, true, false,
                enabled => remote.SetClassIslandRemoteAsync(before, enabled, Check),
                enabled => remote.SetExamAwareRemoteAsync(before, enabled, Check),
                () => remote.ObserveDailyStartupAsync(token), Check);
            progress("CloseExam");
            await Check(); await closeExam(Check);
            progress("StartClassIsland");
            await Check(); await remote.StartClassIslandRemoteAsync(after, Check, token);
            progress("Verify");
            await Check();
            var verified = await remote.ObserveDailyStartupAsync(token);
            RequireSamePrograms(after, verified);
            if (!verified.ClassIslandEnabled || verified.ExamAwareEnabled) throw new RemoteExamException("STARTUP_NOT_READY");
            if (!await remote.ClassIslandReadyAsync(token)) throw new RemoteExamException("CLASSISLAND_NOT_READY");
            await Check();
            store.Save(store.State with { Mode = "Daily", Phase = "Idle", AutomaticPaused = false,
                Recovery = null, Runtime = null, Actual = verified, CheckedAt = DateTimeOffset.UtcNow,
                MatchesMode = true, Message = "远程返回日常已完成：ExamAware2 已退出，ClassIsland 已就绪。录课按原配置判断。" });
            return store.State.Revision;
        }
        catch (Exception error)
        {
            if (touched && store.State.Phase != "Unavailable")
                store.Save(store.State with { Phase = "Incomplete", AutomaticPaused = true, Actual = null,
                    CheckedAt = null, MatchesMode = null,
                    Message = "返回日常未完成，录课保持暂停。处理原因后可从学校网页重试。" +
                        (error is RemoteExamException { LocalMessage: { } detail } ? "\n" + detail : "") });
            throw;
        }
    }

    internal async Task<bool?> ObserveExamStartupAsync(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (effects is not IRemoteClassroomModeEffects) return null;
        try
        {
            var current = await effects.ObserveAsync(false);
            return !current.ClassIslandEnabled && current.ExamAwareEnabled;
        }
        catch (Exception error) when (error is InvalidOperationException or IOException or UnauthorizedAccessException or RemoteExamException)
        { return null; }
    }
    internal Task ValidateRemoteAsync(CancellationToken token) => effects is IRemoteClassroomModeEffects remote
        ? remote.ValidateRemoteAsync(token) : throw new RemoteExamException("MODE_CONTROL_UNAVAILABLE");

    // The N3 executor owns the runtime gate and has already finalized/reserved recording.
    // Reuse the local store, original recovery baseline and startup coordinator.
    internal async Task<long> EnterRemoteExamAsync(RemoteExamConfiguration expected, long expectedRevision,
        Func<long, Task> check, Func<Func<Task>, Task> closeClassIsland, Action<string> progress, CancellationToken token)
    {
        if (!_runtimeGate.Switching || Busy || _stopping) throw new RemoteExamException("OPERATION_BUSY");
        if (effects is not IRemoteClassroomModeEffects remote) throw new RemoteExamException("MODE_CONTROL_UNAVAILABLE");
        var prior = store.State;
        if (prior.Revision != expectedRevision) throw new RemoteExamException("STATE_CHANGED");
        if (prior.Phase == "Unavailable") throw new RemoteExamException("STORAGE_UNAVAILABLE");
        long ownedRevision = prior.Revision;
        bool touched = false;
        async Task Check()
        {
            token.ThrowIfCancellationRequested();
            if (store.State.Revision != ownedRevision) throw new RemoteExamException("STATE_CHANGED");
            await check(ownedRevision);
        }
        try
        {
            await Check();
            var before = await effects.ObserveAsync(false);
            RequireSamePrograms(before, new(expected.ClassIslandPath, expected.ClassIslandRevision, before.ClassIslandEnabled,
                expected.ExamAwarePath, expected.ExamAwareRevision, before.ExamAwareEnabled));
            await Check();
            store.Save(prior with { Phase = "Switching", AutomaticPaused = true,
                RecentRequests = prior.RecentRequests ?? [],
                Recovery = prior.Recovery ?? new(prior.Mode, prior.AutomaticPaused, before), Runtime = null, Actual = before,
                CheckedAt = DateTimeOffset.UtcNow, MatchesMode = null, Message = "远程考试：正在设置登录自启动…" });
            ownedRevision = store.State.Revision;
            touched = true;
            progress("SetStartup");
            var after = await ClassroomStartupCoordinator.ApplyAsync(before, false, true,
                enabled => remote.SetClassIslandRemoteAsync(before, enabled, Check),
                enabled => remote.SetExamAwareRemoteAsync(before, enabled, Check),
                () => effects.ObserveAsync(false), Check);
            progress("CloseClassIsland");
            await Check();
            await closeClassIsland(Check);
            progress("Verify");
            await Check();
            var verified = await effects.ObserveAsync(false);
            RequireSamePrograms(after, verified);
            if (verified.ClassIslandEnabled || !verified.ExamAwareEnabled)
                throw new RemoteExamException("STARTUP_NOT_READY");
            await Check();
            store.Save(store.State with { Mode = "Exam", Phase = "Idle", AutomaticPaused = true,
                Recovery = null, Runtime = null, Actual = verified, CheckedAt = DateTimeOffset.UtcNow,
                MatchesMode = true, Message = "远程考试模式已生效：自启动已切换，ExamAware2 已就绪，ClassIsland 已退出。" });
            return store.State.Revision;
        }
        catch (Exception error)
        {
            if (touched && store.State.Phase != "Unavailable")
                store.Save(store.State with { Phase = "Incomplete", AutomaticPaused = true, Actual = null,
                    CheckedAt = null, MatchesMode = null,
                    Message = "远程考试切换未完成。可从学校网页重试，程序将核对实际状态并补做缺失步骤。" +
                        (error is RemoteExamException { LocalMessage: { } detail } ? "\n" + detail : "") });
            throw;
        }
    }
}

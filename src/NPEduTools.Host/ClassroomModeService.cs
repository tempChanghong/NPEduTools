using NPEduTools.Contracts;

namespace NPEduTools.Host;

public interface IClassroomModeEffects
{
    Task<ClassroomStartupSnapshot> ObserveAsync(bool connect);
    Task SetClassIslandAsync(ClassroomStartupSnapshot expected, bool enabled);
    Task SetExamAwareAsync(ClassroomStartupSnapshot expected, bool enabled);
    Task PauseRecordingAsync();
}

/// <summary>Accepted requests survive client disconnect; each external write is preceded by a durable recovery point.</summary>
public sealed class ClassroomModeService(ClassroomModeStore store, IClassroomModeEffects effects) : IAsyncDisposable
{
    private readonly object _gate = new();
    private Task _operation = Task.CompletedTask;
    private bool _stopping;
    public bool Busy { get { lock (_gate) return !_operation.IsCompleted; } }
    public ClassroomModeState Snapshot => store.State;

    public HostResponse Handle(HostRequest request)
    {
        lock (_gate)
        {
            HostResponse Reply(string outcome, string? error = null) =>
                new(Protocol.Version, request.RequestId, outcome, error, store.State.Message, ClassroomMode: store.State);
            if (Protocol.Validate(request) is { } invalid) return Reply("Rejected", invalid);
            if (request.Capability == "classroom.status") return Reply("Succeeded");
            if (_stopping || !_operation.IsCompleted) return Reply("Rejected", "ClassroomBusy");
            if (store.State.Phase == "Unavailable") return Reply("Rejected", "ClassroomStorageUnavailable");
            if (request.Capability is not ("classroom.refresh" or "classroom.set" or "classroom.restore"))
                return Reply("Rejected", "UnknownCapability");
            bool refresh = request.Capability == "classroom.refresh";
            if (!refresh && store.State.RecentRequests?.Contains(request.RequestId) == true)
                return Reply("Rejected", "AlreadyHandled");
            if (!refresh && request.ExpectedRevision != store.State.Revision) return Reply("Rejected", "RevisionConflict");
            if (request.Capability == "classroom.restore" && store.State.Recovery is null)
                return Reply("Rejected", "NoRecovery");
            // Never overwrite an unresolved recovery baseline with a new mode request.
            if (request.Capability == "classroom.set" && store.State.Recovery is not null)
                return Reply("Rejected", "RestoreRequired");
            var prior = store.State;
            try
            {
                store.Save(prior with { Phase = "Checking", Message = refresh ? "正在核实实际自启动状态…" : "正在检查切换条件…",
                    RecentRequests = refresh ? prior.RecentRequests ?? [] :
                        (prior.RecentRequests ?? []).Append(request.RequestId).TakeLast(64).ToArray() });
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return Reply("Failed", "StorageFailed"); }
            _operation = Task.Run(() => ExecuteAsync(request, prior));
            return Reply("Accepted");
        }
    }

    private async Task ExecuteAsync(HostRequest request, ClassroomModeState prior)
    {
        bool touched = false;
        try
        {
            bool refresh = request.Capability == "classroom.refresh";
            var before = await effects.ObserveAsync(!refresh);
            if (refresh)
            {
                SaveObservation(before, prior, prior.Phase, prior.AutomaticPaused, prior.Recovery,
                    Matches(before, prior.Mode) == false ? "实际自启动设置与当前模式不一致，请重新切换或检查软件设置。" : "已核实实际自启动状态。");
                return;
            }
            bool restore = request.Capability == "classroom.restore";
            var recovery = restore ? prior.Recovery! : new ClassroomModeRecovery(prior.Mode, prior.AutomaticPaused, before);
            if (restore) RequireSamePrograms(recovery.Startup, before);
            string target = restore ? recovery.PreviousMode : request.ClassroomMode!.Target;
            bool ci = restore ? recovery.Startup.ClassIslandEnabled : target == "Daily";
            bool ea = restore ? recovery.Startup.ExamAwareEnabled : target == "Exam";
            store.Save(store.State with { Phase = "Switching", AutomaticPaused = true, Recovery = recovery,
                Actual = before, CheckedAt = DateTimeOffset.UtcNow, MatchesMode = null,
                Message = "已暂停自动录课，正在设置自启动；ClassIsland 可能请求管理员授权。" });
            touched = true;
            await effects.PauseRecordingAsync();
            // Enable the intended startup first; avoid leaving both apps disabled if the second action fails.
            if (ci && !before.ClassIslandEnabled) await effects.SetClassIslandAsync(before, true);
            if (ea && !before.ExamAwareEnabled) await effects.SetExamAwareAsync(before, true);
            if (!ci && before.ClassIslandEnabled) await effects.SetClassIslandAsync(before, false);
            if (!ea && before.ExamAwareEnabled) await effects.SetExamAwareAsync(before, false);
            var after = await effects.ObserveAsync(false);
            RequireSamePrograms(before, after);
            if (after.ClassIslandEnabled != ci || after.ExamAwareEnabled != ea)
                throw new InvalidOperationException("读回结果与目标不一致，可能有其他窗口修改了设置。");
            SaveObservation(after, store.State with { Mode = target }, "Idle",
                restore ? recovery.PreviousPause : target == "Exam", null,
                restore ? "已恢复切换前的自启动设置和录课模式。" :
                target == "Exam" ? "考试模式已生效。自动录课已暂停，原计划保留。" :
                "日常模式已生效。自动录课恢复按原计划判断，仍需保持录制已启用和学校时间可用。");
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // A lost acknowledgement is not a rollback. Keep the recovery point until explicit, verified recovery.
            if (store.State.Phase == "Unavailable") return;
            try
            {
                store.Save(store.State with { Phase = touched || prior.Recovery is not null ? "Incomplete" : prior.Phase,
                    AutomaticPaused = touched || prior.AutomaticPaused, Actual = null, CheckedAt = null, MatchesMode = null,
                    Message = (touched ? "切换未完成，自动录课保持暂停。可恢复切换前设置。 " : "检查未通过，未修改自启动设置。 ") + ex.Message });
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        }
    }

    private void SaveObservation(ClassroomStartupSnapshot actual, ClassroomModeState basis, string phase, bool pause,
        ClassroomModeRecovery? recovery, string message) =>
        store.Save(basis with { Phase = phase, AutomaticPaused = pause, Recovery = recovery, Actual = actual,
            CheckedAt = DateTimeOffset.UtcNow, MatchesMode = Matches(actual, basis.Mode), Message = message,
            RecentRequests = store.State.RecentRequests });
    private static bool? Matches(ClassroomStartupSnapshot actual, string mode) => mode switch
    {
        "Daily" => actual.ClassIslandEnabled && !actual.ExamAwareEnabled,
        "Exam" => !actual.ClassIslandEnabled && actual.ExamAwareEnabled,
        _ => null
    };
    public static void RequireSamePrograms(ClassroomStartupSnapshot expected, ClassroomStartupSnapshot actual)
    {
        if (!string.Equals(expected.ClassIslandPath, actual.ClassIslandPath, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(expected.ExamAwarePath, actual.ExamAwarePath, StringComparison.OrdinalIgnoreCase) ||
            expected.ClassIslandRevision != actual.ClassIslandRevision || expected.ExamAwareRevision != actual.ExamAwareRevision)
            throw new InvalidOperationException("程序位置或配对已改变，请恢复原配置后再操作。");
    }
    public bool BeginShutdown()
    {
        lock (_gate) { if (!_operation.IsCompleted) return false; _stopping = true; return true; }
    }
    public async ValueTask DisposeAsync()
    {
        Task pending;
        lock (_gate) { _stopping = true; pending = _operation; }
        await pending;
    }
}


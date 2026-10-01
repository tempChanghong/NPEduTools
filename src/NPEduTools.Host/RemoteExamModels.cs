namespace NPEduTools.Host;

// Internal execution contracts, NOT NPEP wire DTOs. No path, startup option or arbitrary
// capability is accepted from a remote requester. The adapter supplies locally verified paths.
public sealed record RemoteExamIntent(Guid OperationId, long ExpectedRuntimeRevision, long ExpectedModeRevision,
    bool SwitchMode = false, string Target = "Exam");
public sealed record RemoteExamConfiguration(string ClassIslandPath, long ClassIslandRevision,
    string ExamAwarePath, long ExamAwareRevision);
public sealed record RemoteExamObservation(RemoteExamConfiguration Configuration, long ModeRevision,
    bool ExamAwareReady, bool ClassIslandStopped, bool DesktopAvailable = true,
    bool NoticeOpen = false, bool RecoveryRequired = false, bool PendingActions = false, bool HostElevated = true,
    string ClassroomMode = "Unconfigured", bool? StartupReady = null, bool ExamAwareStopped = false,
    bool ClassIslandReady = false, bool? DailyStartupReady = null);
public sealed record RemoteExamEntry(RemoteExamIntent Intent, string Outcome, string Step,
    DateTimeOffset UpdatedAt, bool PauseEstablished = false, RemoteExamConfiguration? Configuration = null,
    string? Reason = null, DateTimeOffset? ResolvedAt = null, DateTimeOffset? LocallyEndedAt = null,
    RemoteExamObservation? Observed = null, bool AlreadySatisfied = false, Guid? SupersededBy = null);
public sealed record RemoteExamDocument(int Version = 1, long Revision = 0, bool AutomaticPaused = false,
    RemoteExamEntry[]? Operations = null, string? StorageError = null, Guid? PauseOperationId = null);

public sealed class RemoteExamException(string code, string? localMessage = null) : Exception(code)
{
    public string Code { get; } = code;
    // For local diagnostics only. The execution journal and future wire results retain Code.
    public string? LocalMessage { get; } = localMessage;
}

public interface IRemoteExamStore
{
    RemoteExamDocument State { get; }
    void Save(RemoteExamDocument state);
}

/// <summary>
/// The production adapter must preserve the normal-quit and configured-process identity checks.
/// Fixed Exam/Daily targets use the local mode service. Recording is finalized before the
/// reservation is handed to either transition; legacy inspection only reserves an idle recorder.
/// </summary>
public interface IRemoteExamActions
{
    Task<IDisposable> ReserveIdleRecordingAsync(CancellationToken token);
    Task<IDisposable> FinishRecordingAndReserveAsync(CancellationToken token) => ReserveIdleRecordingAsync(token);
    Task<RemoteExamObservation> InspectAsync(CancellationToken token);
    Task PrepareExamAsync(RemoteExamConfiguration expected, Func<Task> beforeDispatch, CancellationToken token);
    Task CloseClassIslandAsync(RemoteExamConfiguration expected, Func<Task> beforeDispatch, CancellationToken token);
    Task ValidateModeAsync(CancellationToken token) => throw new RemoteExamException("MODE_CONTROL_UNAVAILABLE");
    Task<long> EnterExamModeAsync(RemoteExamConfiguration expected, long revision, Func<Task> authorize,
        Action<string> progress, CancellationToken token) => throw new RemoteExamException("MODE_CONTROL_UNAVAILABLE");
    Task<long> EnterDailyModeAsync(RemoteExamConfiguration expected, long revision, Func<Task> authorize,
        Action<string> progress, CancellationToken token) => throw new RemoteExamException("MODE_CONTROL_UNAVAILABLE");
}

/// <summary>
/// Implemented by the trusted N3 transport only after identity, consent, generation and permit
/// checks. Called at every boundary, including after a potentially long UAC wait by the adapter.
/// firstEffect=true additionally checks the fixed start permit against a monotonic deadline.
/// </summary>
public interface IRemoteExamAuthorization
{
    Task CheckAsync(bool firstEffect, CancellationToken token);
}

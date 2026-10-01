namespace NPEduTools.Contracts;

// Local IPC only. This is not the N3 server wire contract or an execution grant.
public sealed record RemoteExamCommand(string Action, long Revision, bool? Allowed = null,
    string? Scope = null, Guid? OperationId = null);
public sealed record RemoteExamPolicy(long Revision, bool Allowed, string? Scope,
    Guid ConsentId, Guid ControlEpoch, bool CanEnable, string Message, string? Error = null, string? Binding = null);
public sealed record RemoteExamHistory(Guid OperationId, string Outcome, string Step, string? Reason,
    DateTimeOffset UpdatedAt, DateTimeOffset? LocallyEndedAt, string Target = "Exam");
public sealed record RemoteExamStatus(RemoteExamPolicy Policy, long RuntimeRevision, bool AutomaticPaused,
    Guid? PauseOperationId, string? StorageError, RemoteExamHistory[] History, RemoteExamPolicy? PlanPolicy = null);

public static class RemoteExamContract
{
    public static bool Valid(RemoteExamCommand c) => c.Revision >= 0 && (c.Action switch
    {
        "consent" or "plan-consent" => c.Allowed is not null && c.OperationId is null &&
            (c.Scope is null ? c.Allowed == false : c.Scope.Length == 64 && c.Scope.All(char.IsAsciiHexDigit)),
        "end-local" => c.Allowed is null && c.Scope is null && c.OperationId is { } id && id != Guid.Empty,
        _ => false
    });
}

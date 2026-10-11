using System.Text.Json.Nodes;
namespace NPEduTools.Contracts;
// Opt-in local status; no passwords, picture bytes or purported legal finding.
public sealed record StudyExitState(Guid AttemptId, string State, string? Reason, double VerificationRemainingSeconds,
    double WarningRemainingSeconds, string PhotoState, string AuditState, string? AuditError=null);
public sealed record StudyExitPacket(string Scope, JsonObject Metadata);

using System.Text.Json.Nodes;

namespace NPEduTools.Contracts;

// Local UI commands contain no device credentials or arbitrary cloud request body.
public sealed record NpepCommand(string Action, long Revision, string? Origin = null, string? DeviceName = null,
    string? ServerInstanceId = null, string? DeploymentEpoch = null, string? ApprovalId = null);
public sealed record NpepState(long Revision, string State, string Connection, string Message, bool Busy = false,
    string? Origin = null, JsonObject? Server = null, JsonObject? Pairing = null, JsonObject? Approval = null,
    bool ReportingPaused = false, string? LastReceivedAt = null, string? Error = null, Guid? OperationId = null);

public static class NpepContract
{
    public static bool Valid(NpepCommand c)
    {
        if (c.Revision < 0) return false;
        bool Uuid(string? s) => s is not null && Guid.TryParseExact(s, "D", out var id) && id != Guid.Empty;
        bool Origin(string? s) => s is { Length: > 0 and <= 2048 } && !s.Any(char.IsControl) && !s.Contains('\\') &&
            Uri.TryCreate(s, UriKind.Absolute, out var uri) && uri.Scheme == "https" && uri.UserInfo == "" && uri.Query == "" && uri.Fragment == "" && uri.AbsolutePath == "/";
        if (c.Action == "inspect") return Origin(c.Origin) && c.DeviceName is null && c.ServerInstanceId is null && c.DeploymentEpoch is null && c.ApprovalId is null;
        if (c.Action == "pair") return Origin(c.Origin) && c.DeviceName is { Length: > 0 and <= 64 } && !string.IsNullOrWhiteSpace(c.DeviceName) && !c.DeviceName.Any(char.IsControl) && Uuid(c.ServerInstanceId) && Uuid(c.DeploymentEpoch) && c.ApprovalId is null;
        if (c.Action == "confirm") return Uuid(c.ApprovalId) && c.Origin is null && c.DeviceName is null && c.ServerInstanceId is null && c.DeploymentEpoch is null;
        return c.Action is "poll" or "recover" or "resume-create" or "unpair" or "pause" or "resume" &&
            c.Origin is null && c.DeviceName is null && c.ServerInstanceId is null && c.DeploymentEpoch is null && c.ApprovalId is null;
    }
}

using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Globalization;
using System.Text;

namespace NPEduTools.Contracts;

// Local UI commands contain no device credentials or arbitrary cloud request body.
public sealed record NpepCommand(string Action, long Revision, string? Origin = null, string? DeviceName = null,
    string? ServerInstanceId = null, string? DeploymentEpoch = null, string? ApprovalId = null, string? UserCode = null,string? ScreenBindingId = null,string? ManagementSecret = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? SchoolQuery = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ClassQuery = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? CatalogueAfter = null);
public sealed record NpepState(long Revision, string State, string Connection, string Message, bool Busy = false,
    string? Origin = null, JsonObject? Server = null, JsonObject? Pairing = null, JsonObject? Approval = null,
    bool ReportingPaused = false, string? LastReceivedAt = null, string? Error = null, Guid? OperationId = null, string? PairingSource = null,JsonObject? Catalogue = null)
{
    // Optional data inside the existing JSON object keeps the strict local IPC envelope readable by older clients.
    private string? Target(string field) => Catalogue?["replacementTarget"] is JsonObject target &&
        target[field] is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;
    [JsonIgnore] public string? RequestedScreenBindingId => Target("screenBindingId");
    [JsonIgnore] public string? RequestedScreenName => Target("screenName");
    [JsonIgnore] public string? RequestedDeviceName => Target("deviceName");
}

public static class NpepContract
{
    public static bool CatalogueId(string? value) => value is { Length: > 0 and <= 191 } &&
        value.All(ch => char.IsAsciiLetterOrDigit(ch) || ch is '_' or '-' or '.' or ':');

    private static bool CatalogueQuery(string? value) => value is { Length: > 0 and <= 382 } &&
        !string.IsNullOrWhiteSpace(value) && value.EnumerateRunes().Count() <= 191 && value.EnumerateRunes().All(r =>
            Rune.GetUnicodeCategory(r) is not (UnicodeCategory.Control or UnicodeCategory.Format));

    public static bool Valid(NpepCommand c)
    {
        if (c.Revision < 0) return false;
        if (c.Action == "find-class") return CatalogueQuery(c.SchoolQuery) && CatalogueQuery(c.ClassQuery) &&
            (c.CatalogueAfter is null || CatalogueId(c.CatalogueAfter)) && Origin(c.Origin) &&
            Uuid(c.ServerInstanceId) && Uuid(c.DeploymentEpoch) && c.DeviceName is null && c.ApprovalId is null &&
            c.UserCode is null && c.ScreenBindingId is null && c.ManagementSecret is null;
        if (c.SchoolQuery is not null || c.ClassQuery is not null || c.CatalogueAfter is not null) return false;
        if(c.Action is "apply-class" or "password-class" or "replacement-class")return CatalogueId(c.ScreenBindingId) &&
            (c.Action=="apply-class"?c.ManagementSecret is null:NoiseManagementContract.SecretValid(c.ManagementSecret)) &&
            Valid(c with{Action="pair",ScreenBindingId=null,ManagementSecret=null});
        if(c.ScreenBindingId is not null || c.ManagementSecret is not null)return false;
        if (c.Action == "claim") return c.UserCode is { Length: 8 } && c.UserCode.All(ch => "ABCDEFGHJKLMNPQRSTUVWXYZ23456789".Contains(ch)) &&
            Valid(c with { Action = "pair", UserCode = null });
        if (c.UserCode is not null) return false;
        bool Uuid(string? s) => s is not null && Guid.TryParseExact(s, "D", out var id) && id != Guid.Empty;
        bool Origin(string? s) => s is { Length: > 0 and <= 2048 } && !s.Any(char.IsControl) && !s.Contains('\\') &&
            Uri.TryCreate(s, UriKind.Absolute, out var uri) && uri.Scheme is ("https" or "http") && uri.UserInfo == "" && uri.Query == "" && uri.Fragment == "" && uri.AbsolutePath == "/";
        if (c.Action == "inspect") return Origin(c.Origin) && c.DeviceName is null && c.ServerInstanceId is null && c.DeploymentEpoch is null && c.ApprovalId is null;
        if (c.Action == "pair") return Origin(c.Origin) && c.DeviceName is { Length: > 0 and <= 64 } && !string.IsNullOrWhiteSpace(c.DeviceName) && !c.DeviceName.Any(char.IsControl) && Uuid(c.ServerInstanceId) && Uuid(c.DeploymentEpoch) && c.ApprovalId is null;
        if (c.Action == "confirm") return Uuid(c.ApprovalId) && c.Origin is null && c.DeviceName is null && c.ServerInstanceId is null && c.DeploymentEpoch is null;
        return c.Action is "poll" or "recover" or "resume-create" or "unpair" or "pause" or "resume" &&
            c.Origin is null && c.DeviceName is null && c.ServerInstanceId is null && c.DeploymentEpoch is null && c.ApprovalId is null;
    }
}

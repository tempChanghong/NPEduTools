using System.Text.Json.Nodes;
using NPEduTools.Contracts;

namespace NPEduTools.App;

/// <summary>Explicit support-data allow-list. Never serialize a Host response or pairing object directly.</summary>
public static class NpepDiagnostics
{
    public static JsonObject Create(string appVersion, DateTimeOffset generatedAt,
        HostResponse? connection, DateTimeOffset? connectionAt, HostResponse? cached, DateTimeOffset? cachedAt, HostResponse? history = null)
    {
        var state = connection?.Outcome == "Succeeded" ? connection.Npep : null;
        var local = cached?.Outcome == "Succeeded" ? cached : null;
        return new()
        {
            ["schemaVersion"] = 1, ["kind"] = "npep-desktop-diagnostics",
            ["generatedAt"] = generatedAt.ToUniversalTime().ToString("O"),
            ["appVersion"] = ApplicationVersion(appVersion), ["protocolVersion"] = Protocol.Version,
            ["privacy"] = "Includes service origin, binding identifiers and bounded redacted connection events. No pairing codes, credentials, names, paths, raw logs, request/response bodies or audio. Review before sharing.",
            ["observation"] = "Read-only local snapshots; sections collected separately. This is not a live probe or a remote execution receipt.",
            ["connectionHistory"] = History(history),
            ["connection"] = new JsonObject
            {
                ["available"] = state is not null, ["observedAt"] = At(state is not null ? connectionAt : null),
                ["readOutcome"] = Choice(connection?.Outcome, "Succeeded Rejected Unavailable Timeout"),
                ["state"] = Choice(state?.State, "UNPAIRED CREATING PENDING APPROVED CONFIRMING ACTIVE SUSPENDED UNPAIRING STORE_UNAVAILABLE"),
                ["transport"] = Choice(state?.Connection, "CONNECTING ONLINE OFFLINE STOPPED PAUSED DISABLED UNAVAILABLE"),
                ["error"] = Error(state?.Error ?? connection?.ErrorCode), ["revision"] = Nonnegative(state?.Revision),
                ["reportingPaused"] = state?.ReportingPaused, ["busy"] = state?.Busy,
                ["origin"] = Origin(state?.Origin), ["lastReceivedAt"] = Timestamp(state?.LastReceivedAt),
                ["serverInstanceId"] = Id(Read(state?.Server, "serverInstanceId")),
                ["deploymentEpoch"] = Id(Read(state?.Server, "deploymentEpoch")),
                ["operationId"] = Id(state?.OperationId),
                ["binding"] = new JsonObject
                {
                    ["schoolId"] = Id(Read(state?.Approval, "schoolId")),
                    ["administrativeClassId"] = Id(Read(state?.Approval, "administrativeClassId")),
                    ["screenBindingId"] = Id(Read(state?.Approval, "screenBindingId"))
                }
            },
            ["localCache"] = new JsonObject
            {
                ["available"] = local is not null, ["observedAt"] = At(local is not null ? cachedAt : null),
                ["readOutcome"] = Choice(cached?.Outcome, "Succeeded Rejected Unavailable Timeout"),
                ["mode"] = Choice(local?.ClassroomMode?.Mode, "Unconfigured Daily Exam"),
                ["modePhase"] = Choice(local?.ClassroomMode?.Phase, "Idle Checking Switching Running Ready Incomplete Unavailable"),
                ["modeCheckedAt"] = At(local?.ClassroomMode?.CheckedAt),
                ["automaticPaused"] = local?.ClassroomMode?.AutomaticPaused,
                ["automaticRecordingEnabled"] = local?.Automatic?.Enabled,
                ["recordingPhase"] = Choice(local?.Recording?.Phase, "Idle Starting Recording Pausing Paused Saving Saved Failed Interrupted"),
                ["schoolClockState"] = Choice(local?.SchoolClock?.State, "Advancing WarmingUp Discontinuous FrozenSuspected Stale Unavailable"),
                ["examAwareBridge"] = Choice(local?.ExamAware?.BridgeState, "Connected Disconnected Unavailable UnsupportedVersion"),
                ["examAwareVersion"] = Version(local?.ExamAware?.AppVersion)
            }
        };
    }

    private static string? Read(JsonObject? obj, string key) => obj?[key] is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;
    private static string Choice(string? value, string choices) => value is not null && choices.Split(' ').Contains(value) ? value : "UNKNOWN";
    private static long? Nonnegative(long? value) => value >= 0 ? value : null;
    private static string? Id(Guid? value) => value is { } id && id != Guid.Empty ? id.ToString("D") : null;
    private static string? Id(string? value) => Guid.TryParseExact(value, "D", out var id) ? Id(id) : null;
    private static string? At(DateTimeOffset? value) => value?.ToUniversalTime().ToString("O");
    private static string? Timestamp(string? value) => DateTimeOffset.TryParse(value, out var at) ? At(at) : null;
    private static string? Origin(string? value) => Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
        uri.Scheme is "http" or "https" && uri.UserInfo == "" ? uri.GetLeftPart(UriPartial.Authority) : null;
    private static string? ApplicationVersion(string? value)
    {
        if (Version(value) is { } plain) return plain;
        if (value is not { Length: > 0 and <= 160 }) return null;
        // Only the application's known build label is stripped; arbitrary metadata stays excluded.
        var match = System.Text.RegularExpressions.Regex.Match(value,
            @"\Av(\d+\.\d+\.\d+) · EVA-02（二号机）(\+[a-fA-F0-9]{7,64})?\z",
            System.Text.RegularExpressions.RegexOptions.CultureInvariant);
        return match.Success ? match.Groups[1].Value + match.Groups[2].Value : null;
    }
    private static string? Version(string? value) => value is { Length: > 0 and <= 160 } &&
        System.Text.RegularExpressions.Regex.IsMatch(value, @"^(?:InDev \d{8}|\d+\.\d+\.\d+(?:[-.][a-zA-Z0-9.]+)?)(?:\+[a-fA-F0-9]{7,64})?$") ? value : null;
    private static string? Error(string? value) => NpepDiagnosticValues.Error(value);
    private static JsonObject History(HostResponse? response)
    {
        var history = response?.Outcome == "Succeeded" ? response.ConnectionHistory : null;
        var events = new JsonArray();
        foreach (var item in history?.Events?.Where(e => e is not null).TakeLast(NpepDiagnosticValues.MaxEvents) ?? [])
        {
            var safe = NpepDiagnosticValues.Safe(item);
            events.Add(new JsonObject { ["id"] = Id(safe.Id), ["at"] = safe.At.ToString("O"), ["kind"] = safe.Kind,
                ["endpoint"] = safe.Endpoint, ["error"] = safe.Code, ["httpStatus"] = safe.HttpStatus,
                ["requestId"] = Id(safe.RequestId), ["elapsedMs"] = safe.ElapsedMs, ["retrying"] = safe.Retrying });
        }
        return new() { ["available"] = history is not null,
            ["persistence"] = history is null ? null : Choice(history.Persistence, "Stored Pending Unavailable Empty"), ["events"] = events };
    }
}

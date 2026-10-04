using System.Globalization;
using System.Text.Json.Nodes;

namespace NPEduTools.Integrations.Npep;

public interface INpepNoiseDisplay
{
    void BindDisplay(string? scope);
    JsonObject? ObserveDisplay();
    JsonObject? PendingDisplayReturn();
    void ConfirmDisplay(JsonObject request, JsonObject reply, double elapsedSeconds);
    void DisplayFailed(string code);
}

/// <summary>Independent 0.9 presence; these messages carry no capture command or browser credential.</summary>
public static class NpepNoiseDisplayProtocol
{
    public static bool Exact(JsonObject o, params string[] keys) => NpepNoiseManagementProtocol.Exact(o, keys);
    private static bool Id(JsonNode? n) => n is JsonValue v && v.TryGetValue<string>(out var s) &&
        Guid.TryParseExact(s, "D", out _) && s == s.ToLowerInvariant() && s[14] == '4' && "89ab".Contains(s[19]);
    private static bool Integer(JsonNode? n, long max) => n is JsonValue &&
        long.TryParse(n.ToJsonString(), NumberStyles.None, CultureInfo.InvariantCulture, out var x) && x >= 0 && x <= max;
    public static bool Utc(JsonNode? n) => n is JsonValue v && v.TryGetValue<string>(out var s) &&
        DateTimeOffset.TryParseExact(s, "yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out _);
    public static bool Window(JsonNode? n)
    {
        if (n is not JsonObject w || !Exact(w, "start", "end")) return false;
        return Calendar(w["start"], out var start) && Calendar(w["end"], out var end) && end > start;
    }
    private static bool Calendar(JsonNode? n, out DateTime value)
    {
        value = default;
        return n is JsonValue v && v.TryGetValue<string>(out var s) &&
            DateTime.TryParseExact(s, "yyyy-MM-dd'T'HH:mm:ss.fff", CultureInfo.InvariantCulture, DateTimeStyles.None, out value) && value.Year is >= 2000 and <= 9998;
    }
    public static void ValidateRequest(string action, JsonObject body)
    {
        string[] fields = ["requestId", "context", "instanceId", "revision", "captureSessionId", "window"];
        bool valid = action is "observe" or "return" && (Exact(body, fields) || action == "return" &&
            Exact(body, [..fields, "offlineStartedAt", "returnMinutes"]) && Utc(body["offlineStartedAt"]) &&
            Integer(body["returnMinutes"], 60) && body["returnMinutes"]!.GetValue<int>() >= 1) &&
            Id(body["requestId"]) && Id(body["instanceId"]) && Id(body["captureSessionId"]) &&
            Integer(body["revision"], 9007199254740991) && Window(body["window"]) && body["context"] is JsonObject;
        if (!valid) throw new NpepException("INVALID_RESPONSE");
        NpepNoiseProtocol.Validate("context", body["context"]!.AsObject());
    }
    public static void ValidateReply(JsonObject data, JsonObject request)
    {
        bool valid = Exact(data, "supported", "serverNow", "returnMinutes", "presence", "activeReturn") &&
            data["supported"] is JsonValue v && v.TryGetValue<bool>(out var supported) && supported && Utc(data["serverNow"]) &&
            Integer(data["returnMinutes"], 60) && data["returnMinutes"]!.GetValue<int>() >= 1 &&
            data["presence"] is JsonObject p && Exact(p, "state", "ageMs") &&
            p["state"] is JsonValue s && s.TryGetValue<string>(out var state) &&
            (state == "UNKNOWN" ? p["ageMs"] is null : state is "DISPLAY_VISIBLE" or "BLOCKED" or "RETURNING" or "HIDDEN" && Integer(p["ageMs"], 600000));
        if (!valid) throw new NpepException("INVALID_RESPONSE");
        if (data["activeReturn"] is null) return;
        if (data["activeReturn"] is not JsonObject r || !Exact(r, "requestId", "window", "startedAt", "expiresAt", "returnMinutes", "remainingSeconds") ||
            !Id(r["requestId"]) || !JsonNode.DeepEquals(r["window"], request["window"]) || !Utc(r["startedAt"]) || !Utc(r["expiresAt"]) ||
            !Integer(r["returnMinutes"], 60) || r["returnMinutes"]!.GetValue<int>() < 1 || !Integer(r["remainingSeconds"], 3600) ||
            DateTimeOffset.Parse(r.Text("expiresAt")) <= DateTimeOffset.Parse(r.Text("startedAt")) ||
            (DateTimeOffset.Parse(r.Text("expiresAt")) - DateTimeOffset.Parse(r.Text("startedAt"))).TotalMinutes > r["returnMinutes"]!.GetValue<int>())
            throw new NpepException("INVALID_RESPONSE");
    }
}

using System.Globalization;
using System.Text.Json.Nodes;

namespace NPEduTools.Integrations.Npep;

/// <summary>Strict independent 0.8 management capability; does not extend statistics 0.6 or schedule 0.7.</summary>
public static class NpepNoiseManagementProtocol
{
    public static bool Exact(JsonObject value, params string[] keys) => value.Count == keys.Length && keys.All(value.ContainsKey);
    private static bool Id(JsonNode? v) => v is JsonValue x && x.TryGetValue<string>(out var text) &&
        Guid.TryParseExact(text, "D", out _) && text == text.ToLowerInvariant() &&
        text[14] == '4' && "89ab".Contains(text[19]);
    private static bool Revision(JsonNode? v) => v is JsonValue &&
        long.TryParse(v.ToJsonString(), NumberStyles.None, CultureInfo.InvariantCulture, out var n) && n is >= 0 and <= 9007199254740991;
    public static void ValidateRequest(string action, JsonObject body)
    {
        bool valid = Id(body["requestId"]) && body["context"] is JsonObject;
        if (valid) NpepNoiseProtocol.Validate("context", body["context"]!.AsObject());
        if (action == "status")
        {
            valid &= Exact(body, "requestId", "context", "protection");
            if (body["protection"] is not JsonObject p) valid = false;
            else valid &= Exact(p, "instanceId", "revision", "sessionId", "window", "protected") &&
                Id(p["instanceId"]) && Revision(p["revision"]) && (p["sessionId"] is null || Id(p["sessionId"])) &&
                p["protected"] is JsonValue v && v.TryGetValue<bool>(out _) && Window(p["window"]);
        }
        else if (action == "authorize") valid &= Exact(body, "requestId", "context", "commandId", "instanceId", "revision", "sessionId") &&
            Id(body["commandId"]) && Id(body["instanceId"]) && Revision(body["revision"]) && Id(body["sessionId"]);
        else valid = false;
        if (!valid) throw new NpepException("INVALID_RESPONSE");
    }
    private static bool Window(JsonNode? w)
    {
        if (w is null) return true;
        if (w is not JsonObject o || !Exact(o, "start", "end")) return false;
        const string format = "yyyy-MM-dd'T'HH:mm:ss.fff";
        return o["start"] is JsonValue a && a.TryGetValue<string>(out var start) &&
            o["end"] is JsonValue b && b.TryGetValue<string>(out var end) &&
            DateTime.TryParseExact(start, format, CultureInfo.InvariantCulture, DateTimeStyles.None, out var s) &&
            DateTime.TryParseExact(end, format, CultureInfo.InvariantCulture, DateTimeStyles.None, out var e) &&
            e > s && s.Year >= 2000 && e.Year <= 9998;
    }
    public static void ValidateReply(string action, JsonObject data, JsonObject request)
    {
        bool valid = action == "status"
            ? Exact(data, "accepted") && data["accepted"] is JsonValue a && a.TryGetValue<bool>(out var accepted) && accepted
            : Exact(data, "authorized", "commandId", "instanceId", "revision", "sessionId") &&
                data["authorized"] is JsonValue b && b.TryGetValue<bool>(out var authorized) && authorized &&
                new[] { "commandId", "instanceId", "revision", "sessionId" }.All(k => JsonNode.DeepEquals(data[k], request[k]));
        if (!valid) throw new NpepException("INVALID_RESPONSE");
    }
}

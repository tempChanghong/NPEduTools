using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace NPEduTools.Integrations.Npep;

public static class NpepNoiseScheduleProtocol
{
    private static readonly JsonDocument Schema = JsonDocument.Parse(Assembly.GetExecutingAssembly().GetManifestResourceStream("npep.noiseSchedule.schema.json")!);
    public static void Validate(string definition, JsonObject value)
    {
        using var document = JsonDocument.Parse(value.ToJsonString());
        if (!NpepProtocol.Check(Schema.RootElement, Schema.RootElement.GetProperty("definitions").GetProperty(definition), document.RootElement, utf16: true))
            throw new NpepException("INVALID_RESPONSE");
        CheckCalendars(value);
    }
    private static void CheckCalendars(JsonNode? node)
    {
        if (node is JsonObject obj) { foreach (var field in obj) CheckCalendars(field.Value); }
        else if (node is JsonArray list) { foreach (var item in list) CheckCalendars(item); }
        else if (node is JsonValue v && v.TryGetValue<string>(out var text) && text.Length == 23 && text[10] == 'T')
        {
            if (!DateTime.TryParseExact(text, "yyyy-MM-dd'T'HH:mm:ss.fff", System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None, out var date) || date.Year is < 2000 or > 9998) throw new NpepException("INVALID_RESPONSE");
        }
    }
}

public interface INpepNoiseSchedules
{
    void Bind(string? scope, bool eligible);
    JsonObject Observe();
    void Confirm(JsonObject response);
}

using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
namespace NPEduTools.Integrations.Npep;
public static class NpepSeatingProtocol
{
    private static readonly JsonDocument Schema = JsonDocument.Parse(Assembly.GetExecutingAssembly().GetManifestResourceStream("npep.seating.schema.json")!);
    public static void Validate(string definition, JsonObject value)
    {
        using var document = JsonDocument.Parse(value.ToJsonString());
        if (!NpepProtocol.Check(Schema.RootElement, Schema.RootElement.GetProperty("definitions").GetProperty(definition), document.RootElement, utf16: true))
            throw new NpepException("INVALID_RESPONSE");
        if (definition == "manifestRequest" && value["observation"] is JsonObject o &&
            (o["visible"]!.GetValue<bool>() && (o["appliedHash"] is null || o["renderAgeMs"] is null) || o["appliedHash"] is null && o["revision"]!.GetValue<int>() != 0)) throw new NpepException("INVALID_RESPONSE");
        if (definition == "manifest" && value["asset"] is JsonObject a &&
            (value["revision"]!.GetValue<int>() < 1 || (long)a["width"]!.GetValue<int>() * a["height"]!.GetValue<int>() > 6000000 || a["chunks"]!.GetValue<int>() != (a["byteCount"]!.GetValue<int>() + 16383) / 16384))
            throw new NpepException("INVALID_RESPONSE");
    }
}

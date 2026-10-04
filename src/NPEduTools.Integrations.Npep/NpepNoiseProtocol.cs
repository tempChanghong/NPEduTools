using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace NPEduTools.Integrations.Npep;

public static class NpepNoiseProtocol
{
    private static readonly JsonDocument Schema = JsonDocument.Parse(Assembly.GetExecutingAssembly().GetManifestResourceStream("npep.noise.schema.json")!);
    public static void Validate(string definition, JsonObject value)
    {
        using var document = JsonDocument.Parse(value.ToJsonString());
        if (!NpepProtocol.Check(Schema.RootElement, Schema.RootElement.GetProperty("definitions").GetProperty(definition), document.RootElement, utf16: true, multiline: true))
            throw new NpepException("INVALID_RESPONSE");
    }
}

public interface INpepNoise
{
    void Bind(string? scope);
    JsonObject Observe();
    JsonArray Reports();
    JsonArray Receipts();
    void Acknowledge(JsonObject response);
    void Execute(JsonObject command, Action authorize);
}

/// <summary>Negotiated management boundary, separate from the unchanged 0.6 statistics wire contract.</summary>
public interface INpepNoiseManagement
{
    JsonObject ObserveProtection();
    bool RequiresManagement(JsonObject command);
    void ExecuteManaged(JsonObject command, Action authorize);
}

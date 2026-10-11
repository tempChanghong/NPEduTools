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
        if (definition == "exchangeRequest")
        {
            ValidateEstimate(value["status"]!.AsObject());
            foreach (var r in value["reports"]!.AsArray()) ValidateEstimate(r!.AsObject());
        }
        else if (definition is "status" or "report") ValidateEstimate(value);
    }
    private static void ValidateEstimate(JsonObject value)
    {
        if (value["estimate"] is not JsonObject e) return;
        var s = e["summary"]!.AsObject();
        double sampled = s["sampledSeconds"]!.GetValue<double>();
        double elapsed = value["summary"]?["elapsedSeconds"]?.GetValue<double>() ?? 0;
        double coverage = s["coverage"]!.GetValue<double>();
        bool current = e["currentDbA"] is not null;
        if (current != (e.Text("state") == "Active") || current && (e["reason"] is not null || value.Text("state") != "Active") ||
            (sampled > 0) != (s["energyMeanDbA"] is not null && s["maximumDbA"] is not null) ||
            sampled == 0 && (s["energyMeanDbA"] is not null || s["maximumDbA"] is not null) ||
            Math.Abs(coverage - (elapsed > 0 ? Math.Clamp(sampled / elapsed, 0, 1) : 0)) > 1e-6 ||
            s["energyMeanDbA"] is JsonValue mean && s["maximumDbA"] is JsonValue maximum && mean.GetValue<double>() > maximum.GetValue<double>() + 1e-8)
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

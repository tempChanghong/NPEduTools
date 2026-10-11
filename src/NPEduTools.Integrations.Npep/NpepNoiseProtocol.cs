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
            ValidateEstimate(document.RootElement.GetProperty("status"));
            foreach (var r in document.RootElement.GetProperty("reports").EnumerateArray()) ValidateEstimate(r);
        }
        else if (definition is "status" or "report") ValidateEstimate(document.RootElement);
    }
    private static double? NullableNumber(JsonElement value, string name) => value.GetProperty(name).ValueKind == JsonValueKind.Null ? null : value.GetProperty(name).GetDouble();
    private static void ValidateEstimate(JsonElement value)
    {
        if (value.TryGetProperty("study", out var study))
        {
            var window = study.GetProperty("window");
            bool startValid = DateTime.TryParseExact(window.GetProperty("start").GetString(), "yyyy-MM-dd'T'HH:mm:ss.fff", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var start);
            bool endValid = DateTime.TryParseExact(window.GetProperty("end").GetString(), "yyyy-MM-dd'T'HH:mm:ss.fff", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var end);
            if (!startValid || !endValid || start.Year < 2000 || end.Year > 9998 || end <= start || (end-start).TotalSeconds > 10800 ||
                study.GetProperty("clockSource").GetString() != (study.GetProperty("mode").GetString() == "Exam" ? "Windows" : "ClassIsland")) throw new NpepException("INVALID_RESPONSE");
            if (study.TryGetProperty("statistics", out var stats))
            {
                if (stats.GetProperty("sessionId").GetString() != value.GetProperty("sessionId").GetString() || stats.GetProperty("raw").GetProperty("unit").GetString() != "dBFS" ||
                    stats.GetProperty("estimated").ValueKind != JsonValueKind.Null && stats.GetProperty("estimated").GetProperty("unit").GetString() != "estimated-dB(A)") throw new NpepException("INVALID_RESPONSE");
                foreach (var channel in new[]{stats.GetProperty("raw"),stats.GetProperty("estimated")}.Where(c=>c.ValueKind!=JsonValueKind.Null))
                    if (channel.GetProperty("highSeconds").GetDouble() > channel.GetProperty("thresholdSampledSeconds").GetDouble()+1e-6 ||
                        channel.GetProperty("mediumSeconds").GetDouble() > channel.GetProperty("mediumThresholdSampledSeconds").GetDouble()+1e-6 ||
                        channel.GetProperty("thresholdSampledSeconds").GetDouble() > channel.GetProperty("sampledSeconds").GetDouble()+1e-6 ||
                        channel.GetProperty("mediumThresholdSampledSeconds").GetDouble() > channel.GetProperty("sampledSeconds").GetDouble()+1e-6 ||
                        (NullableNumber(channel,"energyMean") is null)!=(NullableNumber(channel,"maximum") is null) ||
                        NullableNumber(channel,"energyMean") > NullableNumber(channel,"maximum")+1e-6) throw new NpepException("INVALID_RESPONSE");
            }
        }
        if (value.TryGetProperty("summary", out var raw) && raw.ValueKind == JsonValueKind.Object)
        {
            double elapsedRaw = raw.GetProperty("elapsedSeconds").GetDouble(), sampledRaw = raw.GetProperty("sampledSeconds").GetDouble();
            double coverageRaw = raw.GetProperty("coverage").GetDouble(), clipped = raw.GetProperty("clippedPercent").GetDouble();
            long frames = raw.GetProperty("frames").GetInt64();
            double? meanRaw = NullableNumber(raw, "energyMeanDbfs"), peakRaw = NullableNumber(raw, "peakDbfs");
            if ((sampledRaw > 0) != (frames > 0) ||
                Math.Abs(coverageRaw - (elapsedRaw > 0 ? Math.Clamp(sampledRaw / elapsedRaw, 0, 1) : 0)) > 1e-6 ||
                (meanRaw is null) != (peakRaw is null) || meanRaw is not null && peakRaw < meanRaw - 1e-8 ||
                sampledRaw == 0 && (meanRaw is not null || peakRaw is not null || clipped != 0) ||
                clipped > 0 && (peakRaw is null || peakRaw < 20 * Math.Log10(.999) - 1e-8))
                throw new NpepException("INVALID_RESPONSE");
        }
        if (!value.TryGetProperty("estimate", out var e) || e.ValueKind != JsonValueKind.Object) return;
        var s = e.GetProperty("summary");
        double sampled = s.GetProperty("sampledSeconds").GetDouble();
        double elapsed = raw.ValueKind == JsonValueKind.Object ? raw.GetProperty("elapsedSeconds").GetDouble() : 0;
        double coverage = s.GetProperty("coverage").GetDouble();
        bool current = e.GetProperty("currentDbA").ValueKind != JsonValueKind.Null;
        double? mean = NullableNumber(s, "energyMeanDbA"), maximum = NullableNumber(s, "maximumDbA");
        if (current != (e.GetProperty("state").GetString() == "Active") || current && (e.GetProperty("reason").ValueKind != JsonValueKind.Null ||
            !value.TryGetProperty("state", out var state) || state.GetString() != "Active") ||
            (sampled > 0) != (mean is not null && maximum is not null) ||
            sampled == 0 && (mean is not null || maximum is not null) ||
            Math.Abs(coverage - (elapsed > 0 ? Math.Clamp(sampled / elapsed, 0, 1) : 0)) > 1e-6 ||
            mean > maximum + 1e-8)
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

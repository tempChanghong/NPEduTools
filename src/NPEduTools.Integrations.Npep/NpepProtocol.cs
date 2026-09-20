using System.Globalization;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace NPEduTools.Integrations.Npep;

public sealed class NpepException(string code, int status = 0, int? retryAfter = null) : Exception(code)
{
    public string Code { get; } = code;
    public int Status { get; } = status;
    public int? RetryAfterSeconds { get; } = retryAfter;
}

public static class NpepProtocol
{
    public const long MaxInteger = 9007199254740991;
    public static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    private static readonly JsonDocument Schema = JsonDocument.Parse(Assembly.GetExecutingAssembly().GetManifestResourceStream("npep.schema.json")!);
    public static string Id() => Guid.NewGuid().ToString("D");
    public static JsonObject Body() => new() { ["requestId"] = Id() };
    public static string Text(this JsonObject value, string name) => value[name] is JsonValue item && item.TryGetValue<string>(out var result) ? result : throw new NpepException("INVALID_RESPONSE");
    public static long Number(this JsonObject value, string name) => value[name] is JsonValue item && item.TryGetValue<long>(out var result) ? result : throw new NpepException("INVALID_RESPONSE");
    public static JsonObject Copy(this JsonObject value) => (JsonObject)value.DeepClone();
    public static bool Equal(JsonNode? a, JsonNode? b) => JsonNode.DeepEquals(a, b);

    public static JsonObject Parse(ReadOnlySpan<byte> bytes)
    {
        try
        {
            using var parsed = JsonDocument.Parse(bytes.ToArray(), new() { MaxDepth = 24 });
            RejectDuplicateKeys(parsed.RootElement);
            return JsonNode.Parse(parsed.RootElement.GetRawText()) as JsonObject ?? throw new NpepException("INVALID_RESPONSE");
        }
        catch (JsonException) { throw new NpepException("INVALID_RESPONSE"); }
    }

    // A deliberately bounded interpreter for the checked-in N1 schema vocabulary, not a general JSON Schema engine.
    public static void Validate(string definition, JsonObject value)
    {
        using var document = JsonDocument.Parse(value.ToJsonString(Json));
        if (!Check(Schema.RootElement.GetProperty("definitions").GetProperty(definition), document.RootElement))
            throw new NpepException("INVALID_RESPONSE");
    }
    private static bool Check(JsonElement schema, JsonElement value)
    {
        if (schema.TryGetProperty("$ref", out var reference)) return Check(Schema.RootElement.GetProperty("definitions").GetProperty(reference.GetString()![14..]), value);
        if (schema.TryGetProperty("anyOf", out var alternatives)) return alternatives.EnumerateArray().Any(s => Check(s, value));
        if (schema.TryGetProperty("const", out var constant) && !JsonElement.DeepEquals(constant, value)) return false;
        if (schema.TryGetProperty("enum", out var choices) && !choices.EnumerateArray().Any(c => JsonElement.DeepEquals(c, value))) return false;
        if (!schema.TryGetProperty("type", out var type)) return true;
        switch (type.GetString())
        {
            case "null": return value.ValueKind == JsonValueKind.Null;
            case "object":
                if (value.ValueKind != JsonValueKind.Object) return false;
                var properties = schema.GetProperty("properties");
                if (schema.GetProperty("required").EnumerateArray().Any(p => !value.TryGetProperty(p.GetString()!, out _))) return false;
                return value.EnumerateObject().All(p => properties.TryGetProperty(p.Name, out var s) && Check(s, p.Value));
            case "array":
                if (value.ValueKind != JsonValueKind.Array) return false;
                int count = value.GetArrayLength();
                if (schema.TryGetProperty("minItems", out var minItems) && count < minItems.GetInt32() ||
                    schema.TryGetProperty("maxItems", out var maxItems) && count > maxItems.GetInt32()) return false;
                if (schema.TryGetProperty("uniqueItems", out var unique) && unique.GetBoolean() &&
                    value.EnumerateArray().Select(x => x.GetRawText()).Distinct(StringComparer.Ordinal).Count() != count) return false;
                return value.EnumerateArray().All(x => Check(schema.GetProperty("items"), x));
            case "string":
                if (value.ValueKind != JsonValueKind.String) return false;
                string text = value.GetString()!;
                int length = text.EnumerateRunes().Count();
                if (schema.TryGetProperty("minLength", out var min) && length < min.GetInt32() ||
                    schema.TryGetProperty("maxLength", out var max) && length > max.GetInt32()) return false;
                if (schema.TryGetProperty("pattern", out var pattern) && !Regex.IsMatch(text, pattern.GetString()!, RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100))) return false;
                if (schema.TryGetProperty("format", out var format) && format.GetString() == "date-time" &&
                    !DateTimeOffset.TryParseExact(text, "yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out _)) return false;
                return !text.Any(char.IsControl);
            case "integer":
                if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt64(out long number)) return false;
                return (!schema.TryGetProperty("minimum", out var low) || number >= low.GetInt64()) &&
                    (!schema.TryGetProperty("maximum", out var high) || number <= high.GetInt64());
            default: throw new InvalidOperationException("Unsupported internal schema keyword.");
        }
    }
    private static void RejectDuplicateKeys(JsonElement item)
    {
        if (item.ValueKind == JsonValueKind.Object)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in item.EnumerateObject())
            {
                if (!seen.Add(property.Name)) throw new NpepException("INVALID_RESPONSE");
                RejectDuplicateKeys(property.Value);
            }
        }
        else if (item.ValueKind == JsonValueKind.Array) foreach (var child in item.EnumerateArray()) RejectDuplicateKeys(child);
    }
}

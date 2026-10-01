using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace NPEduTools.Integrations.Npep;

public static class NpepRuntimeProtocol
{
    private static readonly JsonDocument Schema = JsonDocument.Parse(Assembly.GetExecutingAssembly().GetManifestResourceStream("npep.runtime.schema.json")!);
    public static void Validate(string definition, JsonObject value)
    {
        using var document = JsonDocument.Parse(value.ToJsonString());
        if (!NpepProtocol.Check(Schema.RootElement, Schema.RootElement.GetProperty("definitions").GetProperty(definition), document.RootElement, utf16: true))
            throw new NpepException("INVALID_RESPONSE");
    }
    public static string UtcNow() => DateTimeOffset.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'");
}

public interface INpepRuntimeControl
{
    Task<JsonObject> ObserveAsync(CancellationToken token);
    Task ExecuteAsync(JsonObject operation, Func<bool, CancellationToken, Task> authorize, CancellationToken token);
    Task RecoverAsync(JsonObject operation, CancellationToken token);
    Task<JsonObject> ResultAsync(Guid operationId, CancellationToken token);
    bool LocallyEnded(Guid operationId);
    bool HasOperation(Guid operationId);
}

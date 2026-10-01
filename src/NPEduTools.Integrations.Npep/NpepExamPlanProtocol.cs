using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace NPEduTools.Integrations.Npep;

public static class NpepExamPlanProtocol
{
    private static readonly JsonDocument Schema = JsonDocument.Parse(Assembly.GetExecutingAssembly().GetManifestResourceStream("npep.plans.schema.json")!);
    public static void Validate(string definition, JsonObject value)
    {
        using var document = JsonDocument.Parse(value.ToJsonString());
        if (!NpepProtocol.Check(Schema.RootElement, Schema.RootElement.GetProperty("definitions").GetProperty(definition), document.RootElement, utf16: true, multiline: true))
            throw new NpepException("INVALID_RESPONSE");
    }
}

public interface INpepExamPlans
{
    // Includes revision, available, blockReason, player and preparedId; never paths/credentials.
    JsonObject Observe();
    Task<JsonObject> ExecuteAsync(JsonObject operation, bool start, Guid commandId, Action authorize, CancellationToken token);
}

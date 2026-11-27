using System.Text.Json.Nodes;
namespace NPEduTools.Integrations.Npep;
public sealed partial class NpepApi
{
    internal Task<JsonObject> SeatingAsync(string bearer, JsonObject body, bool chunk, CancellationToken token)
    {
        NpepSeatingProtocol.Validate(chunk ? "chunkRequest" : "manifestRequest", body);
        return StudyAsync("device/study-settings/seating" + (chunk ? "/chunk" : ""), chunk ? "chunk" : "manifest", bearer, body, token, "0.14", seatingSequence: !chunk);
    }
}
public sealed partial class NpepDevice
{
    internal Task<JsonObject> ExchangeSeatingManifestAsync(JsonObject context, Func<JsonObject> sample, CancellationToken token) =>
        WithBoundSchoolConnectionAsync(token, (api, bearer, requireCurrent) =>
        {
            // Sample presentation only after waiting for the device gate. There is no second gate before sending.
            var body = sample();
            requireCurrent();
            return api.SeatingAsync(bearer, body, false, token);
        }, studyContext: context);

    internal Task<JsonObject> ReadSeatingChunkAsync(JsonObject body, CancellationToken token) =>
        WithBoundSchoolConnectionAsync(token, (api, bearer, _) => api.SeatingAsync(bearer, body, true, token),
            studyContext: body["context"]?.AsObject() ?? throw new NpepException("INVALID_REQUEST"));
}

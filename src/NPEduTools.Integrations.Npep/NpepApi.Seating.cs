using System.Text.Json.Nodes;
namespace NPEduTools.Integrations.Npep;
public sealed partial class NpepApi
{
    internal Task<JsonObject> SeatingAsync(string bearer, JsonObject body, bool chunk, CancellationToken token)
    {
        NpepSeatingProtocol.Validate(chunk ? "chunkRequest" : "manifestRequest", body);
        return StudyAsync("device/study-settings/seating" + (chunk ? "/chunk" : ""), chunk ? "chunk" : "manifest", bearer, body, token, "0.14");
    }
}
public sealed partial class NpepDevice
{
    internal async Task<JsonObject> SeatingAsync(Func<JsonObject> body, bool chunk, CancellationToken token)
    {
        await _gate.WaitAsync(token);
        try { RequireNotificationConnection(); using var api = Api(); return await api.SeatingAsync(DeviceBearer, body(), chunk, token); }
        catch (NpepException e) { if (e.Status == 401) SuspendIfTerminal(e); throw; }
        finally { _gate.Release(); }
    }
}

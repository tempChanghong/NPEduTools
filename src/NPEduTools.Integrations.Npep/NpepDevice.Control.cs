using System.Text.Json.Nodes;

namespace NPEduTools.Integrations.Npep;

public sealed partial class NpepDevice
{
    internal async Task<JsonObject> ControlContextAsync(Guid epoch, CancellationToken token)
    {
        await _gate.WaitAsync(token);
        try
        {
            RequireNotificationConnection();
            if (_session is null) throw new NpepException("SESSION_SUPERSEDED");
            var r = (JsonObject)Required()["registration"]!;
            var identity = new JsonObject();
            foreach (var field in new[] { "serverInstanceId", "deploymentEpoch", "deviceId", "bindingRevision", "credentialGeneration" }) identity[field] = r[field]!.DeepClone();
            return new() { ["identity"] = identity, ["runId"] = _runId, ["sessionId"] = _session["sessionId"]!.DeepClone(),
                ["statusEpoch"] = _session["statusEpoch"]!.DeepClone(), ["controlEpoch"] = epoch.ToString("D") };
        }
        finally { _gate.Release(); }
    }

    internal async Task<JsonObject> ControlAsync(string path, string response, JsonObject? body, string? definition, CancellationToken token)
    {
        await _gate.WaitAsync(token);
        try { RequireNotificationConnection(); using var api = Api(); return await api.ControlAsync(path, response, DeviceBearer, body, definition, token); }
        catch (NpepException error) { if (error.Status == 401) SuspendIfTerminal(error); throw; }
        finally { _gate.Release(); }
    }
}

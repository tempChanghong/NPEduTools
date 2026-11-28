using System.Text;
using System.Text.Json.Nodes;

namespace NPEduTools.Integrations.Npep;

public sealed partial class NpepApi
{
    public async Task<JsonObject> ManagedUninstallAsync(string operation, JsonObject? body, string? bearer, CancellationToken token)
    {
        string path = operation switch
        {
            "check" => "device/managed-uninstall",
            "issue" => "device/managed-uninstall/issue",
            "redeem" => "device/managed-uninstall/redeem",
            _ => throw new ArgumentException(nameof(operation))
        };
        var origin = new Uri(Origin);
        if (origin.Scheme != "https") throw new NpepException("UNINSTALL_HTTPS_REQUIRED");
        string id = body?.Text("requestId") ?? NpepProtocol.Id();
        using var request = new HttpRequestMessage(body is null ? HttpMethod.Get : HttpMethod.Post, path);
        request.Headers.Add("X-NPEP-Version", "0.20");
        request.Headers.Add("X-Request-Id", id);
        if (bearer is not null) request.Headers.Authorization = new("Bearer", bearer);
        if (body is not null) request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(6));
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
        if ((int)response.StatusCode is >= 300 and < 400) throw new NpepException("REDIRECT_REJECTED");
        var wire = await ReadEnvelopeAsync(response, 65536, deadline.Token, request);
        if (wire.Text("protocolVersion") != "0.20" || wire.Text("requestId") != id)
            throw new NpepException("UNINSTALL_UPGRADE_REQUIRED");
        if (!response.IsSuccessStatusCode)
            throw new NpepException(wire["error"]?["code"]?.GetValue<string>() ?? "INVALID_RESPONSE", (int)response.StatusCode);
        if (wire["data"] is not JsonObject data) throw new NpepException("INVALID_RESPONSE");
        return data;
    }
}

public sealed partial class NpepDevice
{
    public Action<JsonObject>? BeforeUninstallBindingChange { private get; set; }

    internal string? LegacyUninstallFingerprint
    {
        get
        {
            var original = _legacyRecoveryCandidate;
            var current = _saved;
            return original is not null && !_suspended && current?["stage"]?.GetValue<string>() == "ACTIVE" &&
                JsonNode.DeepEquals(original["registration"], current["registration"]) &&
                JsonNode.DeepEquals(original["origin"], current["origin"]) &&
                JsonNode.DeepEquals(original["credentialId"], current["credentialId"])
                ? _vault.LoadedFingerprint : null;
        }
    }

    public async Task<JsonObject> ManagedUninstallAsync(string operation, Guid installId, string? policyVersion, CancellationToken token)
    {
        JsonObject pairing;
        NpepApi api;
        string bearer;
        await _gate.WaitAsync(token);
        try
        {
            pairing = Required();
            if (_suspended || pairing.Text("stage") != "ACTIVE") throw new NpepException("UNINSTALL_ORIGINAL_BINDING_REQUIRED");
            api = Api();
            bearer = DeviceBearer;
        }
        finally { _gate.Release(); }
        using (api)
        {
            var body = operation == "issue" ? new JsonObject
            {
                ["requestId"] = NpepProtocol.Id(), ["installId"] = installId.ToString("D"), ["policyVersion"] = policyVersion
            } : null;
            var result = await api.ManagedUninstallAsync(operation, body, bearer, token);
            await _gate.WaitAsync(token);
            try
            {
                if (_suspended || !ReferenceEquals(pairing, _saved)) throw new NpepException("UNINSTALL_BINDING_CHANGED");
            }
            finally { _gate.Release(); }
            return result;
        }
    }
}

public sealed partial class NpepRuntime
{
    public string? LegacyUninstallFingerprint => _device?.LegacyUninstallFingerprint;
    public JsonObject ReadUninstallBinding() => _device?.View() ?? throw new NpepException("UNINSTALL_ORIGINAL_BINDING_REQUIRED");
    public Task<JsonObject> ManagedUninstallAsync(string operation, Guid installId, string? policyVersion, CancellationToken token) =>
        _device?.ManagedUninstallAsync(operation, installId, policyVersion, token) ??
        throw new NpepException("UNINSTALL_ORIGINAL_BINDING_REQUIRED");
}

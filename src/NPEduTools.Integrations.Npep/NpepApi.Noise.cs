using System.Net.Http.Headers;
using System.Text.Json.Nodes;

namespace NPEduTools.Integrations.Npep;

public sealed partial class NpepApi
{
    internal async Task<JsonObject> NoiseAsync(string bearer, JsonObject body, CancellationToken token)
    {
        NpepNoiseProtocol.Validate("exchangeRequest", body);
        using var request = new HttpRequestMessage(HttpMethod.Post, "device/noise-exchange");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        request.Headers.Add("X-NPEP-Version", "0.6"); request.Headers.Add("X-Request-Id", body.Text("requestId"));
        var bytes = System.Text.Encoding.UTF8.GetBytes(body.ToJsonString());
        if (bytes.Length > 65536) throw new NpepException("PAYLOAD_TOO_LARGE");
        request.Content = new ByteArrayContent(bytes); request.Content.Headers.ContentType = new("application/json");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(8));
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
        int status = (int)response.StatusCode;
        if (status is >= 300 and < 400) throw new NpepException("REDIRECT_REJECTED");
        if (status is 404 or 426) throw new NpepException("NOISE_UNSUPPORTED", status);
        if (response.Content.Headers.ContentLength > 65536) throw new NpepException("INVALID_RESPONSE");
        using var stream = await response.Content.ReadAsStreamAsync(deadline.Token);
        using var buffer = new MemoryStream(); var block = new byte[4096]; int read;
        while ((read = await stream.ReadAsync(block, deadline.Token)) > 0)
        { if (buffer.Length + read > 65536) throw new NpepException("INVALID_RESPONSE"); buffer.Write(block, 0, read); }
        var envelope = NpepProtocol.Parse(buffer.ToArray());
        if (envelope.Text("protocolVersion") != "0.6" || envelope.Text("requestId") != body.Text("requestId")) throw new NpepException("INVALID_RESPONSE");
        if (!response.IsSuccessStatusCode)
        {
            string code = envelope["error"]?["code"]?.GetValue<string>() ?? "INVALID_RESPONSE";
            throw new NpepException(code.Length <= 100 ? code : "INVALID_RESPONSE", status);
        }
        NpepNoiseProtocol.Validate("exchangeEnvelope", envelope);
        return (JsonObject)envelope["data"]!.DeepClone();
    }
}

public sealed partial class NpepDevice
{
    internal async Task<JsonObject> NoiseAsync(JsonObject body, CancellationToken token)
    {
        await _gate.WaitAsync(token);
        try { RequireNotificationConnection(); using var api = Api(); return await api.NoiseAsync(DeviceBearer, body, token); }
        catch (NpepException error) { if (error.Status == 401) SuspendIfTerminal(error); throw; }
        finally { _gate.Release(); }
    }
}

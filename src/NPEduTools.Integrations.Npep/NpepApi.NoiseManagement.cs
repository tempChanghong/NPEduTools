using System.Net.Http.Headers;
using System.Text.Json.Nodes;

namespace NPEduTools.Integrations.Npep;

public sealed partial class NpepApi
{
    internal async Task<JsonObject> NoiseManagementAsync(string bearer, string action, JsonObject body, CancellationToken token)
    {
        NpepNoiseManagementProtocol.ValidateRequest(action, body);
        using var request = new HttpRequestMessage(HttpMethod.Post, "device/noise-management/" + action);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        request.Headers.Add("X-NPEP-Version", "0.8"); request.Headers.Add("X-Request-Id", body.Text("requestId"));
        byte[] bytes = System.Text.Encoding.UTF8.GetBytes(body.ToJsonString());
        if (bytes.Length > 8192) throw new NpepException("PAYLOAD_TOO_LARGE");
        request.Content = new ByteArrayContent(bytes); request.Content.Headers.ContentType = new("application/json");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token); deadline.CancelAfter(TimeSpan.FromSeconds(5));
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
        int status = (int)response.StatusCode;
        if (status is >= 300 and < 400) throw new NpepException("REDIRECT_REJECTED");
        if (status is 404 or 426) throw new NpepException("MANAGEMENT_UNSUPPORTED", status);
        if (response.Content.Headers.ContentLength > 8192) throw new NpepException("INVALID_RESPONSE");
        using var stream = await response.Content.ReadAsStreamAsync(deadline.Token);
        using var buffer = new MemoryStream(); byte[] block = new byte[2048]; int read;
        while ((read = await stream.ReadAsync(block, deadline.Token)) > 0)
        { if (buffer.Length + read > 8192) throw new NpepException("INVALID_RESPONSE"); buffer.Write(block, 0, read); }
        var envelope = NpepProtocol.Parse(buffer.ToArray());
        if (envelope.Text("protocolVersion") != "0.8" || envelope.Text("requestId") != body.Text("requestId")) throw new NpepException("INVALID_RESPONSE");
        if (!response.IsSuccessStatusCode)
        {
            if (!NpepNoiseManagementProtocol.Exact(envelope, "protocolVersion", "requestId", "serverTime", "error") ||
                envelope["error"] is not JsonObject error || !NpepNoiseManagementProtocol.Exact(error, "code", "message", "retryAfterSeconds") ||
                error["code"] is not JsonValue codeValue || !codeValue.TryGetValue<string>(out var code) ||
                !System.Text.RegularExpressions.Regex.IsMatch(code, "^[A-Z][A-Z0-9_]{0,99}$") ||
                error["message"] is not JsonValue message || !message.TryGetValue<string>(out var text) || text.Length > 1000 ||
                error["retryAfterSeconds"] is { } retry && (!int.TryParse(retry.ToJsonString(), out int seconds) || seconds is < 1 or > 3600))
                throw new NpepException("INVALID_RESPONSE");
            throw new NpepException(code, status);
        }
        if (!NpepNoiseManagementProtocol.Exact(envelope, "protocolVersion", "requestId", "serverTime", "data") || envelope["data"] is not JsonObject data ||
            envelope["serverTime"] is not JsonValue v || !v.TryGetValue<string>(out var stamp) ||
            !DateTimeOffset.TryParseExact(stamp, "yyyy-MM-dd'T'HH:mm:ss.fff'Z'", System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AssumeUniversal, out _)) throw new NpepException("INVALID_RESPONSE");
        NpepNoiseManagementProtocol.ValidateReply(action, data, body);
        return data.Copy();
    }
}
public sealed partial class NpepDevice
{
    internal async Task<JsonObject> NoiseManagementAsync(string action, JsonObject body, CancellationToken token)
    {
        await _gate.WaitAsync(token);
        try { RequireNotificationConnection(); using var api = Api(); return await api.NoiseManagementAsync(DeviceBearer, action, body, token); }
        catch (NpepException error) { if (error.Status == 401) SuspendIfTerminal(error); throw; }
        finally { _gate.Release(); }
    }
}

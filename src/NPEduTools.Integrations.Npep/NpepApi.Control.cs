using System.Net.Http.Headers;
using System.Text.Json.Nodes;

namespace NPEduTools.Integrations.Npep;

public sealed partial class NpepApi
{
    internal async Task<JsonObject> ControlAsync(string path, string responseDefinition, string bearer,
        JsonObject? body, string? requestDefinition, CancellationToken token)
    {
        if (path is not ("device/runtime-control-policy" or "device/runtime-status" or "device/runtime-operations" or "device/runtime-operation-events") &&
            !System.Text.RegularExpressions.Regex.IsMatch(path, "^device/runtime-operations/[0-9a-f-]{36}/(start|resolve)$")) throw new NpepException("INVALID_ENDPOINT");
        if (body is not null) NpepRuntimeProtocol.Validate(requestDefinition!, body);
        string id = body?.Text("requestId") ?? NpepProtocol.Id();
        using var request = new HttpRequestMessage(body is null ? HttpMethod.Get : HttpMethod.Post, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        request.Headers.Add("X-NPEP-Version", "0.4"); request.Headers.Add("X-Request-Id", id);
        if (body is not null)
        {
            byte[] bytes = System.Text.Encoding.UTF8.GetBytes(body.ToJsonString());
            if (bytes.Length > 65536) throw new NpepException("PAYLOAD_TOO_LARGE");
            request.Content = new ByteArrayContent(bytes); request.Content.Headers.ContentType = new("application/json");
        }
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(10));
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
        int status = (int)response.StatusCode;
        if (status is >= 300 and < 400) throw new NpepException("REDIRECT_REJECTED");
        if (status is 404 or 426) throw new NpepException("RUNTIME_UNSUPPORTED", status);
        if (response.Content.Headers.ContentLength > 65536) throw new NpepException("INVALID_RESPONSE");
        using var stream = await response.Content.ReadAsStreamAsync(deadline.Token);
        using var buffer = new MemoryStream(); byte[] block = new byte[4096]; int read;
        while ((read = await stream.ReadAsync(block, deadline.Token)) > 0)
        {
            if (buffer.Length + read > 65536) throw new NpepException("INVALID_RESPONSE");
            buffer.Write(block, 0, read);
        }
        var envelope = NpepProtocol.Parse(buffer.ToArray());
        NpepRuntimeProtocol.Validate(response.IsSuccessStatusCode ? responseDefinition + "Envelope" : "errorEnvelope", envelope);
        if (envelope.Text("requestId") != id) throw new NpepException("INVALID_RESPONSE");
        if (!response.IsSuccessStatusCode)
        {
            var error = (JsonObject)envelope["error"]!;
            throw new NpepException(error.Text("code"), status, error["retryAfterSeconds"]?.GetValue<int>());
        }
        return (JsonObject)envelope["data"]!.DeepClone();
    }
}

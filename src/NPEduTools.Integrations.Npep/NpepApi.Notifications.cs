using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;

namespace NPEduTools.Integrations.Npep;

public sealed partial class NpepApi
{
    internal async Task<JsonObject> NotificationsAsync(string bearer, string? cursor, JsonObject? receipt, CancellationToken token)
    {
        if (cursor is { Length: > 200 } || (receipt is not null && cursor is not null)) throw new NpepException("INVALID_RESPONSE");
        string path = receipt is null ? "device/notifications" : "device/notification-receipts";
        if (cursor is not null) path += "?cursor=" + Uri.EscapeDataString(cursor);
        string requestId = receipt?.Text("requestId") ?? NpepProtocol.Id();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(10));
        using var request = new HttpRequestMessage(receipt is null ? HttpMethod.Get : HttpMethod.Post, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        request.Headers.Add("X-NPEP-Version", "0.2"); request.Headers.Add("X-Request-Id", requestId);
        if (receipt is not null)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(receipt.ToJsonString(NpepProtocol.Json));
            if (bytes.Length > 65536) throw new NpepException("PAYLOAD_TOO_LARGE");
            request.Content = new ByteArrayContent(bytes);
            request.Content.Headers.ContentType = new("application/json");
        }
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
        int status = (int)response.StatusCode;
        if (status is >= 300 and < 400) throw new NpepException("REDIRECT_REJECTED");
        if (status is 404 or 426) throw new NpepException("NOTIFICATIONS_UNSUPPORTED", status);
        if (response.Content.Headers.ContentLength > 1048576) throw new NpepException("INVALID_RESPONSE");
        using var stream = await response.Content.ReadAsStreamAsync(deadline.Token);
        using var buffer = new MemoryStream(); byte[] block = new byte[8192]; int read;
        while ((read = await stream.ReadAsync(block, deadline.Token)) > 0)
        {
            if (buffer.Length + read > 1048576) throw new NpepException("INVALID_RESPONSE");
            buffer.Write(block, 0, read);
        }
        var envelope = NpepProtocol.Parse(buffer.ToArray());
        if (envelope.Text("requestId") != requestId || envelope.Text("protocolVersion") != "0.2") throw new NpepException("INVALID_RESPONSE");
        if (!response.IsSuccessStatusCode)
        {
            var error = envelope["error"] as JsonObject ?? throw new NpepException("INVALID_RESPONSE");
            int? retry = error["retryAfterSeconds"]?.GetValue<int>();
            if (response.Headers.RetryAfter?.Delta is { } delta) retry = (int)Math.Clamp(delta.TotalSeconds, 1, 3600);
            string code = error.Text("code");
            if (code.Length is 0 or > 120) throw new NpepException("INVALID_RESPONSE");
            throw new NpepException(code, status, retry);
        }
        return envelope["data"] as JsonObject ?? throw new NpepException("INVALID_RESPONSE");
    }
}

using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;

namespace NPEduTools.Integrations.Npep;

public sealed class NpepApi : IDisposable
{
    private readonly HttpClient _http;
    public string Origin { get; }
    public NpepApi(string origin) : this(origin, new SocketsHttpHandler { AllowAutoRedirect = false, UseCookies = false }) { }
    internal NpepApi(string origin, HttpMessageHandler handler)
    {
        Origin = ValidateOrigin(origin);
        _http = new(handler) { BaseAddress = new Uri(Origin + "/api/v2/npep/"), Timeout = TimeSpan.FromSeconds(10) };
    }
    public static string ValidateOrigin(string origin)
    {
        if (!Uri.TryCreate(origin, UriKind.Absolute, out var uri) || uri.Scheme != "https" ||
            uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0 || uri.AbsolutePath != "/" ||
            origin.Contains('\\') || string.IsNullOrWhiteSpace(uri.Host)) throw new NpepException("INVALID_SERVER_ORIGIN");
        return uri.GetLeftPart(UriPartial.Authority);
    }
    public async Task<JsonObject> SendAsync(string path, string responseDefinition, JsonObject? body = null,
        string? requestDefinition = null, string? bearer = null, CancellationToken token = default)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(10));
        token = deadline.Token;
        // Callers cannot turn this adapter into an arbitrary authenticated URL fetcher.
        if (path is not ("info" or "pairings" or "device/me" or "device/sessions" or "device/status" or "device/revoke") &&
            !System.Text.RegularExpressions.Regex.IsMatch(path, "^pairings/[0-9a-f-]{36}(/confirm|/cancel)?$")) throw new NpepException("INVALID_ENDPOINT");
        if (body is not null) NpepProtocol.Validate(requestDefinition ?? throw new ArgumentNullException(nameof(requestDefinition)), body);
        string requestId = body?.Text("requestId") ?? NpepProtocol.Id();
        using var request = new HttpRequestMessage(body is null ? HttpMethod.Get : HttpMethod.Post, path);
        request.Headers.Add("X-NPEP-Version", "0.1");
        request.Headers.Add("X-Request-Id", requestId);
        if (bearer is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        if (body is not null)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(body.ToJsonString(NpepProtocol.Json));
            if (bytes.Length > 16384) throw new NpepException("PAYLOAD_TOO_LARGE");
            request.Content = new ByteArrayContent(bytes);
            request.Content.Headers.ContentType = new("application/json");
        }
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
        if ((int)response.StatusCode is >= 300 and < 400) throw new NpepException("REDIRECT_REJECTED");
        if (response.Content.Headers.ContentLength > 65536) throw new NpepException("INVALID_RESPONSE");
        using var stream = await response.Content.ReadAsStreamAsync(token);
        using var buffer = new MemoryStream();
        var block = new byte[4096];
        int read;
        while ((read = await stream.ReadAsync(block, token)) > 0)
        {
            if (buffer.Length + read > 65536) throw new NpepException("INVALID_RESPONSE");
            buffer.Write(block, 0, read);
        }
        JsonObject envelope = NpepProtocol.Parse(buffer.ToArray());
        if (envelope.Text("requestId") != requestId || envelope.Text("protocolVersion") != "0.1")
            throw new NpepException("INVALID_RESPONSE");
        if (!response.IsSuccessStatusCode)
        {
            NpepProtocol.Validate("error", envelope);
            var error = (JsonObject)envelope["error"]!;
            int? retry = error["retryAfterSeconds"]?.GetValue<int>();
            if (response.Headers.RetryAfter?.Delta is { } delta) retry = Math.Clamp((int)Math.Ceiling(delta.TotalSeconds), 1, 3600);
            throw new NpepException(error.Text("code"), (int)response.StatusCode, retry);
        }
        if (responseDefinition == "pairing")
        {
            string state = envelope["data"] is JsonObject data ? data.Text("state") : throw new NpepException("INVALID_RESPONSE");
            responseDefinition = state switch { "PENDING" => "pendingPairingResponse", "APPROVED" => "approvedPairingResponse", "ACTIVATED" => "activatedPairingResponse", _ => throw new NpepException("INVALID_RESPONSE") };
        }
        NpepProtocol.Validate(responseDefinition, envelope);
        return (JsonObject)envelope["data"]!.DeepClone();
    }
    public void Dispose() => _http.Dispose();
}

using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;

namespace NPEduTools.Integrations.Npep;

public sealed partial class NpepApi : IDisposable
{
    private readonly HttpClient _http;
    public string Origin { get; }
    public NpepApi(string origin) : this(origin, new SchoolTransport()) { }

    // Each API client owns its observations and headers. Connections to the same origin
    // can outlive one poll; periodic renewal picks up changes to the school's DNS.
    private sealed class SchoolTransport : HttpMessageHandler
    {
        private static readonly HttpMessageInvoker Pool = new(new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseCookies = false,
            PooledConnectionLifetime = TimeSpan.FromMinutes(2)
        });

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) =>
            Pool.SendAsync(request, token);
    }

    internal NpepApi(string origin, HttpMessageHandler handler)
    {
        Origin = ValidateOrigin(origin);
        _http = new(new ObserveHandler(handler, item => Observation?.Invoke(item))) { BaseAddress = new Uri(Origin + "/api/v2/npep/"), Timeout = TimeSpan.FromSeconds(10) };
    }
    public static string ValidateOrigin(string origin)
    {
        if (!Uri.TryCreate(origin, UriKind.Absolute, out var uri) || uri.Scheme is not ("https" or "http") ||
            uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0 || uri.AbsolutePath != "/" ||
            origin.Contains('\\') || string.IsNullOrWhiteSpace(uri.Host)) throw new NpepException("INVALID_SERVER_ORIGIN");
        return uri.GetLeftPart(UriPartial.Authority);
    }
    public Task<JsonObject> SendAsync(string path, string responseDefinition, JsonObject? body = null,
        string? requestDefinition = null, string? bearer = null, CancellationToken token = default) =>
        SendCoreAsync(path, responseDefinition, body, requestDefinition, bearer, token, currentDeviceLink: false);

    internal Task<JsonObject> SendDeviceLinkAsync(string path, string responseDefinition, string bearer, CancellationToken token,
        JsonObject? body = null, string? requestDefinition = null) =>
        SendCoreAsync(path, responseDefinition, body, requestDefinition, bearer, token, currentDeviceLink: true);

    private async Task<JsonObject> SendCoreAsync(string path, string responseDefinition, JsonObject? body,
        string? requestDefinition, string? bearer, CancellationToken token, bool currentDeviceLink)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(10));
        // Callers cannot turn this adapter into an arbitrary authenticated URL fetcher.
        if (path is not ("info" or "pairings" or "pairings/claim" or "pairings/apply" or "pairings/password" or "device/me" or "device/sessions" or "device/status" or "device/revoke") &&
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
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
        int status = (int)response.StatusCode;
        if (status is >= 300 and < 400)
        {
            // Never follow a redirect. An existing device may probe only its original origin on its normal backoff.
            throw currentDeviceLink ? new NpepException("DEVICE_RESPONSE_UNCONFIRMED", status) : new NpepException("REDIRECT_REJECTED");
        }
        try { return await ReadN1ResponseAsync(response, request, responseDefinition, deadline.Token); }
        catch (NpepException error) when (currentDeviceLink &&
            (response.IsSuccessStatusCode && error.Code == "INVALID_RESPONSE" ||
             status == 408 && (error is { Status: 0, Code: "INVALID_RESPONSE" } || !NpepDevice.TerminalConnectionError(error))))
        {
            // An unusable reply says nothing about whether the original device is still authorized.
            throw new NpepException("DEVICE_RESPONSE_UNCONFIRMED", status, RetryAfterSeconds(response, error.RetryAfterSeconds));
        }
        catch (OperationCanceledException) when (currentDeviceLink && !token.IsCancellationRequested && deadline.IsCancellationRequested &&
            status is 401 or 403 or 408 or 429 or >= 500 and <= 599)
        { throw status == 408 ? new NpepException("DEVICE_RESPONSE_UNCONFIRMED", status, RetryAfterSeconds(response)) : UnreadableResponse(response); }
    }

    private static async Task<JsonObject> ReadN1ResponseAsync(HttpResponseMessage response, HttpRequestMessage request,
        string responseDefinition, CancellationToken token)
    {
        var envelope = await ReadEnvelopeAsync(response, 65536, token, request);
        if (!response.IsSuccessStatusCode)
        {
            ValidateResponse(response, () => NpepProtocol.Validate("error", envelope));
            var error = (JsonObject)envelope["error"]!;
            throw new NpepException(error.Text("code"), (int)response.StatusCode,
                RetryAfterSeconds(response, error["retryAfterSeconds"]?.GetValue<int>()));
        }
        if (responseDefinition == "pairing")
        {
            string state = envelope["data"] is JsonObject data ? data.Text("state") : throw new NpepException("INVALID_RESPONSE");
            responseDefinition = state switch { "PENDING" => "pendingPairingResponse", "APPROVED" => "approvedPairingResponse", "ACTIVATED" => "activatedPairingResponse", "CANCELLED" => "cancelledPairingResponse", _ => throw new NpepException("INVALID_RESPONSE") };
        }
        NpepProtocol.Validate(responseDefinition, envelope);
        return (JsonObject)envelope["data"]!.DeepClone();
    }
    public void Dispose() => _http.Dispose();
}

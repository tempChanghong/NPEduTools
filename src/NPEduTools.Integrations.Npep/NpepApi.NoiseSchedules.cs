using System.Net.Http.Headers;
using System.Text.Json.Nodes;

namespace NPEduTools.Integrations.Npep;

public sealed partial class NpepApi
{
    internal async Task<JsonObject> NoiseScheduleAsync(string bearer, JsonObject body, CancellationToken token)
    {
        NpepNoiseScheduleProtocol.Validate("exchangeRequest", body);
        using var request = new HttpRequestMessage(HttpMethod.Post, "device/noise-schedule");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        request.Headers.Add("X-NPEP-Version", "0.7"); request.Headers.Add("X-Request-Id", body.Text("requestId"));
        var bytes = System.Text.Encoding.UTF8.GetBytes(body.ToJsonString());
        if (bytes.Length > 65536) throw new NpepException("PAYLOAD_TOO_LARGE");
        request.Content = new ByteArrayContent(bytes); request.Content.Headers.ContentType = new("application/json");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token); deadline.CancelAfter(TimeSpan.FromSeconds(8));
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
        int status = (int)response.StatusCode;
        if (status is >= 300 and < 400) throw new NpepException("REDIRECT_REJECTED");
        if (status is 404 or 426) throw new NpepException("NOISE_SCHEDULE_UNSUPPORTED", status);
        JsonObject envelope;
        try { envelope = await ReadEnvelopeAsync(response, 65536, deadline.Token, request); }
        catch (OperationCanceledException) when (!token.IsCancellationRequested && status is 429 or >= 500 and <= 599)
        { throw UnreadableResponse(response); }
        if (envelope.Text("protocolVersion") != "0.7" || envelope.Text("requestId") != body.Text("requestId"))
            throw UnreadableResponse(response);
        if (!response.IsSuccessStatusCode)
        {
            if (envelope["error"] is not JsonObject error || error["code"] is not JsonValue codeValue ||
                !codeValue.TryGetValue<string>(out string? code) || code.Length is < 1 or > 100)
                throw UnreadableResponse(response);
            int? delay = error["retryAfterSeconds"] is JsonValue retry && retry.TryGetValue<int>(out int seconds) &&
                seconds is >= 1 and <= 3600 ? seconds : null;
            throw new NpepException(code, status, RetryAfterSeconds(response, delay));
        }
        NpepNoiseScheduleProtocol.Validate("exchangeEnvelope", envelope);
        return envelope["data"]!.AsObject().DeepClone().AsObject();
    }
}

public sealed partial class NpepDevice
{
    internal Task<JsonObject> NoiseScheduleAsync(JsonObject body, Action requireOriginalConnection, CancellationToken token) =>
        WithBoundSchoolConnectionAsync(token, (api, bearer, requireCurrent) =>
        {
            requireOriginalConnection();
            requireCurrent();
            return api.NoiseScheduleAsync(bearer, body, token);
        }, studyContext: body["context"]?.AsObject() ?? throw new NpepException("INVALID_REQUEST"));
}

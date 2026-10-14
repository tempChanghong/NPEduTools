using System.Net;
using System.Net.Http.Headers;
using System.Text.Json.Nodes;
using NPEduTools.Integrations.Npep;

namespace NPEduTools.Npep.Tests;

public sealed class TransientHttpResponseTests
{
    public static IEnumerable<object[]> HtmlErrors =>
        from channel in new[] { "status", "notifications", "control", "plans", "study", "management" }
        from status in new[] { 200, 401, 403, 429, 500, 502, 503 }
        from body in new[] { "html", "empty", "oversized", "stream-oversized" }
        select new object[] { channel, status, body };

    [Theory, MemberData(nameof(HtmlErrors))]
    public async Task UnreadableTransientErrorBodyPreservesHttpFailureAndStrictSuccessParsing(string channel, int status, string body)
    {
        using var api = new NpepApi("https://isolated.invalid", new Handler(_ =>
        {
            string text = body switch { "empty" => "", "oversized" or "stream-oversized" => new string('x', 1048577), _ => "<html>private gateway details</html>" };
            var response = new HttpResponseMessage((HttpStatusCode)status)
                { Content = body == "stream-oversized" ? new UnboundedContent(System.Text.Encoding.UTF8.GetBytes(text)) : new StringContent(text) };
            response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(17));
            return Task.FromResult(response);
        }));
        Task<JsonObject> Request() => channel switch
        {
            "status" => api.SendAsync("info", "infoResponse"),
            "notifications" => api.NotificationsAsync("synthetic-token", null, null, CancellationToken.None),
            "control" => api.ControlAsync("device/runtime-operations", "deviceOperations", "synthetic-token", null, null, CancellationToken.None),
            "plans" => api.PlanAsync("device/exam-plans", "plans", "synthetic-token", null, null, CancellationToken.None),
            "study" => api.StudySettingsAsync("synthetic-token", CancellationToken.None),
            _ => api.SchoolManagementAsync("synthetic-token", "device/school-management", null, CancellationToken.None)
        };
        var error = await Assert.ThrowsAsync<NpepException>(Request);
        bool transient = status == 429 || status >= 500;
        bool refused = status is 401 or 403;
        Assert.Equal(refused ? "AUTH_INVALID" : transient ? status == 429 ? "RATE_LIMITED" : "TEMPORARILY_UNAVAILABLE" : "INVALID_RESPONSE", error.Code);
        Assert.Equal(transient || refused ? status : 0, error.Status);
        Assert.Equal(transient ? 17 : (int?)null, error.RetryAfterSeconds);
        Assert.DoesNotContain("private", error.Message);
    }

    [Fact]
    public async Task ValidStructuredRejectionIsNotReplacedByGenericGatewayFailure()
    {
        using var api = new NpepApi("https://isolated.invalid", new Handler(request =>
            Task.FromResult(Fixtures.Reply(request, new(), "INSTANCE_MISMATCH", 503))));
        var error = await Assert.ThrowsAsync<NpepException>(() => api.SendAsync("info", "infoResponse"));
        Assert.Equal("INSTANCE_MISMATCH", error.Code);
        Assert.Equal(503, error.Status);
    }
    public static IEnumerable<object[]> InvalidEnvelopes =>
        from channel in new[] { "status", "study", "notifications", "control", "plans", "management" }
        from status in new[] { 200, 401, 403, 409, 429, 503 }
        from defect in new[] { "request-id", "protocol-version", "missing-error", "bad-code", "bad-retry" }
        select new object[] { channel, status, defect };

    [Theory, MemberData(nameof(InvalidEnvelopes))]
    public async Task Invalid_structured_response_is_discarded_but_received_http_refusal_is_preserved(string channel, int status, string defect)
    {
        using var api = new NpepApi("https://isolated.invalid", new Handler(request =>
        {
            var envelope = new JsonObject { ["protocolVersion"] = request.Headers.GetValues("X-NPEP-Version").Single(),
                ["requestId"] = request.Headers.GetValues("X-Request-Id").Single(), ["serverTime"] = "2026-10-14T12:00:00.000Z",
                ["error"] = new JsonObject { ["code"] = "TEMPORARILY_UNAVAILABLE", ["message"] = "private", ["retryAfterSeconds"] = 17 } };
            if (defect == "request-id") envelope["requestId"] = NpepProtocol.Id();
            if (defect == "protocol-version") envelope["protocolVersion"] = "0.99";
            if (defect == "missing-error") envelope.Remove("error");
            if (defect == "bad-code") envelope["error"]!["code"] = new JsonObject { ["secret"] = "private" };
            if (defect == "bad-retry") envelope["error"]!["retryAfterSeconds"] = "private";
            var response = new HttpResponseMessage((HttpStatusCode)status) { Content = new StringContent(envelope.ToJsonString()) };
            response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(17));
            return Task.FromResult(response);
        }));
        Task<JsonObject> Request() => channel switch {
            "status" => api.SendAsync("info", "infoResponse"),
            "study" => api.StudySettingsAsync("synthetic", CancellationToken.None),
            "notifications" => api.NotificationsAsync("synthetic", null, null, CancellationToken.None),
            "control" => api.ControlAsync("device/runtime-operations", "deviceOperations", "synthetic", null, null, CancellationToken.None),
            "plans" => api.PlanAsync("device/exam-plans", "poll", "synthetic", null, null, CancellationToken.None),
            _ => api.SchoolManagementAsync("synthetic", "device/school-management", null, CancellationToken.None) };
        var error = await Assert.ThrowsAsync<NpepException>(Request);
        Assert.Equal(status is 401 or 403 ? "AUTH_INVALID" : status == 429 ? "RATE_LIMITED" : status >= 500 ? "TEMPORARILY_UNAVAILABLE" : "INVALID_RESPONSE", error.Code);
        Assert.Equal(status is 401 or 403 or 429 or >= 500 ? status : 0, error.Status);
        Assert.DoesNotContain("private", error.Message);
    }

    [Fact]
    public async Task Unknown_protocol_error_on_503_keeps_transient_status_without_accepting_its_code()
    {
        using var api = new NpepApi("https://isolated.invalid", new Handler(request => Task.FromResult(Fixtures.Reply(request, new(), "PRIVATE_UNKNOWN_ERROR", 503))));
        var error = await Assert.ThrowsAsync<NpepException>(() => api.SendAsync("info", "infoResponse"));
        Assert.Equal("TEMPORARILY_UNAVAILABLE", error.Code); Assert.Equal(503, error.Status);
    }

    [Theory]
    [InlineData(200)]
    [InlineData(401)]
    [InlineData(403)]
    [InlineData(429)]
    [InlineData(503)]
    public async Task InterruptedBodyRetainsKnownHttpRejection(int status)
    {
        using var api = new NpepApi("https://isolated.invalid", new Handler(_ =>
            Task.FromResult(new HttpResponseMessage((HttpStatusCode)status)
                { Content = new StreamContent(new BrokenHttpBodyStream()) })));
        if (status == 200)
        {
            var error = await Assert.ThrowsAsync<HttpRequestException>(() => api.SendAsync("info", "infoResponse"));
            Assert.IsAssignableFrom<IOException>(error.InnerException);
        }
        else
        {
            var error = await Assert.ThrowsAsync<NpepException>(() => api.SendAsync("info", "infoResponse"));
            Assert.Equal(status is 401 or 403 ? "AUTH_INVALID" : status == 429 ? "RATE_LIMITED" : "TEMPORARILY_UNAVAILABLE", error.Code);
            Assert.Equal(status, error.Status);
        }
    }

    private sealed class UnboundedContent(byte[] bytes) : HttpContent
    {
        protected override bool TryComputeLength(out long length) { length = 0; return false; }
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => stream.WriteAsync(bytes).AsTask();
        protected override Task<Stream> CreateContentReadStreamAsync() => Task.FromResult<Stream>(new MemoryStream(bytes, writable: false));
    }
}

internal sealed class BrokenHttpBodyStream : MemoryStream
{
    public override bool CanSeek => false;
    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
        ValueTask.FromException<int>(new IOException("synthetic interrupted HTTP body"));
    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        Task.FromException<int>(new IOException("synthetic interrupted HTTP body"));
}

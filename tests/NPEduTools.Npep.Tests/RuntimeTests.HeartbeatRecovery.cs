using System.Net;
using System.Text.Json.Nodes;
using NPEduTools.Contracts;
using NPEduTools.Integrations.Npep;

namespace NPEduTools.Npep.Tests;

public sealed partial class RuntimeTests
{
    [Theory]
    [InlineData("device/me", "html")]
    [InlineData("device/sessions", "empty")]
    [InlineData("device/status", "html")]
    [InlineData("device/status", "request-id")]
    [InlineData("device/status", "schema")]
    [InlineData("device/status", "redirect")]
    [InlineData("device/status", "unreadable-timeout")]
    [InlineData("device/sessions", "structured-timeout")]
    public async Task Invalid_success_on_current_device_link_recovers_without_new_pairing(string endpoint, string fault)
    {
        using var directory = new TestDirectory();
        var server = new FakeServer();
        using (var device = new NpepDevice(directory.Path, server.Api)) await server.ActivateAsync(device);
        int faultEnabled = 1;
        int revision = 3;
        async Task<HttpResponseMessage> Send(HttpRequestMessage request)
        {
            // The server receives the request first. The gateway then substitutes an unusable 2xx reply.
            var response = await server.Send(request);
            if (!request.RequestUri!.AbsolutePath.EndsWith("/" + endpoint, StringComparison.Ordinal) || Volatile.Read(ref faultEnabled) == 0)
                return response;
            if (fault == "redirect")
            {
                response.Dispose();
                return new(HttpStatusCode.Redirect) { Headers = { Location = new Uri("https://portal.invalid/") } };
            }
            if (fault is "unreadable-timeout" or "structured-timeout")
            {
                response.Dispose();
                return fault == "structured-timeout" ? Fixtures.Reply(request, new(), "TEMPORARILY_UNAVAILABLE", 408)
                    : new(HttpStatusCode.RequestTimeout) { Content = new StringContent("gateway read timed out") };
            }
            if (fault is "html" or "empty")
            {
                response.Dispose();
                return new(fault == "empty" ? HttpStatusCode.NoContent : HttpStatusCode.OK)
                { Content = new StringContent(fault == "html" ? "<html>proxy restarting</html>" : "", System.Text.Encoding.UTF8, "text/html") };
            }
            var envelope = JsonNode.Parse(await response.Content.ReadAsStringAsync())!.AsObject();
            if (fault == "request-id") envelope["requestId"] = NpepProtocol.Id();
            else envelope["data"] = new JsonObject { ["acceptedSequence"] = "not an integer" };
            response.Content = new StringContent(envelope.ToJsonString());
            return response;
        }
        await using var runtime = new NpepRuntime(() => new(directory.Path, origin => new NpepApi(origin, new Handler(Send))),
            "unconfirmed-device-reply", () => Sample() with { ClassroomMode = new(Volatile.Read(ref revision), "Daily") });
        await Until(() => runtime.Snapshot().Error is not null, TimeSpan.FromSeconds(15));
        var failed = runtime.Snapshot();
        Assert.Equal("ACTIVE", failed.State);
        Assert.Equal("OFFLINE", failed.Connection);
        Assert.Equal("DEVICE_RESPONSE_UNCONFIRMED", failed.Error);
        Assert.Null(failed.LastReceivedAt);
        Interlocked.Exchange(ref revision, 4);
        Interlocked.Exchange(ref faultEnabled, 0);
        await UntilTlsRecovery(runtime, () => runtime.Snapshot().Connection == "ONLINE");
        Assert.NotNull(runtime.Snapshot().LastReceivedAt);
        Assert.Single(server.Requests, row => row.Path == "pairings");
        Assert.Equal(1, server.Epoch);

        var sessions = server.Requests.Where(row => row.Path == "device/sessions").Select(row => row.Body!).ToArray();
        Assert.Equal(endpoint == "device/sessions" ? 2 : 1, sessions.Length);
        if (sessions.Length == 2) Assert.True(JsonNode.DeepEquals(sessions[0], sessions[1]));
        var statuses = server.Requests.Where(row => row.Path == "device/status").Select(row => row.Body!).ToArray();
        Assert.Equal(endpoint == "device/status" ? 2 : 1, statuses.Length);
        if (statuses.Length == 2)
        {
            Assert.Equal(statuses[0].Text("sessionId"), statuses[1].Text("sessionId"));
            Assert.NotEqual(statuses[0].Text("requestId"), statuses[1].Text("requestId"));
            Assert.True(statuses[1].Number("sequence") > statuses[0].Number("sequence"));
        }
        Assert.Equal(4, statuses[^1]["status"]!.AsObject().Number("modeRevision"));
        Assert.Contains(runtime.Handle(new(1, Guid.NewGuid(), "npep.diagnostics")).ConnectionHistory!.Events,
            item => item is { Code: "DEVICE_RESPONSE_UNCONFIRMED", Retrying: true });
    }

    [Fact]
    public async Task Authorization_refusal_with_stalled_body_still_suspends_original_pairing()
    {
        using var directory = new TestDirectory();
        var server = new FakeServer();
        using (var device = new NpepDevice(directory.Path, server.Api)) await server.ActivateAsync(device);
        await using var runtime = new NpepRuntime(() => new(directory.Path, origin => new NpepApi(origin, new Handler(request =>
            request.RequestUri!.AbsolutePath.EndsWith("/device/status", StringComparison.Ordinal)
                ? Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized) { Content = new StreamContent(new StalledBody()) })
                : server.Send(request)))), "auth-headers-before-timeout", Sample);
        await Until(() => runtime.Snapshot().Error is not null, TimeSpan.FromSeconds(20));
        var state = runtime.Snapshot();
        Assert.Equal("SUSPENDED", state.State);
        Assert.Equal("STOPPED", state.Connection);
        Assert.Equal("AUTH_INVALID", state.Error);
        Assert.Null(state.LastReceivedAt);
    }

    private sealed class StalledBody : MemoryStream
    {
        public override bool CanSeek => false;
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return 0;
        }
    }
}

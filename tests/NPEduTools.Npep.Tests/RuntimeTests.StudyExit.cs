using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using NPEduTools.Contracts;
using NPEduTools.Integrations.Npep;

namespace NPEduTools.Npep.Tests;

public sealed partial class RuntimeTests
{
    [Theory]
    [InlineData(200)] [InlineData(401)] [InlineData(0)]
    public async Task Study_exit_keeps_heartbeats_and_reconciles_the_same_event_after_an_uncertain_reply(int replyStatus)
    {
        using var directory = new TestDirectory();
        var server = new FakeServer();
        var events = new ExitFixture(server);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var sent = new ConcurrentQueue<JsonObject>();
        NpepDevice? native = null;
        async Task<HttpResponseMessage> Send(HttpRequestMessage request)
        {
            if (await ExitPolicyReply(request, server) is { } policy) return policy;
            if (!request.RequestUri!.AbsolutePath.EndsWith("/photos/exit-events", StringComparison.Ordinal)) return await server.Send(request);
            var body = JsonNode.Parse(await request.Content!.ReadAsStringAsync())!.AsObject();
            sent.Enqueue(body);
            if (sent.Count != 1) return await ExitReply(request, body);
            entered.TrySetResult();
            await release.Task;
            if (replyStatus == 0) throw new HttpRequestException("Synthetic connection loss after storing the event");
            return await ExitReply(request, body, replyStatus);
        }
        using (var paired = new NpepDevice(directory.Path, server.Api)) await server.ActivateAsync(paired);
        await using var runtime = new NpepRuntime(() => native = new(directory.Path, origin => new(origin, new Handler(Send))),
            "study-exit-slow", Sample, study: events);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            var status = await native!.ReportAsync(NpepStatusReader.Map(Sample(), "study-exit-slow"), 0).WaitAsync(TimeSpan.FromSeconds(2));
            Assert.Equal(2, status.Number("acceptedSequence"));
            if (replyStatus != 0)
            {
                Assert.True((await Command(runtime, "pause")).ReportingPaused);
                Assert.False((await Command(runtime, "resume")).ReportingPaused);
            }
            release.TrySetResult();
            await Until(() => events.Stored is not null, TimeSpan.FromSeconds(10));
            Assert.Equal("ACTIVE", native.View().Text("state"));
            var requests = sent.ToArray();
            Assert.Equal(2, requests.Length);
            Assert.True(JsonNode.DeepEquals(requests[0]["event"], requests[1]["event"]));
            Assert.NotEqual(requests[0].Text("requestId"), requests[1].Text("requestId"));
            Assert.Equal(events.EventId, events.Stored);
        }
        finally { release.TrySetResult(); }
    }

    [Fact]
    public async Task Study_exit_pause_preserves_the_queue_and_rate_limited_event_waits_for_retry_after()
    {
        using var directory = new TestDirectory();
        var server = new FakeServer();
        var events = new ExitFixture(server) { Ready = false };
        var clock = new ReceiptTime();
        var requests = new ConcurrentQueue<string>();
        async Task<HttpResponseMessage> Send(HttpRequestMessage request)
        {
            if (await ExitPolicyReply(request, server) is { } policy) return policy;
            if (!request.RequestUri!.AbsolutePath.EndsWith("/photos/exit-events", StringComparison.Ordinal)) return await server.Send(request);
            var body = JsonNode.Parse(await request.Content!.ReadAsStringAsync())!.AsObject();
            requests.Enqueue(body["event"]!.AsObject().Text("eventId"));
            if (requests.Count > 1) return await ExitReply(request, body);
            var limited = await ExitReply(request, body, 429);
            limited.Headers.RetryAfter = new(TimeSpan.FromSeconds(15));
            return limited;
        }
        using (var paired = new NpepDevice(directory.Path, server.Api)) await server.ActivateAsync(paired);
        await using var runtime = new NpepRuntime(() => new(directory.Path, origin => new(origin, new Handler(Send))),
            "study-exit-backoff", Sample, study: events, receiptTime: clock);
        await Until(() => runtime.Snapshot().Connection == "ONLINE");
        Assert.True((await Command(runtime, "pause")).ReportingPaused);
        events.Ready = true;
        await Task.Delay(3300);
        Assert.Empty(requests);
        Assert.NotNull(events.PendingExit());
        Assert.False((await Command(runtime, "resume")).ReportingPaused);
        await Until(() => events.Failure == "RATE_LIMITED", TimeSpan.FromSeconds(10));
        clock.Advance(TimeSpan.FromSeconds(10));
        await Task.Delay(3300);
        Assert.Single(requests);
        Assert.NotNull(events.PendingExit());
        clock.Advance(TimeSpan.FromSeconds(5));
        await Until(() => events.Stored is not null);
        Assert.Equal(new[] { events.EventId.ToString(), events.EventId.ToString() }, requests);
    }

    private static async Task<HttpResponseMessage?> ExitPolicyReply(HttpRequestMessage request, FakeServer server)
    {
        if (!request.RequestUri!.AbsolutePath.EndsWith("/study-settings", StringComparison.Ordinal)) return null;
        var configuration = StudyProtocolTests.Configuration;
        configuration["identity"] = NpepDevice.RegistrationIdentity(server.Registration());
        return await HomeworkReply(request, configuration, version: "0.10");
    }

    private static async Task<HttpResponseMessage> ExitReply(HttpRequestMessage request, JsonObject body, int status = 200)
    {
        string? code = status == 401 ? "AUTH_INVALID" : status == 429 ? "RATE_LIMITED" : null;
        var reply = Fixtures.Reply(request, new() { ["eventId"] = body["event"]!["eventId"]!.DeepClone(), ["state"] = "Stored" }, code, status);
        using var content = reply.Content;
        var envelope = JsonNode.Parse(await content.ReadAsStringAsync())!.AsObject();
        envelope["protocolVersion"] = "0.15";
        reply.Content = new StringContent(envelope.ToJsonString());
        return reply;
    }

    private sealed class ExitFixture(FakeServer server) : INpepStudySettings, INpepStudyExitEvents
    {
        private readonly string _sessionId = NpepProtocol.Id();
        private volatile bool _removed;
        public volatile bool Ready = true;
        public volatile string? Failure;
        public Guid EventId { get; } = Guid.NewGuid();
        public Guid? Stored => _removed && Failure is null ? EventId : null;
        public void BindStudy(string? scope) { }
        public void BindExit(string? scope) { }
        public void ApplyStudy(JsonObject configuration) { }
        public JsonObject? ObserveStudy() => null;
        public void StudyFailed(string code) { }
        public StudyExitPacket? PendingExit() => !Ready || _removed ? null : new(
            NpepDevice.RegistrationIdentity(server.Registration()).ToJsonString(), new() {
                ["eventId"] = EventId.ToString(), ["version"] = new string('a', 64), ["sessionId"] = _sessionId,
                ["reason"] = "Timeout", ["deviceTime"] = "2026-11-27T11:00:00.000Z", ["schoolNow"] = "2026-11-27T19:00:00.000",
                ["photoEventId"] = null });
        public void ExitStored(Guid eventId) { Assert.Equal(EventId, eventId); Failure = null; _removed = true; }
        public void ExitFailed(string code, Guid? eventId = null)
        {
            Failure = code;
            if (code is "AUTH_INVALID" or "BINDING_CHANGED" or "EXIT_NOT_ELIGIBLE") _removed = true;
        }
    }
}

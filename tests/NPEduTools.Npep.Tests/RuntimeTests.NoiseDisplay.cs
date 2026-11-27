using System.Net;
using System.Text.Json.Nodes;
using NPEduTools.Integrations.Npep;

namespace NPEduTools.Npep.Tests;

public sealed partial class RuntimeTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task Display_cycle_correlates_context_and_keeps_return_intent_until_valid_reply(bool pendingReturn, bool invalidReply)
    {
        using var dir = new TestDirectory(); var server = new FakeServer(); var display = new DisplayFixture(pendingReturn);
        int observations = 0;
        async Task<HttpResponseMessage> Send(HttpRequestMessage request)
        {
            if (!request.RequestUri!.AbsolutePath.Contains("/device/noise-display/", StringComparison.Ordinal)) return await server.Send(request);
            observations++;
            var body = JsonNode.Parse(await request.Content!.ReadAsStringAsync())!.AsObject();
            Assert.EndsWith(pendingReturn ? "/return" : "/observe", request.RequestUri.AbsolutePath);
            NpepNoiseDisplayProtocol.ValidateRequest(pendingReturn ? "return" : "observe", body);
            Assert.NotNull(body["context"]?["identity"]);
            if (pendingReturn) Assert.Equal(display.Intent!["requestId"]!.GetValue<string>(), body.Text("requestId"));
            var data = NoiseDisplayProtocolTests.Reply();
            if (pendingReturn) data["activeReturn"] = new JsonObject { ["requestId"] = body["requestId"]!.DeepClone(),
                ["window"] = body["window"]!.DeepClone(), ["startedAt"] = "2026-10-04T11:00:00.000Z",
                ["expiresAt"] = "2026-10-04T11:05:00.000Z", ["returnMinutes"] = 5, ["remainingSeconds"] = 300 };
            if (invalidReply) data["presence"]!["ageMs"] = -1;
            var response = Fixtures.Reply(request, data);
            var envelope = JsonNode.Parse(await response.Content.ReadAsStringAsync())!.AsObject();
            envelope["protocolVersion"] = "0.9"; response.Content = new StringContent(envelope.ToJsonString()); return response;
        }
        await using var runtime = new NpepRuntime(() => new(dir.Path, origin => new(origin, new Handler(Send))), "display-test", Sample);
        await Command(runtime, "inspect"); await Command(runtime, "pair", server); await Command(runtime, "poll"); await Command(runtime, "confirm", server);
        await Until(() => runtime.Snapshot().Connection == "ONLINE");
        if (invalidReply) await Assert.ThrowsAsync<NpepException>(() => runtime.NoiseDisplayCycleAsync(display, CancellationToken.None));
        else await runtime.NoiseDisplayCycleAsync(display, CancellationToken.None);
        Assert.Equal(1, observations); Assert.Equal(invalidReply ? 0 : 1, display.Confirmed);
        Assert.NotNull(display.Scope); if (invalidReply) Assert.Equal(0, display.Confirmed);
    }
    private sealed class DisplayFixture(bool pending) : INpepNoiseDisplay
    {
        private readonly JsonObject _observation = NoiseDisplayProtocolTests.Request();
        public string? Scope;
        public int Confirmed;
        public JsonObject? Intent = pending ? new() { ["requestId"] = NpepProtocol.Id(),
            ["offlineStartedAt"] = "2026-10-04T11:00:00.000Z", ["returnMinutes"] = 5 } : null;
        public void BindDisplay(string? scope) => Scope = scope;
        public JsonObject? ObserveDisplay()
        { var r = _observation.Copy(); r.Remove("requestId"); r.Remove("context"); return r; }
        public JsonObject? PendingDisplayReturn() => Intent?.Copy();
        public void ConfirmDisplay(JsonObject request, JsonObject reply, double elapsedSeconds) { Assert.True(elapsedSeconds >= 0); Confirmed++; }
        public void DisplayFailed(string code) { }
    }

    [Theory]
    [InlineData(false, 200)] [InlineData(false, 401)]
    [InlineData(true, 200)] [InlineData(true, 401)]
    public async Task Slow_noise_display_exchange_does_not_block_status_or_confirm_after_pause(bool pendingReturn, int status)
    {
        using var dir = new TestDirectory();
        var server = new FakeServer();
        var display = new DisplayFixture(pendingReturn);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        NpepDevice? device = null;
        async Task<HttpResponseMessage> Send(HttpRequestMessage request)
        {
            if (!request.RequestUri!.AbsolutePath.Contains("/device/noise-display/", StringComparison.Ordinal)) return await server.Send(request);
            entered.TrySetResult();
            await release.Task;
            return await NoiseDisplayReply(request, status);
        }
        using (var paired = new NpepDevice(dir.Path, server.Api)) await server.ActivateAsync(paired);
        await using var runtime = new NpepRuntime(() => device = new(dir.Path, origin => new(origin, new Handler(Send))), "noise-display-slow", Sample);
        await Until(() => runtime.Snapshot().Connection == "ONLINE");
        var exchange = runtime.NoiseDisplayCycleAsync(display, default);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var heartbeat = await device!.ReportAsync(NpepStatusReader.Map(Sample(), "noise-display-slow"), 0).WaitAsync(TimeSpan.FromSeconds(2));
            Assert.Equal(2, heartbeat.Number("acceptedSequence"));
            Assert.True((await Command(runtime, "pause")).ReportingPaused);
            Assert.False((await Command(runtime, "resume")).ReportingPaused);
            release.TrySetResult();
            Assert.Equal("SESSION_SUPERSEDED", (await Assert.ThrowsAsync<NpepException>(() => exchange)).Code);
            Assert.Equal(0, display.Confirmed);
            Assert.Equal("ACTIVE", device.View().Text("state"));
        }
        finally
        {
            release.TrySetResult();
            try { await exchange; } catch (NpepException) { }
        }
    }

    [Theory]
    [InlineData(false, false)] [InlineData(true, true)]
    public async Task Noise_display_retry_after_is_endpoint_specific_and_keeps_original_return_intent(bool pendingReturn, bool header)
    {
        using var dir = new TestDirectory();
        var server = new FakeServer();
        var display = new DisplayFixture(pendingReturn);
        var time = new ReceiptTime();
        var calls = new List<(string Action, JsonObject Body)>();
        async Task<HttpResponseMessage> Send(HttpRequestMessage request)
        {
            if (!request.RequestUri!.AbsolutePath.Contains("/device/noise-display/", StringComparison.Ordinal)) return await server.Send(request);
            calls.Add((request.RequestUri.Segments[^1], JsonNode.Parse(await request.Content!.ReadAsStringAsync())!.AsObject()));
            var response = await NoiseDisplayReply(request, calls.Count == 1 ? 429 : 200, calls.Count == 1 && !header ? 18 : null);
            if (calls.Count == 1 && header) response.Headers.RetryAfter = new(TimeSpan.FromSeconds(18));
            return response;
        }
        using (var paired = new NpepDevice(dir.Path, server.Api)) await server.ActivateAsync(paired);
        await using var runtime = new NpepRuntime(() => new(dir.Path, origin => new(origin, new Handler(Send))),
            "noise-display-limit", Sample, receiptTime: time);
        await Until(() => runtime.Snapshot().Connection == "ONLINE");
        var error = await Assert.ThrowsAsync<NpepException>(() => runtime.NoiseDisplayCycleAsync(display, default));
        Assert.Equal("RATE_LIMITED", error.Code);
        Assert.Equal(18, error.RetryAfterSeconds);
        time.Advance(TimeSpan.FromSeconds(17));
        await runtime.NoiseDisplayCycleAsync(display, default);
        Assert.Single(calls);
        var original = display.Intent;
        display.Intent = pendingReturn ? null : new DisplayFixture(true).Intent;
        await runtime.NoiseDisplayCycleAsync(display, default);
        Assert.NotEqual(calls[0].Action, calls[1].Action);
        display.Intent = original;
        time.Advance(TimeSpan.FromSeconds(1));
        await runtime.NoiseDisplayCycleAsync(display, default);
        Assert.Equal(3, calls.Count);
        Assert.Equal(calls[0].Action, calls[2].Action);
        if (pendingReturn) Assert.True(JsonNode.DeepEquals(calls[0].Body, calls[2].Body));
        else Assert.NotEqual(calls[0].Body.Text("requestId"), calls[2].Body.Text("requestId"));
        Assert.Equal(2, display.Confirmed);
    }

    private static async Task<HttpResponseMessage> NoiseDisplayReply(HttpRequestMessage request, int status, int? retryAfter = null)
    {
        var body = JsonNode.Parse(await request.Content!.ReadAsStringAsync())!.AsObject();
        var data = NoiseDisplayProtocolTests.Reply();
        if (request.RequestUri!.AbsolutePath.EndsWith("/return", StringComparison.Ordinal))
            data["activeReturn"] = new JsonObject { ["requestId"] = body.Text("requestId"), ["window"] = body["window"]!.DeepClone(),
                ["startedAt"] = "2026-10-04T11:00:00.000Z", ["expiresAt"] = "2026-10-04T11:05:00.000Z", ["returnMinutes"] = 5, ["remainingSeconds"] = 300 };
        var response = Fixtures.Reply(request, data, status == 429 ? "RATE_LIMITED" : status == 401 ? "AUTH_INVALID" : null, status);
        using var content = response.Content;
        var envelope = JsonNode.Parse(await content.ReadAsStringAsync())!.AsObject();
        envelope["protocolVersion"] = "0.9";
        if (retryAfter is { } seconds) envelope["error"]!["retryAfterSeconds"] = seconds;
        response.Content = new StringContent(envelope.ToJsonString());
        return response;
    }
}

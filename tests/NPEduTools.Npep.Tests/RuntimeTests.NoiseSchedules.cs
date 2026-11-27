using System.Text.Json.Nodes;
using NPEduTools.Integrations.Npep;

namespace NPEduTools.Npep.Tests;

public sealed partial class RuntimeTests
{
    [Theory]
    [InlineData(200)]
    [InlineData(401)]
    public async Task Slow_noise_schedule_does_not_block_status_or_apply_after_pause(int status)
    {
        using var dir = new TestDirectory();
        var server = new FakeServer();
        var schedule = new ScheduleFixture();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        NpepDevice? device = null;
        async Task<HttpResponseMessage> Send(HttpRequestMessage request)
        {
            if (!request.RequestUri!.AbsolutePath.EndsWith("/device/noise-schedule", StringComparison.Ordinal))
                return await server.Send(request);
            entered.TrySetResult();
            await release.Task;
            return await ScheduleReply(request, status);
        }
        using (var paired = new NpepDevice(dir.Path, server.Api)) await server.ActivateAsync(paired);
        const string build = "schedule-slow";
        await using var runtime = new NpepRuntime(() => device = new(dir.Path, origin => new(origin, new Handler(Send))), build, Sample);
        await Until(() => runtime.Snapshot().Connection == "ONLINE");
        var exchange = runtime.NoiseScheduleCycleAsync(schedule, default);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var heartbeat = await device!.ReportAsync(NpepStatusReader.Map(Sample(), build), 0).WaitAsync(TimeSpan.FromSeconds(2));
            Assert.Equal(2, heartbeat.Number("acceptedSequence"));
            Assert.True((await Command(runtime, "pause")).ReportingPaused);
            Assert.False((await Command(runtime, "resume")).ReportingPaused);
            release.TrySetResult();
            Assert.Equal("SESSION_SUPERSEDED", (await Assert.ThrowsAsync<NpepException>(() => exchange)).Code);
            Assert.Equal(0, schedule.Confirmed);
            Assert.Equal("ACTIVE", device.View().Text("state"));
        }
        finally
        {
            release.TrySetResult();
            try { await exchange; } catch (NpepException) { }
        }
    }

    [Theory]
    [InlineData(429, 18)]
    [InlineData(404, 60)]
    public async Task Noise_schedule_waits_before_a_fresh_exchange(int status, int seconds)
    {
        using var dir = new TestDirectory();
        var server = new FakeServer();
        var schedule = new ScheduleFixture();
        var time = new ReceiptTime();
        var calls = new List<JsonObject>();
        NpepDevice? device = null;
        async Task<HttpResponseMessage> Send(HttpRequestMessage request)
        {
            if (!request.RequestUri!.AbsolutePath.EndsWith("/device/noise-schedule", StringComparison.Ordinal))
                return await server.Send(request);
            calls.Add(JsonNode.Parse(await request.Content!.ReadAsStringAsync())!.AsObject());
            var response = await ScheduleReply(request, calls.Count == 1 ? status : 200);
            if (calls.Count == 1 && status == 429) response.Headers.RetryAfter = new(TimeSpan.FromSeconds(seconds));
            return response;
        }
        using (var paired = new NpepDevice(dir.Path, server.Api)) await server.ActivateAsync(paired);
        const string build = "schedule-limit";
        await using var runtime = new NpepRuntime(() => device = new(dir.Path, origin => new(origin, new Handler(Send))),
            build, Sample, receiptTime: time);
        await Until(() => runtime.Snapshot().Connection == "ONLINE");
        var error = await Assert.ThrowsAsync<NpepException>(() => runtime.NoiseScheduleCycleAsync(schedule, default));
        Assert.Equal(status == 404 ? "NOISE_SCHEDULE_UNSUPPORTED" : "RATE_LIMITED", error.Code);
        if (status == 429) Assert.Equal(seconds, error.RetryAfterSeconds);
        time.Advance(TimeSpan.FromSeconds(seconds - 1));
        await runtime.NoiseScheduleCycleAsync(schedule, default);
        Assert.Single(calls);
        if (status == 404)
        {
            Assert.Equal(0, schedule.Confirmed);
            return;
        }
        time.Advance(TimeSpan.FromSeconds(1));
        await runtime.NoiseScheduleCycleAsync(schedule, default);
        Assert.Equal(2, calls.Count);
        Assert.NotEqual(calls[0].Text("requestId"), calls[1].Text("requestId"));
        Assert.Equal(calls[0].Number("sequence") + 1, calls[1].Number("sequence"));
        Assert.Equal(1, schedule.Confirmed);
    }

    [Fact]
    public async Task Current_noise_schedule_refusal_is_visible_without_waiting_for_the_next_heartbeat()
    {
        using var dir = new TestDirectory();
        var server = new FakeServer();
        var schedule = new ScheduleFixture();
        using (var paired = new NpepDevice(dir.Path, server.Api)) await server.ActivateAsync(paired);
        await using var runtime = new NpepRuntime(() => new(dir.Path, origin => new(origin, new Handler(request =>
            request.RequestUri!.AbsolutePath.EndsWith("/device/noise-schedule", StringComparison.Ordinal)
                ? ScheduleReply(request, 401) : server.Send(request)))), "schedule-refusal", Sample, schedules: schedule);
        await Until(() => runtime.Snapshot().State == "SUSPENDED");
        Assert.Equal("STOPPED", runtime.Snapshot().Connection);
        Assert.Equal("AUTH_INVALID", runtime.Snapshot().Error);
        Assert.Equal(0, schedule.Confirmed);
    }

    private static Task<HttpResponseMessage> ScheduleReply(HttpRequestMessage request, int status) =>
        HomeworkReply(request, new()
        {
            ["policy"] = new JsonObject { ["source"] = "Disabled", ["rules"] = new JsonArray(), ["version"] = new string('a', 64) },
            ["confirmed"] = true, ["leaseSeconds"] = 86400, ["command"] = null
        }, status == 429 ? "RATE_LIMITED" : status == 401 ? "AUTH_INVALID" : null, status, "0.7");

    private sealed class ScheduleFixture : INpepNoiseSchedules
    {
        public int Confirmed;
        public void Bind(string? scope, bool eligible) { }
        public JsonObject Observe() => new()
        {
            ["capability"] = "noise.schedule", ["version"] = null, ["source"] = "None", ["owner"] = "None",
            ["reason"] = "NOT_ELIGIBLE", ["schoolNow"] = null, ["clockReady"] = false, ["dateNeedsReview"] = false,
            ["window"] = null, ["next"] = null, ["sessionId"] = null, ["leaseRemainingSeconds"] = 0, ["receipt"] = null
        };
        public void Confirm(JsonObject response, TimeSpan roundTrip = default) => Confirmed++;
    }
}

using NPEduTools.Contracts;
using NPEduTools.Integrations.Npep;

namespace NPEduTools.Npep.Tests;

public sealed partial class RuntimeTests
{
    [Theory]
    [InlineData("request-id")]
    [InlineData("unknown-error")]
    public async Task Invalid_503_envelope_recovers_automatically_with_same_pairing_and_session(string defect)
    {
        using var directory = new TestDirectory(); var server = new FakeServer(); int broken = 1;
        using (var device = new NpepDevice(directory.Path, server.Api)) await server.ActivateAsync(device);
        await using var runtime = new NpepRuntime(() => new(directory.Path, origin => new NpepApi(origin,
            new Handler(async request => {
                if (!request.RequestUri!.AbsolutePath.EndsWith("/device/status", StringComparison.Ordinal) || Volatile.Read(ref broken) == 0) return await server.Send(request);
                var response = Fixtures.Reply(request, new(), defect == "unknown-error" ? "UPSTREAM_UNKNOWN" : "TEMPORARILY_UNAVAILABLE", 503);
                if (defect == "request-id") { var envelope = System.Text.Json.Nodes.JsonNode.Parse(await response.Content.ReadAsStringAsync())!.AsObject(); envelope["requestId"] = NpepProtocol.Id(); response.Content = new StringContent(envelope.ToJsonString()); }
                return response;
            }))), "structured-503-recovery", Sample);
        await Until(() => runtime.Snapshot().Error is not null);
        Assert.Equal("TEMPORARILY_UNAVAILABLE", runtime.Snapshot().Error); Assert.Equal("ACTIVE", runtime.Snapshot().State);
        Assert.Equal("OFFLINE", runtime.Snapshot().Connection);
        Interlocked.Exchange(ref broken, 0);
        await UntilTlsRecovery(runtime, () => runtime.Snapshot().Connection == "ONLINE");
        Assert.Single(server.Requests, r => r.Path == "device/sessions"); Assert.Single(server.Requests, r => r.Path == "pairings");
        var history = runtime.Handle(new(1, Guid.NewGuid(), "npep.diagnostics")).ConnectionHistory!;
        Assert.Contains(history.Events, e => e.Kind == "Failure" && e.Code == "TEMPORARILY_UNAVAILABLE" && e.HttpStatus == 503 && e.Retrying == true);
        Assert.Contains(history.Events, e => e.Kind == "TransportFailure" && e.Endpoint == "Status" && e.RequestId is not null && e.HttpStatus == 503);
        Assert.Contains(history.Events, e => e.Kind == "Recovered");
        Assert.Null(runtime.Snapshot().Error);
        Assert.Null(runtime.Handle(new(1, Guid.NewGuid(), "npep.status")).ConnectionHistory);
    }
    [Theory]
    [InlineData("AUTH_INVALID", 401)]
    [InlineData("BINDING_CHANGED", 409)]
    [InlineData("SESSION_SUPERSEDED", 409)]
    [InlineData("INSTANCE_MISMATCH", 503)]
    public async Task Verified_terminal_refusal_does_not_auto_resume_even_with_a_transient_status(string code, int status)
    {
        using var directory = new TestDirectory(); var server = new FakeServer();
        using (var device = new NpepDevice(directory.Path, server.Api)) await server.ActivateAsync(device);
        await using var runtime = new NpepRuntime(() => new(directory.Path, origin => new NpepApi(origin, new Handler(request =>
            request.RequestUri!.AbsolutePath.EndsWith("/device/status", StringComparison.Ordinal) ? Task.FromResult(Fixtures.Reply(request, new(), code, status)) : server.Send(request)))), "fenced-refusal", Sample);
        await Until(() => runtime.Snapshot().State == "SUSPENDED");
        Assert.Equal("STOPPED", runtime.Snapshot().Connection);
        Assert.Contains(runtime.Handle(new(1, Guid.NewGuid(), "npep.diagnostics")).ConnectionHistory!.Events, e => e.Kind == "Failure" && e.Code == code && e.Retrying == false);
        await Task.Delay(700); Assert.Single(server.Requests, r => r.Path == "device/sessions");
    }

    [Fact]
    public async Task Live_status_is_sampled_only_after_a_slow_study_request_releases_shared_network_gate()
    {
        using var directory = new TestDirectory(); var server = new FakeServer();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var device = new NpepDevice(directory.Path, origin => new NpepApi(origin, new Handler(async request => {
            if (!request.RequestUri!.AbsolutePath.EndsWith("/device/study-settings", StringComparison.Ordinal)) return await server.Send(request);
            entered.TrySetResult(); await release.Task;
            var response = Fixtures.Reply(request, new(), "TEMPORARILY_UNAVAILABLE", 503);
            var envelope = System.Text.Json.Nodes.JsonNode.Parse(await response.Content.ReadAsStringAsync())!.AsObject(); envelope["protocolVersion"] = "0.10";
            response.Content = new StringContent(envelope.ToJsonString()); return response;
        })));
        await server.ActivateAsync(device); await device.OpenSessionAsync();
        Task<System.Text.Json.Nodes.JsonObject>? report = null;
        int samples = 0; var pending = device.StudyAsync(null, CancellationToken.None);
        try {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            report = device.ReportAsync(() => { Interlocked.Increment(ref samples); return new(NpepStatusReader.Map(Sample(), "fixture"), System.Diagnostics.Stopwatch.GetTimestamp()); });
            await Task.Delay(6100);
            Assert.Equal(0, samples); Assert.False(report.IsCompleted);
            release.TrySetResult();
            Assert.Equal("TEMPORARILY_UNAVAILABLE", (await Assert.ThrowsAsync<NpepException>(() => pending)).Code);
            await report.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.Equal(1, samples); Assert.Equal("ACTIVE", device.View().Text("state"));
            Assert.Single(server.Requests, r => r.Path == "device/status"); Assert.Single(server.Requests, r => r.Path == "device/sessions");
        } finally { release.TrySetResult(); try { await pending; } catch (NpepException) { } if (report is not null) await report; }
    }
    [Theory]
    [InlineData(401)]
    [InlineData(403)]
    public async Task Authorization_refusal_with_interrupted_body_still_suspends_pairing(int status)
    {
        using var directory = new TestDirectory();
        var server = new FakeServer();
        using (var device = new NpepDevice(directory.Path, server.Api)) await server.ActivateAsync(device);
        await using var runtime = new NpepRuntime(() => new(directory.Path, origin => new NpepApi(origin,
            new Handler(request => request.RequestUri!.AbsolutePath.EndsWith("/device/status", StringComparison.Ordinal)
                ? Task.FromResult(new HttpResponseMessage((System.Net.HttpStatusCode)status)
                    { Content = new StreamContent(new BrokenHttpBodyStream()) }) : server.Send(request)))), "auth-body-rejection", Sample);
        await Until(() => runtime.Snapshot().State == "SUSPENDED");
        Assert.Equal("STOPPED", runtime.Snapshot().Connection);
        Assert.Equal("AUTH_INVALID", runtime.Snapshot().Error);
    }
    [Fact]
    public async Task Actual_runtime_waits_out_the_6100ms_study_queue_before_sampling_and_never_needs_manual_resume()
    {
        using var directory = new TestDirectory(); var server = new FakeServer();
        using (var prepared = new NpepDevice(directory.Path, server.Api)) await server.ActivateAsync(prepared);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        NpepDevice? shared = null; Task<System.Text.Json.Nodes.JsonObject>? slow = null; int samples = 0;
        var runtime = new NpepRuntime(() => shared = new(directory.Path, origin => new NpepApi(origin, new Handler(async request => {
            string path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith("/device/study-settings", StringComparison.Ordinal)) {
                entered.TrySetResult(); await release.Task;
                var response = Fixtures.Reply(request, new(), "TEMPORARILY_UNAVAILABLE", 503);
                var envelope = System.Text.Json.Nodes.JsonNode.Parse(await response.Content.ReadAsStringAsync())!.AsObject(); envelope["protocolVersion"] = "0.10";
                response.Content = new StringContent(envelope.ToJsonString()); return response;
            }
            var reply = await server.Send(request);
            if (path.EndsWith("/device/sessions", StringComparison.Ordinal)) slow = shared!.StudyAsync(null, CancellationToken.None);
            return reply;
        }))), "actual-queued-runtime", () => { Interlocked.Increment(ref samples); return Sample(); });
        try {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5)); await Task.Delay(6100);
            Assert.Equal(0, samples); Assert.DoesNotContain(server.Requests, r => r.Path == "device/status");
            release.TrySetResult();
            await Until(() => runtime.Snapshot().Connection == "ONLINE");
            Assert.Equal(1, samples); Assert.Null(runtime.Snapshot().Error); Assert.Equal("ACTIVE", runtime.Snapshot().State);
            Assert.Single(server.Requests, r => r.Path == "device/status"); Assert.Single(server.Requests, r => r.Path == "device/sessions"); Assert.Single(server.Requests, r => r.Path == "pairings");
            var history = runtime.Handle(new(1, Guid.NewGuid(), "npep.diagnostics")).ConnectionHistory!;
            Assert.Contains(history.Events, e => e.Kind == "QueueWait" && e.Endpoint == "Status" && e.ElapsedMs >= 6000);
            Assert.DoesNotContain(history.Events, e => e.Kind == "Failure");
        } finally { release.TrySetResult(); if (slow is not null) try { await slow; } catch (NpepException) { } await runtime.DisposeAsync(); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Http_response_failure_does_not_suspend_pairing_and_status_recovers_automatically(bool bodyInterrupted)
    {
        using var directory = new TestDirectory();
        var server = new FakeServer();
        using (var device = new NpepDevice(directory.Path, server.Api)) await server.ActivateAsync(device);
        int broken = 1;
        await using var runtime = new NpepRuntime(() => new(directory.Path, origin => new NpepApi(origin,
            new Handler(request => request.RequestUri!.AbsolutePath.EndsWith("/device/status", StringComparison.Ordinal)
                && Volatile.Read(ref broken) == 1
                    ? Task.FromResult(new HttpResponseMessage(bodyInterrupted ? System.Net.HttpStatusCode.OK : System.Net.HttpStatusCode.BadGateway)
                        { Content = bodyInterrupted ? new StreamContent(new BrokenHttpBodyStream()) : new StringContent("<html>temporary gateway failure</html>") })
                    : server.Send(request)))), "gateway-recovery", Sample);
        await Until(() => runtime.Snapshot().Error is not null);
        Assert.Equal(bodyInterrupted ? "NETWORK_UNAVAILABLE" : "TEMPORARILY_UNAVAILABLE", runtime.Snapshot().Error);
        Assert.Equal("ACTIVE", runtime.Snapshot().State);
        Assert.Equal("OFFLINE", runtime.Snapshot().Connection);
        Interlocked.Exchange(ref broken, 0);
        await UntilTlsRecovery(runtime, () => runtime.Snapshot().Connection == "ONLINE");
        Assert.Null(runtime.Snapshot().Error);
        Assert.Single(server.Requests, request => request.Path == "device/sessions");
        Assert.Single(server.Requests, request => request.Path == "pairings");
    }

    [Fact]
    public async Task Slow_status_sample_is_discarded_and_reporter_recovers_without_repairing()
    {
        using var directory = new TestDirectory();
        var server = new FakeServer();
        int samples = 0;
        HostResponse ReadSample()
        {
            // Reproduce the real monotonic sample-age rejection, without sensors or
            // changing the five-second wire freshness limit.
            if (Interlocked.Increment(ref samples) == 1) Thread.Sleep(5300);
            return Sample();
        }
        await using var runtime = new NpepRuntime(() => new(directory.Path, server.Api), "sample-recovery", ReadSample);
        await Command(runtime, "inspect"); await Command(runtime, "pair", server);
        await Command(runtime, "poll"); await Command(runtime, "confirm", server);
        await Until(() => runtime.Snapshot().Error == "SAMPLE_TOO_OLD", TimeSpan.FromSeconds(12));
        Assert.Equal("ACTIVE", runtime.Snapshot().State);
        Assert.Equal("OFFLINE", runtime.Snapshot().Connection);
        Assert.DoesNotContain(server.Requests, request => request.Path == "device/status");
        await Until(() => runtime.Snapshot().Connection == "ONLINE", TimeSpan.FromSeconds(15));
        Assert.True(Volatile.Read(ref samples) >= 2);
        Assert.Null(runtime.Snapshot().Error);
        Assert.Single(server.Requests, request => request.Path == "device/sessions");
        Assert.Single(server.Requests, request => request.Path == "device/status");
    }
}

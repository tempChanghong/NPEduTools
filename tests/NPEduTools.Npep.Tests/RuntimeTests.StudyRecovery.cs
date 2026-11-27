using System.Net;
using System.Text.Json.Nodes;
using NPEduTools.Integrations.Npep;

namespace NPEduTools.Npep.Tests;

public sealed partial class RuntimeTests
{
    [Fact]
    public async Task A_slow_school_management_read_does_not_stop_school_study_sync()
    {
        using var directory = new TestDirectory();
        var server = new FakeServer();
        var study = new StudyFixture();
        var slowManagement = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseManagement = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var downloadedDuringManagement = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var downloadedAfterResume = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task<HttpResponseMessage> Send(HttpRequestMessage request)
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith("/device/school-management", StringComparison.Ordinal))
            {
                if (slowManagement.TrySetResult())
                {
                    await releaseManagement.Task;
                    return new(HttpStatusCode.Unauthorized) { Content = new StringContent("") };
                }
                return new(HttpStatusCode.ServiceUnavailable) { Content = new StringContent("") };
            }
            if (path.EndsWith("/device/study-settings", StringComparison.Ordinal))
                return await StudyReply(request, StudyConfiguration(server));
            if (path.EndsWith("/device/study-settings/receipt", StringComparison.Ordinal))
            {
                if (slowManagement.Task.IsCompleted && !releaseManagement.Task.IsCompleted) downloadedDuringManagement.TrySetResult();
                if (releaseManagement.Task.IsCompleted) downloadedAfterResume.TrySetResult();
                return await StudyReply(request, new() { ["confirmed"] = true, ["version"] = StudyProtocolTests.Configuration["version"]!.DeepClone() });
            }
            return await server.Send(request);
        }
        await using var runtime = new NpepRuntime(() => new(directory.Path, origin => new(origin, new Handler(Send))), "study-background", Sample,
            study: study, schoolManagement: new ClassroomPolicyObserver());
        try
        {
            await Command(runtime, "inspect"); await Command(runtime, "pair", server);
            await Command(runtime, "poll"); await Command(runtime, "confirm", server);
            await slowManagement.Task.WaitAsync(TimeSpan.FromSeconds(9));
            await downloadedDuringManagement.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(study.Applied >= 1);
            Assert.True((await Command(runtime, "pause")).ReportingPaused);
            Assert.False((await Command(runtime, "resume")).ReportingPaused);
            releaseManagement.TrySetResult(); // Previous connection's rejection arrives after the explicit resume.
            await downloadedAfterResume.Task.WaitAsync(TimeSpan.FromSeconds(8));
            Assert.Equal("ACTIVE", runtime.Snapshot().State);
        }
        finally { releaseManagement.TrySetResult(); }
    }

    [Theory]
    [InlineData("transport", 2, null)]
    [InlineData("gateway", 2, null)]
    [InlineData("rate-limited", 1, "RATE_LIMITED")]
    [InlineData("revoked", 1, "AUTH_INVALID")]
    public async Task Study_configuration_recovers_a_short_read_failure_without_replaying_receipts(string fault, int reads, string? expected)
    {
        using var directory = new TestDirectory();
        var server = new FakeServer();
        var study = new StudyFixture(); var time = new ReceiptTime();
        List<string> requests = [];
        int receipts = 0;
        async Task<HttpResponseMessage> Send(HttpRequestMessage request)
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/device/study-settings", StringComparison.Ordinal))
            {
                requests.Add(request.Headers.GetValues("X-Request-Id").Single());
                if (requests.Count == 1)
                {
                    if (fault == "transport") throw new HttpRequestException("Isolated read reset");
                    if (fault == "gateway") return new(HttpStatusCode.BadGateway) { Content = new StringContent("<html>gateway restarting</html>") };
                    return await StudyReply(request, new(), fault == "revoked" ? "AUTH_INVALID" : "RATE_LIMITED", fault == "revoked" ? 401 : 429);
                }
                return await StudyReply(request, StudyConfiguration(server));
            }
            if (request.RequestUri.AbsolutePath.EndsWith("/device/study-settings/receipt", StringComparison.Ordinal))
            {
                receipts++;
                return await StudyReply(request, new() { ["confirmed"] = true, ["version"] = StudyProtocolTests.Configuration["version"]!.DeepClone() });
            }
            return await server.Send(request);
        }
        await using var runtime = new NpepRuntime(() => new(directory.Path, origin => new(origin, new Handler(Send))), "study-read-recovery", Sample, receiptTime: time);
        await Command(runtime, "inspect"); await Command(runtime, "pair", server);
        await Command(runtime, "poll"); await Command(runtime, "confirm", server);
        await Until(() => runtime.Snapshot().Connection == "ONLINE", TimeSpan.FromSeconds(20));
        if (expected is null) await runtime.StudyCycleAsync(study, CancellationToken.None);
        else
        {
            var error = await Assert.ThrowsAsync<NpepException>(() => runtime.StudyCycleAsync(study, CancellationToken.None));
            Assert.Equal(expected, error.Code);
            if (fault == "rate-limited")
            {
                Assert.Equal(25, error.RetryAfterSeconds);
                await runtime.StudyCycleAsync(study, CancellationToken.None); // Normal cadence must respect the server's wait.
            }
        }
        Assert.Equal(reads, requests.Count);
        Assert.Equal(reads, requests.Distinct().Count());
        Assert.Equal(expected is null ? 1 : 0, receipts);
        Assert.Equal(expected is null ? 1 : 0, study.Applied);
        if (fault == "rate-limited")
        {
            time.Advance(TimeSpan.FromSeconds(25));
            await runtime.StudyCycleAsync(study, CancellationToken.None);
            Assert.Equal(2, requests.Count);
            Assert.Equal(1, receipts);
            Assert.Equal(1, study.Applied);
        }
    }

    private static JsonObject StudyConfiguration(FakeServer server)
    {
        var data = StudyProtocolTests.Configuration;
        data["identity"] = NpepDevice.RegistrationIdentity(server.Registration());
        return data;
    }

    private static async Task<HttpResponseMessage> StudyReply(HttpRequestMessage request, JsonObject data, string? problem = null, int status = 200)
    {
        var response = Fixtures.Reply(request, data, problem, status);
        var envelope = JsonNode.Parse(await response.Content.ReadAsStringAsync())!.AsObject();
        envelope["protocolVersion"] = "0.10";
        if (status == 429)
        {
            envelope["error"]!["retryAfterSeconds"] = 20;
            response.Headers.RetryAfter = new(TimeSpan.FromSeconds(25));
        }
        response.Content = new StringContent(envelope.ToJsonString());
        return response;
    }

    [Theory]
    [InlineData("receipt", "TEMPORARILY_UNAVAILABLE", 503, true)]
    [InlineData("execution", "TEMPORARILY_UNAVAILABLE", 503, true)]
    [InlineData("resume-exchange", "RATE_LIMITED", 429, true)]
    [InlineData("resume-exchange", "transport", 0, true)]
    [InlineData("resume-exchange", "deadline", 0, true)]
    [InlineData("configuration", "TEMPORARILY_UNAVAILABLE", 503, false)]
    [InlineData("receipt", "POLICY_CHANGED", 409, false)]
    [InlineData("execution", "AUTH_INVALID", 401, false)]
    [InlineData("resume-exchange", "INVALID_RESPONSE", 503, false)]
    [InlineData("resume-exchange", "paused", 503, false)]
    public async Task Fresh_download_survives_only_transient_downstream_failures(string stage, string error, int status, bool retain)
    {
        using var directory = new TestDirectory();
        var server = new FakeServer();
        var study = new StudyFixture { ExecutionEnabled = true, ResumeEnabled = true };
        var time = new ReceiptTime();
        NpepRuntime? activeRuntime = null;
        bool fail = true;
        async Task<HttpResponseMessage> Send(HttpRequestMessage request)
        {
            var path = request.RequestUri!.AbsolutePath;
            if (!path.Contains("/device/study-settings", StringComparison.Ordinal)) return await server.Send(request);
            string endpoint = path.Split('/').Last();
            if (endpoint == "study-settings") endpoint = "configuration";
            string version = endpoint == "resume-exchange" ? "0.11" : "0.10";
            HttpResponseMessage response;
            if (fail && endpoint == stage)
            {
                if (error == "paused")
                    Assert.Equal("Accepted", activeRuntime!.Handle(new(1, Guid.NewGuid(), "npep.command",
                        Npep: new("pause", activeRuntime.Snapshot().Revision))).Outcome);
                if (error == "transport") throw new HttpRequestException("Synthetic transport failure");
                if (error == "deadline") throw new OperationCanceledException("Synthetic request deadline");
                response = Fixtures.Reply(request, new(), error == "paused" ? "TEMPORARILY_UNAVAILABLE" : error, status);
            }
            else
            {
                JsonObject data;
                if (endpoint == "configuration")
                {
                    data = StudyProtocolTests.Configuration;
                    var registration = server.Registration();
                    var identity = new JsonObject();
                    foreach (var field in new[] { "serverInstanceId", "deploymentEpoch", "deviceId", "bindingRevision", "credentialGeneration" })
                        identity[field] = registration[field]!.DeepClone();
                    data["identity"] = identity;
                }
                else
                {
                    var body = JsonNode.Parse(await request.Content!.ReadAsStringAsync())!.AsObject();
                    data = new() { ["confirmed"] = true, ["version"] = (body["version"] ?? body["status"]!["version"])!.DeepClone() };
                    if (endpoint == "execution") data["leaseSeconds"] = 86400;
                    if (endpoint == "resume-exchange") data["command"] = null;
                }
                response = Fixtures.Reply(request, data);
            }
            var envelope = JsonNode.Parse(await response.Content.ReadAsStringAsync())!.AsObject();
            envelope["protocolVersion"] = version;
            response.Content = new StringContent(envelope.ToJsonString());
            return response;
        }
        await using var runtime = new NpepRuntime(() => new(directory.Path, origin => new(origin, new Handler(Send))), "study-recovery", Sample, receiptTime: time);
        activeRuntime = runtime;
        await Command(runtime, "inspect"); await Command(runtime, "pair", server);
        await Command(runtime, "poll"); await Command(runtime, "confirm", server);
        await Until(() => runtime.Snapshot().Connection == "ONLINE");
        if (retain) await runtime.StudyCycleAsync(study, CancellationToken.None);
        else
        {
            var rejection = await Assert.ThrowsAsync<NpepException>(() => runtime.StudyCycleAsync(study, CancellationToken.None));
            Assert.Equal(error == "paused" ? "SESSION_SUPERSEDED" : error, rejection.Code);
        }
        Assert.Equal(stage == "configuration" ? 0 : 1, study.Applied);
        Assert.Equal(stage == "resume-exchange" ? 1 : 0, study.Granted);
        Assert.Equal(0, study.Resumed);
        if (retain) Assert.NotNull(study.ObserveStudy());
        // A subsequent poll obtains current settings and exchanges a new execution grant.
        if (retain)
        {
            fail = false;
            if (error == "RATE_LIMITED")
            {
                await runtime.StudyCycleAsync(study, CancellationToken.None);
                Assert.Equal(2, study.Applied); // Fresh GETs continue while the exchange is cooling down.
                Assert.Equal(1, study.Granted);
                time.Advance(TimeSpan.FromSeconds(6));
            }
            await runtime.StudyCycleAsync(study, CancellationToken.None);
            Assert.Equal(error == "RATE_LIMITED" ? 3 : 2, study.Applied);
            Assert.Equal(stage == "resume-exchange" ? 2 : 1, study.Granted);
        }
    }
}

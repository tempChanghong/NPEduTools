using System.Net;
using System.Text.Json.Nodes;
using NPEduTools.Integrations.Npep;

namespace NPEduTools.Npep.Tests;

public sealed partial class RuntimeTests
{
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
        await using var runtime = new NpepRuntime(() => new(directory.Path, origin => new(origin, new Handler(Send))), "study-recovery", Sample);
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
            await runtime.StudyCycleAsync(study, CancellationToken.None);
            Assert.Equal(2, study.Applied);
            Assert.Equal(stage == "resume-exchange" ? 2 : 1, study.Granted);
        }
    }
}

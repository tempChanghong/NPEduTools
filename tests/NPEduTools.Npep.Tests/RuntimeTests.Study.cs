using System.Net;
using System.Text.Json.Nodes;
using NPEduTools.Integrations.Npep;

namespace NPEduTools.Npep.Tests;

public sealed partial class RuntimeTests
{
    [Theory]
    [InlineData(false, false, false)] [InlineData(true, false, true)] [InlineData(false, true, true)] [InlineData(false, false, true)]
    [InlineData(false, false, true, "valid")] [InlineData(false, false, true, "unsupported")]
    [InlineData(false, false, true, "old-version")] [InlineData(false, false, true, "late-pause")]
    public async Task Study_cycle_applies_only_the_current_identity_and_distinguishes_receipt_failure(bool wrongIdentity, bool changedPolicy, bool executionEnabled, string resumeMode = "none")
    {
        using var dir = new TestDirectory(); var server = new FakeServer(); var study = new StudyFixture { ExecutionEnabled = executionEnabled, ResumeEnabled = resumeMode != "none" }; int receipts = 0;
        NpepRuntime? activeRuntime = null;
        async Task<HttpResponseMessage> Send(HttpRequestMessage request)
        {
            string path = request.RequestUri!.AbsolutePath;
            if (!path.Contains("/device/study-settings", StringComparison.Ordinal)) return await server.Send(request);
            JsonObject data;
            if (path.EndsWith("/resume-exchange", StringComparison.Ordinal))
            {
                Assert.Equal(1, study.Granted); Assert.Equal("0.11", request.Headers.GetValues("X-NPEP-Version").Single());
                var body = JsonNode.Parse(await request.Content!.ReadAsStringAsync())!.AsObject();
                NpepStudyProtocol.Validate("resumeExchangeRequest", body);
                Assert.Equal("ACCEPTED", body["receipt"]!.AsObject().Text("outcome"));
                if (resumeMode == "late-pause") Assert.Equal("Accepted", activeRuntime!.Handle(new(1, Guid.NewGuid(), "npep.command", Npep: new("pause", activeRuntime.Snapshot().Revision))).Outcome);
                var resumeResponse = Fixtures.Reply(request, new() { ["confirmed"] = true, ["version"] = resumeMode == "old-version" ? new string('b', 64) : body["version"]!.DeepClone(),
                    ["command"] = new JsonObject { ["commandId"] = NpepProtocol.Id(), ["version"] = body["version"]!.DeepClone(),
                        ["window"] = JsonNode.Parse("{\"start\":\"2026-10-12T19:00:00.000\",\"end\":\"2026-10-12T21:30:00.000\"}") } });
                var resumeEnvelope = JsonNode.Parse(await resumeResponse.Content.ReadAsStringAsync())!.AsObject(); resumeEnvelope["protocolVersion"] = "0.11";
                resumeResponse.Content = new StringContent(resumeEnvelope.ToJsonString());
                if (resumeMode == "unsupported") resumeResponse.StatusCode = HttpStatusCode.NotFound;
                return resumeResponse;
            }
            if (path.EndsWith("/execution", StringComparison.Ordinal))
            {
                Assert.Equal(1, receipts);
                var body = JsonNode.Parse(await request.Content!.ReadAsStringAsync())!.AsObject(); NpepStudyProtocol.Validate("executionRequest", body);
                data = new() { ["confirmed"] = true, ["version"] = body["version"]!.DeepClone(), ["leaseSeconds"] = 86400 };
            }
            else if (!path.EndsWith("/receipt", StringComparison.Ordinal))
            {
                data = StudyProtocolTests.Configuration;
                var registration = server.Registration(); var identity = new JsonObject();
                foreach (var field in new[] {"serverInstanceId","deploymentEpoch","deviceId","bindingRevision","credentialGeneration"}) identity[field] = registration[field]!.DeepClone();
                if (wrongIdentity) identity["deviceId"] = NpepProtocol.Id();
                data["identity"] = identity;
            }
            else
            {
                receipts++; var body = JsonNode.Parse(await request.Content!.ReadAsStringAsync())!.AsObject();
                NpepStudyProtocol.Validate("receiptRequest", body);
                Assert.False(body["context"]!.AsObject().ContainsKey("controlEpoch"));
                Assert.Equal("NOT_INTEGRATED", body["status"]!.AsObject().Text("captureState"));
                if (changedPolicy)
                {
                    var rejection = Fixtures.Reply(request, new(), "POLICY_CHANGED", 409);
                    var e = JsonNode.Parse(await rejection.Content.ReadAsStringAsync())!.AsObject(); e["protocolVersion"] = "0.10";
                    rejection.Content = new StringContent(e.ToJsonString()); return rejection;
                }
                data = new() { ["confirmed"] = true, ["version"] = body["status"]!["version"]!.DeepClone() };
            }
            var response = Fixtures.Reply(request, data);
            var envelope = JsonNode.Parse(await response.Content.ReadAsStringAsync())!.AsObject(); envelope["protocolVersion"] = "0.10";
            response.Content = new StringContent(envelope.ToJsonString()); return response;
        }
        await using var runtime = new NpepRuntime(() => new(dir.Path, origin => new(origin, new Handler(Send))), "study-test", Sample);
        activeRuntime = runtime;
        await Command(runtime, "inspect"); await Command(runtime, "pair", server); await Command(runtime, "poll"); await Command(runtime, "confirm", server);
        await Until(() => runtime.Snapshot().Connection == "ONLINE");
        if (resumeMode == "late-pause") Assert.Equal("SESSION_SUPERSEDED", (await Assert.ThrowsAsync<NpepException>(() => runtime.StudyCycleAsync(study, CancellationToken.None))).Code);
        else if (wrongIdentity || changedPolicy)
            Assert.Equal(wrongIdentity ? "SESSION_SUPERSEDED" : "POLICY_CHANGED", (await Assert.ThrowsAsync<NpepException>(() => runtime.StudyCycleAsync(study, CancellationToken.None))).Code);
        else await runtime.StudyCycleAsync(study, CancellationToken.None);
        Assert.Equal(!wrongIdentity && !changedPolicy && executionEnabled ? 1 : 0, study.Granted);
        Assert.Equal(wrongIdentity ? 0 : 1, study.Applied); Assert.Equal(wrongIdentity ? 0 : 1, receipts);
        Assert.Equal(resumeMode == "valid" ? 1 : 0, study.Resumed);
    }
    private sealed class StudyFixture : INpepStudySettings
    {
        private JsonObject? _configuration;
        public int Applied, Granted, Resumed;
        public bool ExecutionEnabled, ResumeEnabled;
        public bool SupportsResume => ResumeEnabled;
        public JsonObject? ObserveStudyResumeReceipt() => new() { ["commandId"] = "11111111-1111-4111-8111-111111111111", ["outcome"] = "ACCEPTED" };
        public void ApplyStudyResume(JsonObject configuration, JsonObject command, TimeSpan roundTrip = default)
        { NpepStudyProtocol.Validate("resumeCommand", command); Assert.True(roundTrip >= TimeSpan.Zero); Resumed++; }
        public bool SupportsExecution => ExecutionEnabled;
        public JsonObject? ObserveStudyExecution() => new() { ["owner"] = "None", ["state"] = "Idle", ["sessionId"] = null,
            ["reason"] = "POLICY_EXPIRED", ["clockReady"] = false, ["schoolNow"] = null, ["window"] = null, ["signalFresh"] = false };
        public void ConfirmStudyExecution(JsonObject configuration, JsonObject response, TimeSpan roundTrip = default)
        { NpepStudyProtocol.Validate("executionResponse", response); Assert.True(response["confirmed"]!.GetValue<bool>()); Assert.True(roundTrip >= TimeSpan.Zero); Granted++; }
        public void BindStudy(string? scope) { if (scope is null) _configuration = null; }
        public void ApplyStudy(JsonObject configuration) { Applied++; _configuration = configuration.Copy(); }
        public void StudyFailed(string code) => _configuration = null;
        public JsonObject? ObserveStudy() => _configuration is null ? null : new() {
            ["version"] = _configuration["version"]!.DeepClone(), ["policyState"] = "APPLIED", ["presentationState"] = "NOT_INTEGRATED",
            ["captureState"] = "NOT_INTEGRATED", ["phase"] = "Blocked", ["reason"] = "SCHOOL_CLOCK_UNAVAILABLE", ["clockReady"] = false,
            ["schoolNow"] = null, ["window"] = null };
    }
}

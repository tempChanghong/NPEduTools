using System.Net;
using System.Text.Json.Nodes;
using NPEduTools.Integrations.Npep;

namespace NPEduTools.Npep.Tests;

public sealed partial class RuntimeTests
{
    // The control channel polls every 10 seconds. Allow two cycles and runner
    // scheduling overhead; the generic wait keeps its original 8-second budget.
    private static Task UntilControl(Func<bool> test) => Until(test, TimeSpan.FromSeconds(25));

    [Fact]
    public void N3SharedExamplesMatchDesktopValidator()
    {
        var cases = (JsonArray)JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "n3-examples.json")))!["cases"]!;
        foreach (var row in cases.OfType<JsonObject>())
        {
            var error = Record.Exception(() => NpepRuntimeProtocol.Validate(row.Text("definition"), (JsonObject)row["value"]!));
            Assert.True((error is null) == row["valid"]!.GetValue<bool>(), row.Text("name") + ": " + error?.Message);
        }
    }

    private sealed class ControlEffects : INpepRuntimeControl
    {
        public int Executions, Recoveries;
        private bool _known;
        public Task<JsonObject> ObserveAsync(CancellationToken token) => Task.FromResult(new JsonObject
        {
            ["runtimeMode"] = "OTHER", ["runtimePhase"] = "IDLE", ["runtimeRevision"] = 0, ["modeRevision"] = 0,
            ["configurationRevision"] = 0, ["remoteExamPause"] = false, ["recording"] = "IDLE", ["desktop"] = "INTERACTIVE",
            ["noticeOpen"] = false, ["operationId"] = null, ["observedAt"] = NpepRuntimeProtocol.UtcNow()
        });
        public async Task ExecuteAsync(JsonObject op, Func<bool, CancellationToken, Task> authorize, CancellationToken token)
        { await authorize(false, token); await authorize(true, token); Executions++; _known = true; }
        public Task RecoverAsync(JsonObject op, CancellationToken token) { Recoveries++; _known = true; return Task.CompletedTask; }
        public bool HasOperation(Guid id) => _known;
        public bool LocallyEnded(Guid id) => false;
        public Task<JsonObject> ResultAsync(Guid id, CancellationToken token) => Task.FromResult(new JsonObject
        {
            ["state"] = Executions > 0 ? "SUCCEEDED" : "UNKNOWN", ["step"] = "VERIFY", ["reasonCode"] = null,
            ["evidence"] = new JsonObject { ["examAware"] = Executions > 0 ? "READY" : "UNKNOWN", ["classIsland"] = Executions > 0 ? "EXITED" : "UNKNOWN",
                ["remoteExamPause"] = true, ["startup"] = "EXAM_MODE_APPLIED", ["sideEffects"] = "POSSIBLE", ["alreadySatisfied"] = false,
                ["observedAt"] = NpepRuntimeProtocol.UtcNow(), ["configurationRevision"] = 0 }
        });
    }

    private sealed class ControlWire(FakeServer server)
    {
        public JsonObject? Policy, Operation, Received;
        public bool LoseStartReply, LoseEventReply;
        public int StatusReports, EventRequests;
        public JsonObject? FirstReceived;
        public NpepApi Api(string origin) => new(origin, new Handler(Send));
        private async Task<HttpResponseMessage> Send(HttpRequestMessage request)
        {
            if (!request.Headers.GetValues("X-NPEP-Version").Contains("0.4")) return await server.Send(request);
            string path = request.RequestUri!.AbsolutePath["/api/v2/npep/".Length..];
            var body = request.Content is null ? null : (JsonObject)JsonNode.Parse(await request.Content.ReadAsStringAsync())!;
            JsonObject data;
            if (path == "device/runtime-control-policy")
            {
                NpepRuntimeProtocol.Validate("policyRequest", body!); Policy = ((JsonObject)body!["policy"]!).Copy();
                data = new() { ["disposition"] = "APPLIED", ["consentId"] = Policy["consentId"]!.DeepClone(), ["policyRevision"] = Policy["policyRevision"]!.DeepClone(), ["receivedAt"] = NpepRuntimeProtocol.UtcNow() };
            }
            else if (path == "device/runtime-status")
            {
                NpepRuntimeProtocol.Validate("statusRequest", body!); StatusReports++;
                if (Policy!["enabled"]!.GetValue<bool>() && Operation is null)
                {
                    var ctx = (JsonObject)body!["context"]!;
                    Operation = new() { ["operationId"] = NpepProtocol.Id(), ["deviceId"] = ctx["identity"]!["deviceId"]!.DeepClone(), ["requestId"] = NpepProtocol.Id(),
                        ["identity"] = ctx["identity"]!.DeepClone(), ["schoolId"] = "school", ["administrativeClassId"] = "class", ["screenBindingId"] = "screen",
                        ["target"] = "EXAM", ["scope"] = "EXAM_MODE", ["consentId"] = Policy["consentId"]!.DeepClone(), ["policyRevision"] = Policy["policyRevision"]!.DeepClone(),
                        ["controlEpoch"] = ctx["controlEpoch"]!.DeepClone(), ["expectedRuntimeRevision"] = 0, ["expectedModeRevision"] = 0, ["expectedConfigurationRevision"] = 0,
                        ["initiator"] = new JsonObject { ["displayName"] = "测试管理员" }, ["createdAt"] = NpepRuntimeProtocol.UtcNow(),
                        ["expiresAt"] = DateTimeOffset.UtcNow.AddMinutes(5).ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'"), ["state"] = "QUEUED", ["step"] = null, ["reasonCode"] = null,
                        ["lastEventSequence"] = 0, ["progressReceivedAt"] = null, ["grant"] = null, ["evidence"] = null, ["resolvedAt"] = null, ["resolutionId"] = null,
                        ["localEndedAt"] = null, ["freshness"] = "UNKNOWN" };
                }
                data = new() { ["disposition"] = "APPLIED", ["acceptedSequence"] = body!["sequence"]!.DeepClone(), ["receivedAt"] = NpepRuntimeProtocol.UtcNow(), ["nextPollSeconds"] = 10 };
            }
            else if (path == "device/runtime-operations") data = new() { ["items"] = Operation is null || Operation["resolvedAt"] is not null ? new JsonArray() : new JsonArray(Operation.Copy()), ["pollAfterSeconds"] = 10 };
            else if (path.EndsWith("/start"))
            {
                NpepRuntimeProtocol.Validate("startRequest", body!);
                var grant = ((JsonObject)body!["context"]!).Copy();
                foreach (var field in new[] { "consentId", "policyRevision", "expectedRuntimeRevision", "expectedModeRevision", "expectedConfigurationRevision" }) grant[field] = body[field]!.DeepClone();
                grant["operationId"] = Operation!["operationId"]!.DeepClone(); grant["grantId"] = NpepProtocol.Id(); grant["authorizedAt"] = NpepRuntimeProtocol.UtcNow();
                grant["startNotAfter"] = DateTimeOffset.UtcNow.AddSeconds(30).ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'");
                Operation["grant"] = grant.Copy(); Operation["state"] = "START_AUTHORIZED";
                if (LoseStartReply) { LoseStartReply = false; throw new HttpRequestException("lost committed start reply"); }
                data = new() { ["grant"] = grant, ["serverTime"] = NpepRuntimeProtocol.UtcNow() };
            }
            else if (path == "device/runtime-operation-events")
            {
                NpepRuntimeProtocol.Validate("eventsRequest", body!); Received = ((JsonObject)body!["events"]![0]!).Copy();
                Operation!["state"] = Received["state"]!.DeepClone(); Operation["lastEventSequence"] = Received["sequence"]!.DeepClone();
                if (Received.Text("state") == "SUCCEEDED") Operation["resolvedAt"] = NpepRuntimeProtocol.UtcNow();
                FirstReceived ??= Received.Copy(); EventRequests++;
                if (LoseEventReply) { LoseEventReply = false; throw new HttpRequestException("lost committed event reply"); }
                data = new() { ["results"] = new JsonArray(new JsonObject { ["eventId"] = Received["eventId"]!.DeepClone(), ["status"] = "ACCEPTED", ["code"] = null, ["acceptedSequence"] = Received["sequence"]!.DeepClone() }) };
            }
            else throw new InvalidOperationException(path);
            return new(HttpStatusCode.OK) { Content = new StringContent(new JsonObject { ["protocolVersion"] = "0.4", ["requestId"] = request.Headers.GetValues("X-Request-Id").Single(), ["serverTime"] = NpepRuntimeProtocol.UtcNow(), ["data"] = data }.ToJsonString()) };
        }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task N3GrantAndPersistentReceiptNeverReplayAfterRestartOrLostStartReply(bool loseStart, bool delayedPairing)
    {
        using var dir = new TestDirectory(); var server = new FakeServer(); var wire = new ControlWire(server) { LoseStartReply = loseStart };
        var effects = new ControlEffects();
        await using (var runtime = new NpepRuntime(() => new(dir.Path, wire.Api), "test", Sample, effects))
        {
            // Let the initial control poll run while unpaired, as during slower startup.
            if (delayedPairing) await Task.Delay(1500);
            await Command(runtime, "inspect"); await Command(runtime, "pair", server); await Command(runtime, "poll"); await Command(runtime, "confirm", server);
            await Until(() => runtime.ControlPolicy().CanEnable);
            var p = runtime.ControlPolicy(); runtime.SetControlConsent(new("consent", p.Revision, true, p.Scope));
            await UntilControl(() => wire.Received is not null);
            Assert.Equal(loseStart ? "UNKNOWN" : "SUCCEEDED", wire.Received!.Text("state"));
            Assert.Equal(loseStart ? 0 : 1, effects.Executions); Assert.Equal(loseStart ? 1 : 0, effects.Recoveries);
        }
        server.SessionRequest = null; int reports = wire.StatusReports;
        var restartedEffects = new ControlEffects();
        await using var restarted = new NpepRuntime(() => new(dir.Path, wire.Api), "test", Sample, restartedEffects);
        await UntilControl(() => wire.StatusReports > reports);
        Assert.Equal(0, restartedEffects.Executions); Assert.Equal(0, restartedEffects.Recoveries);
    }

    [Fact]
    public async Task N3LostCommittedReceiptIsRetriedUnchangedAfterRestartWithoutExecutingAgain()
    {
        using var dir = new TestDirectory(); var server = new FakeServer();
        var wire = new ControlWire(server) { LoseEventReply = true };
        var effects = new ControlEffects();
        await using (var runtime = new NpepRuntime(() => new(dir.Path, wire.Api), "test", Sample, effects))
        {
            await Command(runtime, "inspect"); await Command(runtime, "pair", server);
            await Command(runtime, "poll"); await Command(runtime, "confirm", server);
            await UntilControl(() => wire.EventRequests == 1);
            Assert.Equal(1, effects.Executions);
            Assert.Equal("SUCCEEDED", wire.FirstReceived!.Text("state"));
            Assert.NotNull(wire.Operation!["resolvedAt"]);
        }
        // The server no longer polls the resolved operation, while the durable client outbox
        // still owns its unacknowledged receipt. A new Host must upload exactly that receipt.
        server.SessionRequest = null;
        var restartedEffects = new ControlEffects();
        await using var restarted = new NpepRuntime(() => new(dir.Path, wire.Api), "test", Sample, restartedEffects);
        await UntilControl(() => wire.EventRequests == 2);
        Assert.True(JsonNode.DeepEquals(wire.FirstReceived, wire.Received));
        Assert.Equal(0, restartedEffects.Executions);
        Assert.Equal(0, restartedEffects.Recoveries);
    }
}

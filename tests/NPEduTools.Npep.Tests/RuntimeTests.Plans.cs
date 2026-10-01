using System.Net;
using System.Text.Json.Nodes;
using NPEduTools.Integrations.Npep;

namespace NPEduTools.Npep.Tests;

public sealed partial class RuntimeTests
{
    private sealed class PlanEffects : INpepExamPlans
    {
        public int Prepares, Starts;
        public Action? BeforeAuthorize;
        public JsonObject Observe() => new() { ["revision"] = 1, ["available"] = true, ["blockReason"] = null,
            ["player"] = new JsonObject { ["known"] = true, ["sessions"] = new JsonArray(), ["lastSession"] = null }, ["preparedId"] = null };
        public Task<JsonObject> ExecuteAsync(JsonObject op, bool start, Guid commandId, Action authorize, CancellationToken token)
        {
            BeforeAuthorize?.Invoke(); authorize();
            if (start) Starts++; else Prepares++;
            return Task.FromResult(new JsonObject { ["state"] = start ? "STARTED" : "PREPARED", ["reasonCode"] = null,
                ["sessionId"] = start ? "test-player" : null, ["summary"] = start ? null : new JsonObject {
                    ["preparationId"] = NpepProtocol.Id(), ["sha256"] = op["sha256"]!.DeepClone(), ["examName"] = "考试", ["message"] = "",
                    ["exams"] = new JsonArray(new JsonObject { ["name"] = "语文", ["start"] = "09:00", ["end"] = "11:00", ["alertTime"] = 15 }) } });
        }
    }
    private sealed class PlanWire(FakeServer server)
    {
        public JsonObject? Operation, Received;
        public bool LoseGrant, LoseResult, DifferentGrant;
        public NpepApi Api(string origin) => new(origin, new Handler(Send));
        private async Task<HttpResponseMessage> Send(HttpRequestMessage request)
        {
            if (!request.Headers.GetValues("X-NPEP-Version").Contains("0.5")) return await server.Send(request);
            string path = request.RequestUri!.AbsolutePath["/api/v2/npep/".Length..];
            var body = request.Content is null ? null : NpepProtocol.Parse(await request.Content.ReadAsByteArrayAsync());
            JsonObject data;
            if (path == "device/exam-plan-status")
            {
                NpepExamPlanProtocol.Validate("reportRequest", body!);
                if (Operation is null && body!["status"]!["enabled"]!.GetValue<bool>())
                {
                    var s = (JsonObject)body["status"]!;
                    Operation = new() { ["operationId"] = NpepProtocol.Id(), ["requestId"] = NpepProtocol.Id(), ["context"] = body["context"]!.DeepClone(),
                        ["consentId"] = s["consentId"]!.DeepClone(), ["policyRevision"] = s["policyRevision"]!.DeepClone(), ["revision"] = 1,
                        ["fileName"] = "test.json", ["sha256"] = new string('a', 64), ["dataBase64"] = "e30=", ["createdAt"] = NpepRuntimeProtocol.UtcNow(),
                        ["expiresAt"] = DateTimeOffset.UtcNow.AddMinutes(5).ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'"), ["state"] = "QUEUED", ["summary"] = null,
                        ["sessionId"] = null, ["reasonCode"] = null, ["grant"] = null };
                }
                data = new() { ["accepted"] = true };
            }
            else if (path == "device/exam-plans") data = new() { ["item"] = Operation?.DeepClone() };
            else if (path.EndsWith("/grant"))
            {
                NpepExamPlanProtocol.Validate("deviceRequest", body!);
                Operation!["state"] = "START_AUTHORIZED";
                Operation["grant"] = new JsonObject { ["operationId"] = DifferentGrant ? NpepProtocol.Id() : Operation["operationId"]!.DeepClone(),
                    ["grantId"] = NpepProtocol.Id(), ["startNotAfter"] = DateTimeOffset.UtcNow.AddSeconds(30).ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'") };
                if (LoseGrant) throw new HttpRequestException("lost grant response");
                data = new() { ["grant"] = Operation["grant"]!.DeepClone(), ["serverTime"] = NpepRuntimeProtocol.UtcNow() };
            }
            else if (path.EndsWith("/result"))
            {
                NpepExamPlanProtocol.Validate("resultRequest", body!);
                if (LoseResult) { LoseResult = false; throw new HttpRequestException("result upload interrupted"); }
                Received = body!.Copy(); Operation!["state"] = body!["state"]!.DeepClone(); Operation["summary"] = body!["summary"]?.DeepClone();
                data = new() { ["accepted"] = true };
            }
            else throw new InvalidOperationException(path);
            return new(HttpStatusCode.OK) { Content = new StringContent(new JsonObject { ["protocolVersion"] = "0.5", ["requestId"] = request.Headers.GetValues("X-Request-Id").Single(),
                ["serverTime"] = NpepRuntimeProtocol.UtcNow(), ["data"] = data }.ToJsonString()) };
        }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task PlansRequireExplicitStartAndNeverReplayAfterLostGrantOrWrongGrant(bool loseGrant, bool differentGrant)
    {
        using var dir = new TestDirectory(); var server = new FakeServer(); var wire = new PlanWire(server); var effects = new PlanEffects();
        await using var runtime = new NpepRuntime(() => new(dir.Path, wire.Api), "test", Sample, plans: effects);
        await Command(runtime, "inspect"); await Command(runtime, "pair", server); await Command(runtime, "poll"); await Command(runtime, "confirm", server);
        await Until(() => runtime.PlanPolicy().CanEnable);
        Assert.True(runtime.PlanPolicy().Allowed); Assert.True(runtime.ControlPolicy().Allowed);
        var p = runtime.PlanPolicy(); runtime.SetPlanConsent(new("plan-consent", p.Revision, true, p.Scope));
        wire.LoseResult = true;
        await Assert.ThrowsAsync<HttpRequestException>(() => runtime.PlanCycleAsync(default));
        Assert.Equal(1, effects.Prepares); Assert.Equal(0, effects.Starts);
        await runtime.PlanCycleAsync(default);
        Assert.Equal(1, effects.Prepares); Assert.Equal("PREPARED", wire.Received!.Text("state"));
        await runtime.PlanCycleAsync(default); Assert.Equal(0, effects.Starts);
        wire.Operation!["state"] = "START_REQUESTED"; wire.Operation["dataBase64"] = null; wire.LoseGrant = loseGrant; wire.DifferentGrant = differentGrant;
        await runtime.PlanCycleAsync(default);
        Assert.Equal(loseGrant || differentGrant ? 0 : 1, effects.Starts);
        Assert.Equal(loseGrant || differentGrant ? "UNKNOWN" : "STARTED", wire.Received!.Text("state"));
        // Simulate the server having lost the final response; the persisted result is resent only.
        wire.Operation["state"] = "START_AUTHORIZED";
        await runtime.PlanCycleAsync(default);
        Assert.Equal(loseGrant || differentGrant ? 0 : 1, effects.Starts);
        Assert.True(runtime.ControlPolicy().Allowed); // Both exam capabilities derive from this binding.
    }

    [Fact]
    public async Task UnpairImmediatelyBeforePlanDispatchPreventsTheEffectAndUpload()
    {
        using var dir = new TestDirectory(); var server = new FakeServer(); var wire = new PlanWire(server); var effects = new PlanEffects();
        await using var runtime = new NpepRuntime(() => new(dir.Path, wire.Api), "test", Sample, plans: effects);
        await Command(runtime, "inspect"); await Command(runtime, "pair", server); await Command(runtime, "poll"); await Command(runtime, "confirm", server);
        await Until(() => runtime.PlanPolicy().CanEnable);
        var p = runtime.PlanPolicy(); runtime.SetPlanConsent(new("plan-consent", p.Revision, true, p.Scope));
        effects.BeforeAuthorize = () => Command(runtime, "unpair").GetAwaiter().GetResult();
        Assert.Equal("DEVICE_SUSPENDED", (await Assert.ThrowsAsync<NpepException>(() => runtime.PlanCycleAsync(default))).Code);
        Assert.Equal(0, effects.Prepares); Assert.Null(wire.Received);
        Assert.False(runtime.PlanPolicy().Allowed);
    }

    [Fact]
    public void PlanJournalRetainsUnfinishedIntentAcrossRestartAndRejectsInterruptedWrites()
    {
        using var dir = new TestDirectory(); Directory.CreateDirectory(dir.Path);
        var journal = new NpepPlanJournal(dir.Path);
        var intent = new JsonObject { ["version"] = 1, ["operationId"] = NpepProtocol.Id(), ["stage"] = "start", ["context"] = new JsonObject(), ["result"] = null };
        journal.Save(intent);
        Assert.True(JsonNode.DeepEquals(intent, new NpepPlanJournal(dir.Path).State));
        File.WriteAllText(Path.Combine(dir.Path, "exam-plan-journal.json.pending"), "partial");
        Assert.Throws<InvalidDataException>(() => new NpepPlanJournal(dir.Path));
    }
}

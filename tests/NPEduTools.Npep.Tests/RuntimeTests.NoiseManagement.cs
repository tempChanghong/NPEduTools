using System.Net;
using System.Text.Json.Nodes;
using NPEduTools.Integrations.Npep;

namespace NPEduTools.Npep.Tests;

public sealed partial class RuntimeTests
{
    [Theory]
    [InlineData(false, false, 0, 1)]
    [InlineData(true, false, 1, 0)]
    public async Task Noise_cycle_registers_after_statistics_and_checks_school_authority_before_execution(bool denied, bool wrongScope, int untrusted, int managed)
    {
        await NoiseManagementCycle(denied, wrongScope, false, untrusted, managed);
    }
    [Fact]
    public async Task Noise_cycle_rejects_a_grant_for_another_session_without_executing_anything()
    { await NoiseManagementCycle(false, true, false, 0, 0); }
    [Fact]
    public async Task Legacy_management_failure_keeps_statistics_but_does_not_authorize_protected_stop()
    { await NoiseManagementCycle(false, false, true, 1, 0); }
    [Fact]
    public async Task Noise_cycle_keeps_manual_stop_when_optional_registration_fails()
    { await NoiseManagementCycle(false, false, false, 1, 0, registrationRejected: true, protectedStop: false); }

    private async Task NoiseManagementCycle(bool denied, bool wrongScope, bool unsupported, int untrusted, int managed,
        bool registrationRejected = false, bool protectedStop = true)
    {
        using var dir = new TestDirectory(); var server = new FakeServer(); var noise = new ManagedNoise { Protected = protectedStop };
        var paths = new List<string>();
        Task<HttpResponseMessage> Wrap(HttpRequestMessage request, JsonObject data, string version, string? error = null, int status = 200)
        {
            var response = Fixtures.Reply(request, data, error, status);
            var envelope = JsonNode.Parse(response.Content.ReadAsStringAsync().GetAwaiter().GetResult())!.AsObject();
            envelope["protocolVersion"] = version; response.Content = new StringContent(envelope.ToJsonString());
            return Task.FromResult(response);
        }
        async Task<HttpResponseMessage> Send(HttpRequestMessage request)
        {
            string path = request.RequestUri!.AbsolutePath;
            if (!path.Contains("/device/noise", StringComparison.Ordinal)) return await server.Send(request);
            paths.Add(path);
            var body = JsonNode.Parse(await request.Content!.ReadAsStringAsync())!.AsObject();
            if (path.EndsWith("/noise-exchange", StringComparison.Ordinal))
            {
                Assert.Equal("0.6", request.Headers.GetValues("X-NPEP-Version").Single());
                NpepNoiseProtocol.Validate("exchangeRequest", body);
                var command = new JsonObject { ["commandId"] = NpepProtocol.Id(), ["action"] = "STOP", ["instanceId"] = noise.Status["instanceId"]!.DeepClone(),
                    ["revision"] = noise.Status["revision"]!.DeepClone(), ["sessionId"] = noise.Status["sessionId"]!.DeepClone(),
                    ["durationSeconds"] = 60, ["expiresAt"] = DateTimeOffset.UtcNow.AddSeconds(30).ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'") };
                return await Wrap(request, new() { ["serverTime"] = DateTimeOffset.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'"),
                    ["command"] = command, ["acceptedReports"] = new JsonArray(), ["acceptedReceipts"] = new JsonArray() }, "0.6");
            }
            Assert.Equal("0.8", request.Headers.GetValues("X-NPEP-Version").Single());
            if (unsupported) return await Wrap(request, new(), "0.8", "NOT_FOUND", 404);
            if (path.EndsWith("/status", StringComparison.Ordinal))
            {
                NpepNoiseManagementProtocol.ValidateRequest("status", body);
                if (registrationRejected) return await Wrap(request, new(), "0.8", "INTERNAL_ERROR", 500);
                return await Wrap(request, new() { ["accepted"] = true }, "0.8");
            }
            NpepNoiseManagementProtocol.ValidateRequest("authorize", body);
            if (denied) return await Wrap(request, new(), "0.8", "MANAGEMENT_REQUIRED", 403);
            return await Wrap(request, new() { ["authorized"] = true, ["commandId"] = body["commandId"]!.DeepClone(),
                ["instanceId"] = body["instanceId"]!.DeepClone(), ["revision"] = body["revision"]!.DeepClone(),
                ["sessionId"] = wrongScope ? JsonValue.Create(NpepProtocol.Id()) : body["sessionId"]!.DeepClone() }, "0.8");
        }
        await using var runtime = new NpepRuntime(() => new(dir.Path, origin => new(origin, new Handler(Send))), "management-test", Sample);
        await Command(runtime, "inspect"); await Command(runtime, "pair", server); await Command(runtime, "poll"); await Command(runtime, "confirm", server);
        await Until(() => runtime.Snapshot().Connection == "ONLINE");
        if (wrongScope) Assert.Equal("INVALID_RESPONSE", (await Assert.ThrowsAsync<NpepException>(() => runtime.NoiseCycleAsync(noise, CancellationToken.None))).Code);
        else await runtime.NoiseCycleAsync(noise, CancellationToken.None);
        Assert.True(noise.Acknowledged); Assert.Equal(untrusted, noise.Untrusted); Assert.Equal(managed, noise.Managed);
        Assert.EndsWith("/noise-exchange", paths[0]); Assert.EndsWith("/noise-management/status", paths[1]);
        if (protectedStop) Assert.EndsWith("/noise-management/authorize", paths[2]);
        else Assert.Equal(2, paths.Count);
    }
    private sealed class ManagedNoise : INpepNoise, INpepNoiseManagement
    {
        public readonly JsonObject Status = new() { ["instanceId"] = NpepProtocol.Id(), ["revision"] = 1,
            ["sessionId"] = NpepProtocol.Id(), ["state"] = "Active", ["deviceName"] = "Synthetic", ["configured"] = true,
            ["startedAt"] = "2026-10-04T10:00:00.000Z", ["currentDbfs"] = -50, ["quality"] = "Good",
            ["summary"] = null, ["algorithm"] = "pcm-energy-v1", ["uploadError"] = null };
        public bool Acknowledged, Protected = true;
        public int Managed, Untrusted;
        public void Bind(string? scope) { }
        public JsonObject Observe() => Status.Copy();
        public JsonArray Reports() => [];
        public JsonArray Receipts() => [];
        public void Acknowledge(JsonObject response) => Acknowledged = true;
        public void Execute(JsonObject command, Action authorize) { authorize(); Untrusted++; }
        public void ExecuteManaged(JsonObject command, Action authorize) { authorize(); Managed++; }
        public bool RequiresManagement(JsonObject command) => Protected;
        public JsonObject ObserveProtection() => new() { ["instanceId"] = Status["instanceId"]!.DeepClone(), ["revision"] = 1,
            ["sessionId"] = Status["sessionId"]!.DeepClone(), ["protected"] = Protected,
            ["window"] = new JsonObject { ["start"] = "2026-10-04T19:00:00.000", ["end"] = "2026-10-04T20:00:00.000" } };
    }
}

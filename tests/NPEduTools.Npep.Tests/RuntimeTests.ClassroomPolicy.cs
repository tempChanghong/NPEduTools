using System.Text;
using System.Text.Json.Nodes;
using NPEduTools.Integrations.Npep;

namespace NPEduTools.Npep.Tests;

public sealed partial class RuntimeTests
{
    [Fact]
    public async Task Action_policy_receipt_retry_preserves_the_applied_revision_and_refuses_another_screen_identity()
    {
        using var dir = new TestDirectory();
        var server = new FakeServer();
        var school = new ClassroomPolicyObserver();
        var receipts = new List<JsonObject>();
        bool wrongBinding = false;
        JsonObject Identity()
        {
            var registration = server.Registration();
            var identity = new JsonObject();
            foreach (var field in new[] { "serverInstanceId", "deploymentEpoch", "deviceId", "bindingRevision", "credentialGeneration" })
                identity[field] = registration[field]!.DeepClone();
            return identity;
        }

        async Task<HttpResponseMessage> Send(HttpRequestMessage request)
        {
            if (!request.RequestUri!.AbsolutePath.Contains("/device/school-management", StringComparison.Ordinal))
                return await server.Send(request);
            Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
            string version = request.Headers.GetValues("X-NPEP-Version").Single();
            JsonObject data;
            if (request.Method == HttpMethod.Get)
            {
                var identity = Identity();
                if (wrongBinding) identity["bindingRevision"] = identity["bindingRevision"]!.GetValue<long>() + 1;
                data = new()
                {
                    ["identity"] = identity, ["credential"] = null,
                    ["equipment"] = new JsonObject { ["revision"] = 0L, ["locked"] = false, ["microphoneId"] = null, ["cameraId"] = null },
                    ["actionPolicy"] = new JsonObject { ["schemaVersion"] = 1, ["revision"] = 12L, ["policyId"] = new string('a', 64),
                        ["source"] = "GRADE", ["usbKeys"] = new JsonArray(), ["actions"] = new JsonObject {
                            ["exitBackground"] = new JsonObject { ["mode"] = "DENY", ["factor"] = null },
                            ["stopScheduledMonitoring"] = new JsonObject { ["mode"] = "ALLOW", ["factor"] = null } } }
                };
            }
            else
            {
                Assert.Equal(HttpMethod.Post, request.Method);
                Assert.EndsWith("/receipt", request.RequestUri.AbsolutePath);
                var payload = JsonNode.Parse(await request.Content!.ReadAsStringAsync())!.AsObject();
                Assert.Equal(request.Headers.GetValues("X-Request-Id").Single(), payload["requestId"]!.GetValue<string>());
                Assert.True(JsonNode.DeepEquals(Identity(), payload["context"]!["identity"]));
                receipts.Add(payload);
                if (receipts.Count == 1) throw new HttpRequestException("Synthetic lost response after receiving first receipt.");
                data = new() { ["confirmed"] = true };
            }
            var response = Fixtures.Reply(request, data);
            var envelope = JsonNode.Parse(await response.Content.ReadAsStringAsync())!.AsObject();
            envelope["protocolVersion"] = version;
            response.Content = new StringContent(envelope.ToJsonString(), Encoding.UTF8, "application/json");
            return response;
        }

        await using var runtime = new NpepRuntime(() => new(dir.Path, origin => new(origin, new Handler(Send))), "classroom-actions-test", Sample,
            schoolManagement: school);
        Assert.Null(Assert.Single(school.ObservedBindings));
        await Command(runtime, "inspect"); await Command(runtime, "pair", server); await Command(runtime, "poll"); await Command(runtime, "confirm", server);
        await Until(() => runtime.Snapshot().Connection == "ONLINE");
        await Assert.ThrowsAsync<HttpRequestException>(() => runtime.SchoolManagementCycleAsync(CancellationToken.None));
        await runtime.SchoolManagementCycleAsync(CancellationToken.None);
        Assert.Equal(2, receipts.Count);
        Assert.Equal(12, receipts[1]["actionPolicyReceipt"]!["revision"]!.GetValue<long>());
        Assert.True(JsonNode.DeepEquals(receipts[0]["actionPolicyReceipt"], receipts[1]["actionPolicyReceipt"]));
        Assert.NotEqual(receipts[0]["requestId"]!.GetValue<string>(), receipts[1]["requestId"]!.GetValue<string>());
        Assert.True(receipts[0]["sequence"]!.GetValue<long>() < receipts[1]["sequence"]!.GetValue<long>());
        wrongBinding = true;
        var mismatch = await Assert.ThrowsAsync<NpepException>(() => runtime.SchoolManagementCycleAsync(CancellationToken.None));
        Assert.Equal("SESSION_SUPERSEDED", mismatch.Code);
        Assert.Equal(2, school.Applications); Assert.Equal(2, receipts.Count);
        Assert.True(JsonNode.DeepEquals(Identity(), JsonNode.Parse(school.ObservedBindings.Last()!)));
        await Command(runtime, "pause");
        Assert.NotNull(school.ObservedBindings.Last());
        await Command(runtime, "unpair");
        Assert.Null(school.ObservedBindings.Last());
    }

    private sealed class ClassroomPolicyObserver : INpepSchoolManagement
    {
        private JsonObject? _applied;
        private string? _scope;
        public int Applications { get; private set; }
        public System.Collections.Concurrent.ConcurrentQueue<string?> ObservedBindings { get; } = new();
        public void ObserveActionBinding(string? scope) => ObservedBindings.Enqueue(scope);
        public void BindSchool(string? scope) => _scope = scope;
        public void ApplySchool(JsonObject policy)
        {
            Assert.Equal(_scope, policy["identity"]!.ToJsonString());
            _applied = policy["actionPolicy"]!.AsObject(); Applications++;
        }
        public JsonObject ObserveSchool() => new()
        {
            ["actionPolicyReceipt"] = new JsonObject { ["schemaVersion"] = 1, ["revision"] = _applied!["revision"]!.DeepClone(),
                ["policyId"] = _applied["policyId"]!.DeepClone(), ["error"] = null }
        };
    }
}

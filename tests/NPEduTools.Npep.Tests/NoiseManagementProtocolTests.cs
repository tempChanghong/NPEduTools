using System.Net;
using System.Text.Json.Nodes;
using NPEduTools.Integrations.Npep;

namespace NPEduTools.Npep.Tests;

public sealed class NoiseManagementProtocolTests
{
    public static IEnumerable<object[]> WireCases => JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "noise-management-wire-cases.json")))!.AsArray()
        .Select(c => new object[] { c!["name"]!.GetValue<string>(), c["definition"]!.GetValue<string>(), c["expected"]!.GetValue<bool>(), c["value"]!.ToJsonString() });
    [Theory] [MemberData(nameof(WireCases))]
    public void Shared_wire_cases_use_real_desktop_parser(string name, string definition, bool expected, string json)
    {
        var value = JsonNode.Parse(json)!.AsObject();
        var error = Record.Exception(() => NpepNoiseManagementProtocol.ValidateRequest(definition, value));
        if (expected) Assert.Null(error); else Assert.IsType<NpepException>(error);
        if (Environment.GetEnvironmentVariable("NPEP_NOISE_MANAGEMENT_FIXTURES") is { Length: > 0 } directory)
        {
            Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, name + ".json"), new JsonObject { ["name"] = name,
                ["definition"] = definition, ["acceptedByDesktop"] = error is null, ["value"] = value }.ToJsonString());
        }
    }
    internal static JsonObject Context() => new() {
        ["identity"] = new JsonObject { ["serverInstanceId"] = NpepProtocol.Id(), ["deploymentEpoch"] = NpepProtocol.Id(),
            ["deviceId"] = NpepProtocol.Id(), ["bindingRevision"] = 1, ["credentialGeneration"] = 1 },
        ["runId"] = NpepProtocol.Id(), ["sessionId"] = NpepProtocol.Id(), ["statusEpoch"] = 1, ["controlEpoch"] = NpepProtocol.Id()
    };
    internal static JsonObject Authorization() => new() { ["requestId"] = NpepProtocol.Id(), ["context"] = Context(),
        ["commandId"] = NpepProtocol.Id(), ["instanceId"] = NpepProtocol.Id(), ["revision"] = 1, ["sessionId"] = NpepProtocol.Id() };
    [Fact]
    public void Management_wire_is_independent_strict_and_correlates_all_authorized_fields()
    {
        var body = Authorization(); NpepNoiseManagementProtocol.ValidateRequest("authorize", body);
        var reply = new JsonObject { ["authorized"] = true, ["commandId"] = body["commandId"]!.DeepClone(),
            ["instanceId"] = body["instanceId"]!.DeepClone(), ["revision"] = body["revision"]!.DeepClone(), ["sessionId"] = body["sessionId"]!.DeepClone() };
        NpepNoiseManagementProtocol.ValidateReply("authorize", reply, body);
        foreach (string key in new[] { "commandId", "instanceId", "revision", "sessionId" })
        {
            var changed = reply.Copy(); changed[key] = key == "revision" ? JsonValue.Create(2) : JsonValue.Create(NpepProtocol.Id());
            Assert.Throws<NpepException>(() => NpepNoiseManagementProtocol.ValidateReply("authorize", changed, body));
        }
        body["isAdmin"] = true; Assert.Throws<NpepException>(() => NpepNoiseManagementProtocol.ValidateRequest("authorize", body));
        var status = new JsonObject { ["requestId"] = NpepProtocol.Id(), ["context"] = Context(), ["protection"] = new JsonObject {
            ["instanceId"] = NpepProtocol.Id(), ["revision"] = 0, ["sessionId"] = null, ["window"] = null, ["protected"] = false } };
        NpepNoiseManagementProtocol.ValidateRequest("status", status);
        status["protection"]!["window"] = new JsonObject { ["start"] = "2026-02-30T19:00:00.000", ["end"] = "2026-02-30T20:00:00.000" };
        Assert.Throws<NpepException>(() => NpepNoiseManagementProtocol.ValidateRequest("status", status));
    }
    [Theory]
    [InlineData(200, false, null)]
    [InlineData(200, true, "INVALID_RESPONSE")]
    [InlineData(403, false, "MANAGEMENT_REQUIRED")]
    [InlineData(404, false, "MANAGEMENT_UNSUPPORTED")]
    [InlineData(302, false, "REDIRECT_REJECTED")]
    public async Task Http_management_checks_capability_authentication_correlation_and_rejects_failure(int status, bool wrongScope, string? error)
    {
        var body = Authorization();
        using var api = new NpepApi("https://npep.test", new Handler(request => {
            Assert.Equal("/api/v2/npep/device/noise-management/authorize", request.RequestUri!.AbsolutePath);
            Assert.Equal("0.8", request.Headers.GetValues("X-NPEP-Version").Single());
            Assert.Equal("device-fixture", request.Headers.Authorization!.Parameter);
            var data = new JsonObject { ["authorized"] = true, ["commandId"] = body["commandId"]!.DeepClone(),
                ["instanceId"] = body["instanceId"]!.DeepClone(), ["revision"] = body["revision"]!.DeepClone(),
                ["sessionId"] = wrongScope ? JsonValue.Create(NpepProtocol.Id()) : body["sessionId"]!.DeepClone() };
            var response = Fixtures.Reply(request, data, status == 403 ? "MANAGEMENT_REQUIRED" : null, status);
            var envelope = JsonNode.Parse(response.Content.ReadAsStringAsync().GetAwaiter().GetResult())!.AsObject();
            envelope["protocolVersion"] = "0.8"; response.Content = new StringContent(envelope.ToJsonString());
            return Task.FromResult(response);
        }));
        if (error is null) Assert.True((await api.NoiseManagementAsync("device-fixture", "authorize", body, CancellationToken.None))["authorized"]!.GetValue<bool>());
        else Assert.Equal(error, (await Assert.ThrowsAsync<NpepException>(() => api.NoiseManagementAsync("device-fixture", "authorize", body, CancellationToken.None))).Code);
    }
}

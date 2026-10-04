using System.Net;
using System.Text.Json.Nodes;
using NPEduTools.Integrations.Npep;

namespace NPEduTools.Npep.Tests;

public sealed class NoiseDisplayProtocolTests
{
    [Fact]
    public void Shared_KV_examples_and_negative_variants_use_the_actual_desktop_parser()
    {
        var examples = JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "noise-display-presence-wire-cases.json")))!.AsObject();
        var results = new JsonArray();
        void Check(string name, string action, JsonObject body, bool accepted)
        {
            var error = Record.Exception(() => NpepNoiseDisplayProtocol.ValidateRequest(action, body));
            Assert.Equal(accepted, error is null);
            results.Add(new JsonObject { ["name"] = name, ["definition"] = action == "return" ? "deviceReturn" : action,
                ["value"] = body.DeepClone(), ["acceptedByDesktop"] = error is null });
        }
        foreach (string action in new[] { "observe", "return" })
        {
            var body = examples[action == "observe" ? "deviceObserve" : "deviceReturn"]!.AsObject();
            Check(action + "-valid", action, body.Copy(), true);
            var b = body.Copy(); b["context"]!["controlEpoch"] = Guid.Empty.ToString("D"); Check(action + "-empty-control-epoch", action, b, true);
            foreach (string field in new[] { "requestId", "instanceId", "captureSessionId" })
            { b = body.Copy(); b[field] = Guid.Empty.ToString("D"); Check(action + "-bad-" + field, action, b, false); }
            b = body.Copy(); b["revision"] = -1; Check(action + "-negative-revision", action, b, false);
            b = body.Copy(); b["revision"] = 9007199254740992L; Check(action + "-unsafe-revision", action, b, false);
            b = body.Copy(); b["isAdmin"] = true; Check(action + "-extra", action, b, false);
            b = body.Copy(); b["window"]!["start"] = "2026-02-30T19:00:00.000"; Check(action + "-bad-calendar", action, b, false);
        }
        var intent = examples["deviceReturn"]!.AsObject().Copy(); intent.Remove("returnMinutes"); Check("return-incomplete-offline", "return", intent, false);
        intent = examples["deviceReturn"]!.AsObject().Copy(); intent["offlineStartedAt"] = "2026-10-04T19:01:00Z"; Check("return-inexact-utc", "return", intent, false);
        NpepNoiseDisplayProtocol.ValidateReply(examples["deviceReply"]!.AsObject(), examples["deviceObserve"]!.AsObject());
        if (Environment.GetEnvironmentVariable("NPEP_NOISE_PRESENCE_FIXTURES") is { Length: > 0 } directory)
        {
            Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, "parser-results.json"), results.ToJsonString());
        }
    }
    internal static JsonObject Request() => new() { ["requestId"] = NpepProtocol.Id(), ["context"] = NoiseManagementProtocolTests.Context(),
        ["instanceId"] = NpepProtocol.Id(), ["revision"] = 0, ["captureSessionId"] = NpepProtocol.Id(),
        ["window"] = new JsonObject { ["start"] = "2026-10-04T19:00:00.000", ["end"] = "2026-10-04T20:00:00.000" } };
    internal static JsonObject Reply() => new() { ["supported"] = true, ["serverNow"] = "2026-10-04T11:00:00.000Z", ["returnMinutes"] = 10,
        ["presence"] = new JsonObject { ["state"] = "UNKNOWN", ["ageMs"] = null }, ["activeReturn"] = null };
    [Fact]
    public void Presence_uses_unchanged_empty_control_epoch_and_strict_independent_fields()
    {
        var r = Request(); r["context"]!["controlEpoch"] = Guid.Empty.ToString("D"); NpepNoiseDisplayProtocol.ValidateRequest("observe", r);
        foreach (string action in new[] { "start", "stop", "status", "authorize" })
            Assert.Throws<NpepException>(() => NpepNoiseDisplayProtocol.ValidateRequest(action, r));
        r["isAdmin"] = true; Assert.Throws<NpepException>(() => NpepNoiseDisplayProtocol.ValidateRequest("observe", r));
    }
    [Theory]
    [InlineData("2026-02-30T19:00:00.000")]
    [InlineData("2026-10-04T19:00:00.000Z")]
    [InlineData("2026-10-04T19:00:00")]
    public void Invalid_school_calendar_rejected(string start)
    {
        var r = Request(); r["window"]!["start"] = start;
        Assert.Throws<NpepException>(() => NpepNoiseDisplayProtocol.ValidateRequest("observe", r));
    }
    [Fact]
    public void Offline_intent_requires_both_original_start_and_bounded_minutes_and_only_return_route()
    {
        var r = Request(); r["offlineStartedAt"] = "2026-10-04T11:00:00.000Z";
        Assert.Throws<NpepException>(() => NpepNoiseDisplayProtocol.ValidateRequest("return", r));
        r["returnMinutes"] = 10; NpepNoiseDisplayProtocol.ValidateRequest("return", r);
        Assert.Throws<NpepException>(() => NpepNoiseDisplayProtocol.ValidateRequest("observe", r));
        r["returnMinutes"] = 61; Assert.Throws<NpepException>(() => NpepNoiseDisplayProtocol.ValidateRequest("return", r));
    }
    [Theory]
    [InlineData("UNKNOWN", null, true)]
    [InlineData("DISPLAY_VISIBLE", 0, true)]
    [InlineData("BLOCKED", 15000, true)]
    [InlineData("UNKNOWN", 0, false)]
    [InlineData("HIDDEN", null, false)]
    [InlineData("DISPLAY_VISIBLE", -1, false)]
    [InlineData("STOPPED", 0, false)]
    public void Diagnostic_states_are_bounded_and_unknown_age_is_explicit(string state, int? age, bool valid)
    {
        var reply = Reply(); reply["presence"]!["state"] = state; reply["presence"]!["ageMs"] = age;
        var error = Record.Exception(() => NpepNoiseDisplayProtocol.ValidateReply(reply, Request()));
        if (valid) Assert.Null(error); else Assert.IsType<NpepException>(error);
    }
    [Theory]
    [InlineData(200, false, null)]
    [InlineData(200, true, "INVALID_RESPONSE")]
    [InlineData(404, false, "DISPLAY_UNSUPPORTED")]
    [InlineData(426, false, "DISPLAY_UNSUPPORTED")]
    [InlineData(302, false, "REDIRECT_REJECTED")]
    public async Task Http_checks_version_request_correlation_auth_and_old_service_behavior(int status, bool wrongId, string? error)
    {
        var r = Request();
        using var api = new NpepApi("https://npep.test", new Handler(request => {
            Assert.Equal("/api/v2/npep/device/noise-display/observe", request.RequestUri!.AbsolutePath);
            Assert.Equal("0.9", request.Headers.GetValues("X-NPEP-Version").Single());
            Assert.Equal("device-fixture", request.Headers.Authorization!.Parameter);
            var response = Fixtures.Reply(request, Reply(), status: status);
            var envelope = JsonNode.Parse(response.Content.ReadAsStringAsync().GetAwaiter().GetResult())!.AsObject();
            envelope["protocolVersion"] = "0.9"; if (wrongId) envelope["requestId"] = NpepProtocol.Id();
            response.Content = new StringContent(envelope.ToJsonString()); return Task.FromResult(response);
        }));
        if (error is null) Assert.True((await api.NoiseDisplayAsync("device-fixture", "observe", r, CancellationToken.None))["supported"]!.GetValue<bool>());
        else Assert.Equal(error, (await Assert.ThrowsAsync<NpepException>(() => api.NoiseDisplayAsync("device-fixture", "observe", r, CancellationToken.None))).Code);
    }
}

using System.Net;
using System.Text.Json.Nodes;
using NPEduTools.Integrations.Npep;

namespace NPEduTools.Npep.Tests;

public sealed partial class RuntimeTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task Display_cycle_correlates_context_and_keeps_return_intent_until_valid_reply(bool pendingReturn, bool invalidReply)
    {
        using var dir = new TestDirectory(); var server = new FakeServer(); var display = new DisplayFixture(pendingReturn);
        int observations = 0;
        async Task<HttpResponseMessage> Send(HttpRequestMessage request)
        {
            if (!request.RequestUri!.AbsolutePath.Contains("/device/noise-display/", StringComparison.Ordinal)) return await server.Send(request);
            observations++;
            var body = JsonNode.Parse(await request.Content!.ReadAsStringAsync())!.AsObject();
            Assert.EndsWith(pendingReturn ? "/return" : "/observe", request.RequestUri.AbsolutePath);
            NpepNoiseDisplayProtocol.ValidateRequest(pendingReturn ? "return" : "observe", body);
            Assert.NotNull(body["context"]?["identity"]);
            if (pendingReturn) Assert.Equal(display.Intent!["requestId"]!.GetValue<string>(), body.Text("requestId"));
            var data = NoiseDisplayProtocolTests.Reply();
            if (pendingReturn) data["activeReturn"] = new JsonObject { ["requestId"] = body["requestId"]!.DeepClone(),
                ["window"] = body["window"]!.DeepClone(), ["startedAt"] = "2026-10-04T11:00:00.000Z",
                ["expiresAt"] = "2026-10-04T11:05:00.000Z", ["returnMinutes"] = 5, ["remainingSeconds"] = 300 };
            if (invalidReply) data["presence"]!["ageMs"] = -1;
            var response = Fixtures.Reply(request, data);
            var envelope = JsonNode.Parse(await response.Content.ReadAsStringAsync())!.AsObject();
            envelope["protocolVersion"] = "0.9"; response.Content = new StringContent(envelope.ToJsonString()); return response;
        }
        await using var runtime = new NpepRuntime(() => new(dir.Path, origin => new(origin, new Handler(Send))), "display-test", Sample);
        await Command(runtime, "inspect"); await Command(runtime, "pair", server); await Command(runtime, "poll"); await Command(runtime, "confirm", server);
        await Until(() => runtime.Snapshot().Connection == "ONLINE");
        if (invalidReply) await Assert.ThrowsAsync<NpepException>(() => runtime.NoiseDisplayCycleAsync(display, CancellationToken.None));
        else await runtime.NoiseDisplayCycleAsync(display, CancellationToken.None);
        Assert.Equal(1, observations); Assert.Equal(invalidReply ? 0 : 1, display.Confirmed);
        Assert.NotNull(display.Scope); if (invalidReply) Assert.Equal(0, display.Confirmed);
    }
    private sealed class DisplayFixture(bool pending) : INpepNoiseDisplay
    {
        public string? Scope;
        public int Confirmed;
        public readonly JsonObject? Intent = pending ? new() { ["requestId"] = NpepProtocol.Id(),
            ["offlineStartedAt"] = "2026-10-04T11:00:00.000Z", ["returnMinutes"] = 5 } : null;
        public void BindDisplay(string? scope) => Scope = scope;
        public JsonObject? ObserveDisplay()
        { var r = NoiseDisplayProtocolTests.Request(); r.Remove("requestId"); r.Remove("context"); return r; }
        public JsonObject? PendingDisplayReturn() => Intent?.Copy();
        public void ConfirmDisplay(JsonObject request, JsonObject reply, double elapsedSeconds) { Assert.True(elapsedSeconds >= 0); Confirmed++; }
        public void DisplayFailed(string code) { }
    }
}

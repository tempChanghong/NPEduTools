using System.Net;
using System.Text.Json.Nodes;
using NPEduTools.Integrations.Npep;

namespace NPEduTools.Npep.Tests;

public sealed partial class RuntimeTests
{
    private sealed class SequencedSeatingFixture : INpepStudySettings
    {
        public int Applied;
        private readonly string _app = NpepProtocol.Id();
        public void BindStudy(string? scope) { }
        public void ApplyStudy(JsonObject configuration) { }
        public JsonObject? ObserveStudy() => null;
        public void StudyFailed(string code) => throw new InvalidOperationException("Seating failure cannot stop study capture.");
        public void ApplySeating(JsonObject manifest, byte[]? bytes) { Assert.Null(manifest["asset"]); Applied++; }
        public JsonObject? ObserveSeating() => new() { ["appInstanceId"] = _app, ["sequence"] = 1, ["hostSequence"] = 7,
            ["revision"] = 0, ["appliedHash"] = null, ["visible"] = false, ["renderAgeMs"] = null };
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Seating_host_sequence_requires_explicit_server_negotiation_and_recovers_after_server_rollback(bool modern)
    {
        using var directory = new TestDirectory(); var server = new FakeServer(); var study = new SequencedSeatingFixture();
        var sent = new List<long?>(); bool rejectSequence = false;
        async Task<HttpResponseMessage> Send(HttpRequestMessage request)
        {
            if (!request.RequestUri!.AbsolutePath.EndsWith("/study-settings/seating", StringComparison.Ordinal)) return await server.Send(request);
            Assert.Equal("1", request.Headers.GetValues("X-NPEP-Seating-Sequence").Single());
            var body = JsonNode.Parse(await request.Content!.ReadAsStringAsync())!.AsObject();
            NpepSeatingProtocol.Validate("manifestRequest", body);
            long? sequence = body["observation"]?["hostSequence"]?.GetValue<long>(); sent.Add(sequence);
            var data = new JsonObject { ["version"] = body["version"]!.DeepClone(), ["classId"] = "one", ["revision"] = 0, ["asset"] = null };
            if (modern && !rejectSequence) data["supportsHostSequence"] = true;
            var response = Fixtures.Reply(request, data, rejectSequence && sequence.HasValue ? "INVALID_REQUEST" : null,
                rejectSequence && sequence.HasValue ? 400 : 200);
            var envelope = JsonNode.Parse(await response.Content.ReadAsStringAsync())!.AsObject(); envelope["protocolVersion"] = "0.14";
            response.Content = new StringContent(envelope.ToJsonString()); return response;
        }
        await using var runtime = new NpepRuntime(() => new(directory.Path, origin => new(origin, new Handler(Send))), "seating-sequence-test", Sample);
        await Command(runtime, "inspect"); await Command(runtime, "pair", server); await Command(runtime, "poll"); await Command(runtime, "confirm", server);
        await Until(() => runtime.Snapshot().Connection == "ONLINE");
        var input = new JsonObject { ["version"] = new string('a', 64) };
        await runtime.SeatingCycleAsync(study, input, CancellationToken.None);
        await runtime.SeatingCycleAsync(study, input, CancellationToken.None);
        Assert.Null(sent[0]); Assert.Equal(modern ? 7L : null, sent[1]);
        Assert.Equal(2, study.Applied);
        if (modern) {
            rejectSequence = true;
            Assert.Equal("INVALID_REQUEST", (await Assert.ThrowsAsync<NpepException>(() => runtime.SeatingCycleAsync(study, input, CancellationToken.None))).Code);
            Assert.Equal(2, study.Applied);
            await runtime.SeatingCycleAsync(study, input, CancellationToken.None);
            await runtime.SeatingCycleAsync(study, input, CancellationToken.None);
            Assert.Equal(new long?[] { null, 7, 7, null, null }, sent);
            rejectSequence = false;
            await runtime.SeatingCycleAsync(study, input, CancellationToken.None);
            await runtime.SeatingCycleAsync(study, input, CancellationToken.None);
            Assert.Null(sent[^2]); Assert.Equal(7, sent[^1]);
        }
        Assert.Equal("ONLINE", runtime.Snapshot().Connection);
    }
}

using System.Net;
using System.Text.Json.Nodes;
using NPEduTools.Integrations.Npep;

namespace NPEduTools.Npep.Tests;

public sealed partial class RuntimeTests
{
    private sealed class HomeworkFixture : INpepStudySettings
    {
        public int Applied;
        public void BindStudy(string? scope) { }
        public void ApplyStudy(JsonObject configuration) { }
        public JsonObject? ObserveStudy() => null;
        public void StudyFailed(string code) => throw new InvalidOperationException("Homework failure cannot withdraw the capture policy.");
        public void ApplyStudyHomework(JsonObject payload, byte[] bytes, string snapshotId) { NpepStudyProtocol.Validate("homeworkSnapshot",payload); Applied++; }
    }
    [Theory]
    [InlineData("valid")] [InlineData("hash")] [InlineData("index")] [InlineData("mixed")] [InlineData("date")] [InlineData("pause")] [InlineData("unsupported")]
    public async Task Homework_cycle_uses_independent_version_and_rejects_mixed_or_late_chunks(string mode)
    {
        using var dir = new TestDirectory(); var server = new FakeServer(); var study = new HomeworkFixture(); NpepRuntime? active = null;
        var payload = new JsonObject { ["version"] = new string('a',64), ["date"] = mode == "date" ? "2026-10-13" : "2026-10-12",
            ["items"] = new JsonArray(new JsonObject { ["id"] = "long", ["revision"] = 1, ["subject"] = "物理", ["title"] = "练习册",
                ["content"] = new string('作', 6000) + "\n🙂下一行", ["sources"] = new JsonArray(new JsonObject { ["id"] = "one", ["name"] = "行政一班" }) }) };
        var bytes = System.Text.Encoding.UTF8.GetBytes(payload.ToJsonString());
        var id = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes)); int chunks = (bytes.Length + 16383) / 16384;
        async Task<HttpResponseMessage> Send(HttpRequestMessage request)
        {
            if (!request.RequestUri!.AbsolutePath.EndsWith("/study-settings/homework", StringComparison.Ordinal)) return await server.Send(request);
            Assert.Equal("0.12", request.Headers.GetValues("X-NPEP-Version").Single());
            var body = JsonNode.Parse(await request.Content!.ReadAsStringAsync())!.AsObject(); NpepStudyProtocol.Validate("homeworkRequest", body);
            int index = body["index"]!.GetValue<int>(); Assert.Equal("2026-10-12", body.Text("date"));
            if (mode == "pause") Assert.Equal("Accepted", active!.Handle(new(1, Guid.NewGuid(), "npep.command",
                Npep: new("pause", active.Snapshot().Revision))).Outcome);
            var reply = Fixtures.Reply(request, new() { ["snapshotId"] = mode == "hash" || mode == "mixed" && index > 0 ? new string('b',64) : id,
                ["index"] = mode == "index" ? index + 1 : index, ["count"] = chunks,
                ["data"] = Convert.ToBase64String(bytes, index * 16384, Math.Min(16384, bytes.Length - index * 16384)) },
                mode == "unsupported" ? "PROTOCOL_UNSUPPORTED" : null, mode == "unsupported" ? 426 : 200);
            var envelope = JsonNode.Parse(await reply.Content.ReadAsStringAsync())!.AsObject(); envelope["protocolVersion"] = "0.12";
            reply.Content = new StringContent(envelope.ToJsonString()); return reply;
        }
        await using var runtime = new NpepRuntime(() => new(dir.Path, origin => new(origin, new Handler(Send))), "homework-test", Sample); active = runtime;
        await Command(runtime,"inspect"); await Command(runtime,"pair",server); await Command(runtime,"poll"); await Command(runtime,"confirm",server);
        await Until(() => runtime.Snapshot().Connection == "ONLINE");
        var input = new JsonObject { ["version"] = new string('a',64), ["date"] = "2026-10-12" };
        if (mode == "valid") { await runtime.StudyHomeworkCycleAsync(study,input,CancellationToken.None); Assert.Equal(1,study.Applied); }
        else { await Assert.ThrowsAsync<NpepException>(() => runtime.StudyHomeworkCycleAsync(study,input,CancellationToken.None)); Assert.Equal(0,study.Applied); }
    }
}

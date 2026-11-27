using System.Net;
using System.Text.Json.Nodes;
using NPEduTools.Integrations.Npep;

namespace NPEduTools.Npep.Tests;

public sealed partial class RuntimeTests
{
    private sealed class HomeworkFixture : INpepStudySettings
    {
        public int Applied;
        public string? AppliedId;
        public JsonObject? Request;
        public volatile string? HomeworkError;
        public void BindStudy(string? scope) { }
        public void ApplyStudy(JsonObject configuration) { }
        public JsonObject? ObserveStudy() => null;
        public void StudyFailed(string code) { if (Request is null) throw new InvalidOperationException("Homework failure cannot withdraw the capture policy."); }
        public JsonObject? StudyHomeworkRequest() => Request?.Copy();
        public void StudyHomeworkFailed(string code) => HomeworkError = code;
        public void ApplyStudyHomework(JsonObject payload, byte[] bytes, string snapshotId)
        { NpepStudyProtocol.Validate("homeworkSnapshot",payload); AppliedId = snapshotId; Interlocked.Increment(ref Applied); }
    }

    [Fact]
    public async Task Background_homework_refresh_honors_retry_after_and_recovers_before_normal_refresh_interval()
    {
        using var dir = new TestDirectory(); var server = new FakeServer(); var time = new ReceiptTime();
        using (var paired = new NpepDevice(dir.Path, server.Api)) await server.ActivateAsync(paired);
        var study = new HomeworkFixture { Request = new() { ["version"] = new string('a',64), ["date"] = "2026-10-12" } };
        var current = HomeworkBytes(1);
        int reads = 0;
        async Task<HttpResponseMessage> Send(HttpRequestMessage request)
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith("/study-settings", StringComparison.Ordinal))
            {
                var configuration = StudyProtocolTests.Configuration;
                configuration["identity"] = NpepDevice.RegistrationIdentity(server.Registration());
                return await HomeworkReply(request, configuration, version: "0.10");
            }
            if (!path.EndsWith("/study-settings/homework", StringComparison.Ordinal)) return await server.Send(request);
            if (Interlocked.Increment(ref reads) == 1)
            {
                var response = await HomeworkReply(request, new(), "RATE_LIMITED", 429);
                response.Headers.RetryAfter = new(TimeSpan.FromSeconds(9));
                return response;
            }
            var body = JsonNode.Parse(await request.Content!.ReadAsStringAsync())!.AsObject();
            return await HomeworkReply(request, HomeworkChunk(current, body["index"]!.GetValue<int>()));
        }
        await using var runtime = new NpepRuntime(() => new(dir.Path, origin => new(origin, new Handler(Send))),
            "homework-short-retry", Sample, study: study, receiptTime: time);
        await Until(() => study.HomeworkError == "HOMEWORK_REFRESH_UNAVAILABLE", TimeSpan.FromSeconds(14));
        time.Advance(TimeSpan.FromSeconds(6));
        await Task.Delay(3250);
        Assert.Equal(1, Volatile.Read(ref reads));
        time.Advance(TimeSpan.FromSeconds(3));
        await Until(() => Volatile.Read(ref study.Applied) == 1);
        Assert.Equal(3, Volatile.Read(ref reads));
        Assert.Equal(current.Id, study.AppliedId);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Changed_homework_snapshot_restarts_once_without_publishing_half_of_either_version(bool changesAgain)
    {
        using var dir = new TestDirectory(); var server = new FakeServer(); var study = new HomeworkFixture();
        var old = HomeworkBytes(1); var current = HomeworkBytes(2);
        var seen = new List<(int Index, string? Snapshot)>();
        async Task<HttpResponseMessage> Send(HttpRequestMessage request)
        {
            if (!request.RequestUri!.AbsolutePath.EndsWith("/study-settings/homework")) return await server.Send(request);
            var body = JsonNode.Parse(await request.Content!.ReadAsStringAsync())!.AsObject();
            int index = body["index"]!.GetValue<int>();
            seen.Add((index, body["snapshotId"]?.GetValue<string>()));
            if (seen.Count == 2 || changesAgain && seen.Count == 4)
                return await HomeworkReply(request, new(), "HOMEWORK_CHANGED", 409);
            return await HomeworkReply(request, HomeworkChunk(seen.Count == 1 ? old : current, index));
        }
        using (var device = new NpepDevice(dir.Path, server.Api)) await server.ActivateAsync(device);
        await using var runtime = new NpepRuntime(() => new(dir.Path, origin => new(origin, new Handler(Send))), "homework-changed", Sample);
        await Until(() => runtime.Snapshot().Connection == "ONLINE", TimeSpan.FromSeconds(16));
        var request = new JsonObject { ["version"] = new string('a',64), ["date"] = "2026-10-12" };
        if (changesAgain)
        {
            Assert.Equal("HOMEWORK_CHANGED", (await Assert.ThrowsAsync<NpepException>(() => runtime.StudyHomeworkCycleAsync(study, request, default))).Code);
            Assert.Equal(0, study.Applied);
        }
        else
        {
            await runtime.StudyHomeworkCycleAsync(study, request, default);
            Assert.Equal(1, study.Applied);
            Assert.Equal(current.Id, study.AppliedId);
        }
        Assert.Equal(new (int, string?)[] { (0, null), (1, old.Id), (0, null), (1, current.Id) }, seen);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_slow_homework_chunk_does_not_hold_status_and_a_late_result_cannot_survive_pause(bool denied)
    {
        using var dir = new TestDirectory(); var server = new FakeServer(); var study = new HomeworkFixture();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var payload = HomeworkBytes(1);
        NpepDevice? native = null;
        async Task<HttpResponseMessage> Send(HttpRequestMessage request)
        {
            if (!request.RequestUri!.AbsolutePath.EndsWith("/study-settings/homework")) return await server.Send(request);
            entered.TrySetResult(); await release.Task;
            return denied ? await HomeworkReply(request, new(), "AUTH_INVALID", 401) : await HomeworkReply(request, HomeworkChunk(payload, 0));
        }
        using (var paired = new NpepDevice(dir.Path, server.Api)) await server.ActivateAsync(paired);
        await using var runtime = new NpepRuntime(() => native = new(dir.Path, origin => new(origin, new Handler(Send))), "homework-slow", Sample);
        await Until(() => runtime.Snapshot().Connection == "ONLINE", TimeSpan.FromSeconds(16));
        var input = new JsonObject { ["version"] = new string('a',64), ["date"] = "2026-10-12" };
        var fetching = runtime.StudyHomeworkCycleAsync(study, input, default);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(4));
            var receipt = await native!.ReportAsync(NpepStatusReader.Map(Sample(), "homework-slow"), 0).WaitAsync(TimeSpan.FromSeconds(2));
            Assert.Equal(2, receipt.Number("acceptedSequence"));
            Assert.True((await Command(runtime, "pause")).ReportingPaused);
            Assert.False((await Command(runtime, "resume")).ReportingPaused);
            release.TrySetResult();
            Assert.Equal("SESSION_SUPERSEDED", (await Assert.ThrowsAsync<NpepException>(() => fetching)).Code);
            Assert.Equal("ACTIVE", native.View().Text("state"));
            Assert.Equal(0, study.Applied);
        }
        finally
        {
            release.TrySetResult();
            if (!fetching.IsCompleted) try { await fetching; } catch (NpepException) { }
        }
    }

    private static (string Id, byte[] Bytes) HomeworkBytes(int revision)
    {
        var content = new JsonObject { ["version"] = new string('a',64), ["date"] = "2026-10-12",
            ["items"] = new JsonArray(new JsonObject { ["id"] = "long", ["revision"] = revision, ["subject"] = "物理", ["title"] = "练习册",
                ["content"] = new string('作', 6000), ["sources"] = new JsonArray(new JsonObject { ["id"] = "one", ["name"] = "行政一班" }) }) };
        var bytes = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(content, new System.Text.Json.JsonSerializerOptions
            { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
        return (Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes)), bytes);
    }

    private static JsonObject HomeworkChunk((string Id, byte[] Bytes) payload, int index) => new()
    {
        ["snapshotId"] = payload.Id, ["index"] = index, ["count"] = (payload.Bytes.Length + 16383) / 16384,
        ["data"] = Convert.ToBase64String(payload.Bytes, index * 16384, Math.Min(16384, payload.Bytes.Length - index * 16384))
    };

    private static async Task<HttpResponseMessage> HomeworkReply(HttpRequestMessage request, JsonObject data, string? error = null, int status = 200, string version = "0.12")
    {
        var reply = Fixtures.Reply(request, data, error, status);
        var envelope = JsonNode.Parse(await reply.Content.ReadAsStringAsync())!.AsObject();
        envelope["protocolVersion"] = version;
        reply.Content = new StringContent(envelope.ToJsonString()); return reply;
    }
    [Theory]
    [InlineData("valid")] [InlineData("manual")] [InlineData("hash")] [InlineData("index")] [InlineData("mixed")] [InlineData("date")] [InlineData("pause")] [InlineData("unsupported")]
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
            Assert.Equal(mode == "manual" ? "Manual" : null, body["purpose"]?.GetValue<string>());
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
        if (mode == "manual") input["purpose"] = "Manual";
        if (mode is "valid" or "manual") { await runtime.StudyHomeworkCycleAsync(study,input,CancellationToken.None); Assert.Equal(1,study.Applied); }
        else { await Assert.ThrowsAsync<NpepException>(() => runtime.StudyHomeworkCycleAsync(study,input,CancellationToken.None)); Assert.Equal(0,study.Applied); }
    }
}

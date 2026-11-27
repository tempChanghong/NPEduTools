using System.Security.Cryptography;
using System.Text.Json.Nodes;
using NPEduTools.Integrations.Npep;

namespace NPEduTools.Npep.Tests;

public sealed partial class RuntimeTests
{
    [Fact]
    public async Task Background_seating_sync_honors_retry_after_without_blocking_status()
    {
        using var directory = new TestDirectory();
        var server = new FakeServer();
        var time = new ReceiptTime();
        string version = new('a', 64);
        var study = new SeatingFixture { Request = new() { ["version"] = version } };
        int manifests = 0;
        NpepDevice? native = null;
        async Task<HttpResponseMessage> Send(HttpRequestMessage request)
        {
            string path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith("/study-settings", StringComparison.Ordinal))
            {
                var configuration = StudyProtocolTests.Configuration;
                configuration["identity"] = NpepDevice.RegistrationIdentity(server.Registration());
                return await SeatingReply(request, configuration, version: "0.10");
            }
            if (!path.EndsWith("/study-settings/seating", StringComparison.Ordinal)) return await server.Send(request);
            if (Interlocked.Increment(ref manifests) == 1)
            {
                var limited = await SeatingReply(request, new(), "RATE_LIMITED", 429);
                limited.Headers.RetryAfter = new(TimeSpan.FromSeconds(9));
                return limited;
            }
            return await SeatingReply(request, new JsonObject { ["version"] = version, ["classId"] = "class-one",
                ["revision"] = 1, ["asset"] = null });
        }
        using (var paired = new NpepDevice(directory.Path, server.Api)) await server.ActivateAsync(paired);
        await using var runtime = new NpepRuntime(() => native = new(directory.Path, origin => new(origin, new Handler(Send))),
            "seating-retry-after", Sample, study: study, receiptTime: time);
        await Until(() => study.Error == "RATE_LIMITED", TimeSpan.FromSeconds(14));
        time.Advance(TimeSpan.FromSeconds(6));
        var receipt = await native!.ReportAsync(NpepStatusReader.Map(Sample(), "seating-retry-after"), 0).WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(2, receipt.Number("acceptedSequence"));
        await Task.Delay(3250);
        Assert.Equal(1, Volatile.Read(ref manifests));
        time.Advance(TimeSpan.FromSeconds(3));
        await Until(() => Volatile.Read(ref study.Withdrawals) == 1);
        Assert.Equal(2, Volatile.Read(ref manifests));
    }

    [Theory]
    [InlineData("network")] [InlineData("deadline")] [InlineData("replace")] [InlineData("withdraw")] [InlineData("corrupt")]
    [InlineData("capability")] [InlineData("pause-resume")] [InlineData("revision-only")]
    public async Task Seating_retry_resumes_verified_chunks_but_never_mixes_a_replacement(string change)
    {
        using var directory = new TestDirectory(); var server = new FakeServer();
        byte[] image = Enumerable.Repeat((byte)42, 40000).ToArray();
        string hash = Convert.ToHexStringLower(SHA256.HashData(image));
        var indices = new List<int>(); bool fail = true, withdrawn = false, corrupt = false, hostSequence = false; int revision = 1, budgetCalls = 0;
        string version = new('a', 64);
        async Task<HttpResponseMessage> Send(HttpRequestMessage request)
        {
            if (!request.RequestUri!.AbsolutePath.Contains("/study-settings/seating", StringComparison.Ordinal)) return await server.Send(request);
            var body = JsonNode.Parse(await request.Content!.ReadAsStringAsync())!.AsObject();
            JsonObject data;
            if (request.RequestUri.AbsolutePath.EndsWith("/chunk", StringComparison.Ordinal))
            {
                int index = body["index"]!.GetValue<int>(); indices.Add(index);
                // Each synthetic cycle permits only two chunk responses, just as
                // a slow connection may exhaust the fixed transfer deadline.
                if (change == "deadline" && ++budgetCalls > 2) throw new OperationCanceledException("Synthetic transfer deadline");
                if (fail && index == 2 && change != "deadline")
                {
                    fail = false;
                    throw new HttpRequestException("Synthetic interrupted image transfer");
                }
                var part = image.AsSpan(index * 16384, Math.Min(16384, image.Length - index * 16384)).ToArray();
                if (corrupt && index == 2) part[^1] ^= 1;
                data = new() { ["revision"] = revision, ["hash"] = hash, ["index"] = index, ["count"] = 3,
                    ["data"] = Convert.ToBase64String(part) };
            }
            else data = new() { ["version"] = version, ["classId"] = "class-one", ["revision"] = revision,
                ["asset"] = withdrawn ? null : new JsonObject { ["hash"] = hash, ["width"] = 100, ["height"] = 100, ["byteCount"] = image.Length, ["chunks"] = 3 } };
            if (hostSequence && !request.RequestUri.AbsolutePath.EndsWith("/chunk", StringComparison.Ordinal)) data["supportsHostSequence"] = true;
            return await SeatingReply(request, data);
        }
        using (var device = new NpepDevice(directory.Path, server.Api)) await server.ActivateAsync(device);
        await using var runtime = new NpepRuntime(() => new(directory.Path, origin => new(origin, new Handler(Send))), "seating-recovery", Sample);
        await Until(() => runtime.Snapshot().Connection == "ONLINE");
        var study = new SeatingFixture(); var wanted = new JsonObject { ["version"] = version };
        var error = await Record.ExceptionAsync(() => runtime.SeatingCycleAsync(study, wanted, CancellationToken.None));
        Assert.IsType(change == "deadline" ? typeof(OperationCanceledException) : typeof(HttpRequestException), error);
        Assert.Null(study.Image); Assert.Equal(new[] { 0, 1, 2 }, indices);
        indices.Clear(); budgetCalls = 0;
        if (change == "capability") hostSequence = true;
        if (change == "revision-only") revision++;
        if (change == "pause-resume")
        {
            Assert.True((await Command(runtime, "pause")).ReportingPaused);
            Assert.False((await Command(runtime, "resume")).ReportingPaused);
            await Until(() => runtime.Snapshot().Connection == "ONLINE");
        }
        if (change == "replace") { revision = 2; image = Enumerable.Repeat((byte)99, 40000).ToArray(); hash = Convert.ToHexStringLower(SHA256.HashData(image)); }
        if (change == "withdraw") { revision = 2; withdrawn = true; }
        if (change == "corrupt")
        {
            corrupt = true;
            Assert.Equal("INVALID_RESPONSE", (await Assert.ThrowsAsync<NpepException>(() => runtime.SeatingCycleAsync(study, wanted, CancellationToken.None))).Code);
            Assert.Null(study.Image); corrupt = false; indices.Clear();
        }
        await runtime.SeatingCycleAsync(study, wanted, CancellationToken.None);
        if (withdrawn) { Assert.Empty(indices); Assert.Null(study.Image); Assert.Equal(1, study.Withdrawals); }
        else
        {
            Assert.Equal(change is "replace" or "corrupt" or "pause-resume" or "revision-only" ? new[] { 0, 1, 2 } : new[] { 2 }, indices);
            Assert.Equal(image, study.Image); Assert.Equal(1, study.Publications);
        }
    }

    [Theory]
    [InlineData("replace")] [InlineData("withdraw")] [InlineData("repeat")]
    public async Task Seating_change_mid_download_refreshes_the_manifest_at_most_once(string change)
    {
        using var directory = new TestDirectory();
        var server = new FakeServer();
        byte[] original = new byte[20000], replacement = Enumerable.Repeat((byte)12, 20000).ToArray();
        string version = new('a', 64);
        var manifests = new List<string>();
        var chunks = new List<(int Revision, int Index)>();
        async Task<HttpResponseMessage> Send(HttpRequestMessage request)
        {
            string path = request.RequestUri!.AbsolutePath;
            if (!path.Contains("/study-settings/seating", StringComparison.Ordinal)) return await server.Send(request);
            var body = JsonNode.Parse(await request.Content!.ReadAsStringAsync())!.AsObject();
            bool chunk = path.EndsWith("/chunk", StringComparison.Ordinal);
            int revision = chunk ? body["revision"]!.GetValue<int>() : manifests.Count + 1;
            var image = revision == 1 ? original : replacement;
            string hash = Convert.ToHexStringLower(SHA256.HashData(image));
            JsonObject data;
            bool changed = false;
            if (chunk)
            {
                int index = body["index"]!.GetValue<int>();
                chunks.Add((revision, index));
                changed = index == 1 && (revision == 1 || change == "repeat");
                data = new() { ["revision"] = revision, ["hash"] = hash, ["index"] = index, ["count"] = 2,
                    ["data"] = Convert.ToBase64String(image.AsSpan(index * 16384, Math.Min(16384, image.Length - index * 16384))) };
            }
            else
            {
                manifests.Add(body.Text("requestId"));
                data = new() { ["version"] = version, ["classId"] = "class-one", ["revision"] = revision,
                    ["asset"] = revision == 2 && change == "withdraw" ? null : new JsonObject
                        { ["hash"] = hash, ["width"] = 100, ["height"] = 100, ["byteCount"] = image.Length, ["chunks"] = 2 } };
            }
            return await SeatingReply(request, data, changed ? "SEATING_CHANGED" : null, changed ? 409 : 200);
        }
        using (var device = new NpepDevice(directory.Path, server.Api)) await server.ActivateAsync(device);
        await using var runtime = new NpepRuntime(() => new(directory.Path, origin => new(origin, new Handler(Send))), "seating-revision", Sample);
        await Until(() => runtime.Snapshot().Connection == "ONLINE");
        var study = new SeatingFixture();
        var wanted = new JsonObject { ["version"] = version };
        var error = await Record.ExceptionAsync(() => runtime.SeatingCycleAsync(study, wanted, default));
        Assert.Equal(2, manifests.Distinct().Count());
        Assert.Equal(2, manifests.Count);
        Assert.Equal(change == "withdraw" ? new[] { (1, 0), (1, 1) } : new[] { (1, 0), (1, 1), (2, 0), (2, 1) }, chunks);
        if (change == "repeat") Assert.Equal("SEATING_CHANGED", Assert.IsType<NpepException>(error).Code);
        else Assert.Null(error);
        Assert.Equal(change == "withdraw" ? 1 : 0, study.Withdrawals);
        Assert.Equal(change == "replace" ? 1 : 0, study.Publications);
        if (change == "replace") Assert.Equal(replacement, study.Image);
        else Assert.Null(study.Image);
    }

    [Theory]
    [InlineData("chunk", false)]
    [InlineData("chunk", true)]
    [InlineData("manifest", false)]
    [InlineData("manifest", true)]
    public async Task Slow_seating_exchange_allows_heartbeat_and_a_late_reply_cannot_survive_pause(string slowStep, bool denied)
    {
        using var directory = new TestDirectory();
        var server = new FakeServer();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var image = new byte[16000]; string hash = Convert.ToHexStringLower(SHA256.HashData(image)); string version = new('a', 64);
        NpepDevice? native = null;
        async Task<HttpResponseMessage> Send(HttpRequestMessage request)
        {
            string path = request.RequestUri!.AbsolutePath;
            if (!path.Contains("/study-settings/seating", StringComparison.Ordinal)) return await server.Send(request);
            bool chunk = path.EndsWith("/chunk", StringComparison.Ordinal);
            bool slow = chunk == (slowStep == "chunk");
            if (slow) { entered.TrySetResult(); await release.Task; }
            JsonObject data;
            if (chunk)
                data = new() { ["revision"] = 1, ["hash"] = hash, ["index"] = 0, ["count"] = 1, ["data"] = Convert.ToBase64String(image) };
            else data = new() { ["version"] = version, ["classId"] = "class-one", ["revision"] = 1, ["asset"] = new JsonObject
                { ["hash"] = hash, ["width"] = 100, ["height"] = 100, ["byteCount"] = image.Length, ["chunks"] = 1 } };
            bool oldRefusal = denied && slow;
            return await SeatingReply(request, data, oldRefusal ? "AUTH_INVALID" : null, oldRefusal ? 401 : 200);
        }
        using (var paired = new NpepDevice(directory.Path, server.Api)) await server.ActivateAsync(paired);
        await using var runtime = new NpepRuntime(() => native = new(directory.Path, origin => new(origin, new Handler(Send))), "seating-slow", Sample);
        await Until(() => runtime.Snapshot().Connection == "ONLINE", TimeSpan.FromSeconds(16));
        var study = new SeatingFixture();
        var downloading = runtime.SeatingCycleAsync(study, new() { ["version"] = version }, default);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(4));
            var receipt = await native!.ReportAsync(NpepStatusReader.Map(Sample(), "seating-slow"), 0).WaitAsync(TimeSpan.FromSeconds(2));
            Assert.Equal(2, receipt.Number("acceptedSequence"));
            Assert.True((await Command(runtime, "pause")).ReportingPaused);
            Assert.False((await Command(runtime, "resume")).ReportingPaused);
            release.TrySetResult();
            Assert.Equal("SESSION_SUPERSEDED", (await Assert.ThrowsAsync<NpepException>(() => downloading)).Code);
            Assert.Equal("ACTIVE", native.View().Text("state"));
            Assert.Null(study.Image);
        }
        finally
        {
            release.TrySetResult();
            if (!downloading.IsCompleted) try { await downloading; } catch (NpepException) { }
        }
    }

    private static async Task<HttpResponseMessage> SeatingReply(HttpRequestMessage request, JsonObject data, string? error = null,
        int status = 200, string version = "0.14")
    {
        var reply = Fixtures.Reply(request, data, error, status);
        using var content = reply.Content;
        var envelope = JsonNode.Parse(await content.ReadAsStringAsync())!.AsObject();
        envelope["protocolVersion"] = version;
        reply.Content = new StringContent(envelope.ToJsonString());
        return reply;
    }

    private sealed class SeatingFixture : INpepStudySettings
    {
        public byte[]? Image; public int Publications, Withdrawals;
        public JsonObject? Request;
        public volatile string? Error;
        public void BindStudy(string? scope) { }
        public void ApplyStudy(JsonObject configuration) { }
        public JsonObject? ObserveStudy() => null;
        public void StudyFailed(string code) { }
        public JsonObject? StudySeatingRequest() => Request?.Copy();
        public void SeatingFailed(string code) => Error = code;
        public void ApplySeating(JsonObject manifest, byte[]? bytes)
        { if (bytes is null) { Image = null; Interlocked.Increment(ref Withdrawals); } else { Image = bytes.ToArray(); Publications++; } }
    }
}

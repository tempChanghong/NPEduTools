using System.Security.Cryptography;
using System.Text.Json.Nodes;
using NPEduTools.Integrations.Npep;

namespace NPEduTools.Npep.Tests;

public sealed partial class RuntimeTests
{
    [Theory]
    [InlineData("network")] [InlineData("deadline")] [InlineData("replace")] [InlineData("withdraw")] [InlineData("corrupt")]
    public async Task Seating_retry_resumes_verified_chunks_but_never_mixes_a_replacement(string change)
    {
        using var directory = new TestDirectory(); var server = new FakeServer();
        byte[] image = Enumerable.Repeat((byte)42, 40000).ToArray();
        string hash = Convert.ToHexStringLower(SHA256.HashData(image));
        var indices = new List<int>(); bool fail = true, withdrawn = false, corrupt = false; int revision = 1, budgetCalls = 0;
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
            var reply = Fixtures.Reply(request, data);
            var envelope = JsonNode.Parse(await reply.Content.ReadAsStringAsync())!.AsObject(); envelope["protocolVersion"] = "0.14";
            reply.Content = new StringContent(envelope.ToJsonString()); return reply;
        }
        using (var device = new NpepDevice(directory.Path, server.Api)) await server.ActivateAsync(device);
        await using var runtime = new NpepRuntime(() => new(directory.Path, origin => new(origin, new Handler(Send))), "seating-recovery", Sample);
        await Until(() => runtime.Snapshot().Connection == "ONLINE");
        var study = new SeatingFixture(); var wanted = new JsonObject { ["version"] = version };
        var error = await Record.ExceptionAsync(() => runtime.SeatingCycleAsync(study, wanted, CancellationToken.None));
        Assert.IsType(change == "deadline" ? typeof(OperationCanceledException) : typeof(HttpRequestException), error);
        Assert.Null(study.Image); Assert.Equal(new[] { 0, 1, 2 }, indices);
        indices.Clear(); budgetCalls = 0;
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
            Assert.Equal(change is "replace" or "corrupt" ? new[] { 0, 1, 2 } : new[] { 2 }, indices);
            Assert.Equal(image, study.Image); Assert.Equal(1, study.Publications);
        }
    }
    private sealed class SeatingFixture : INpepStudySettings
    {
        public byte[]? Image; public int Publications, Withdrawals;
        public void BindStudy(string? scope) { }
        public void ApplyStudy(JsonObject configuration) { }
        public JsonObject? ObserveStudy() => null;
        public void StudyFailed(string code) { }
        public void ApplySeating(JsonObject manifest, byte[]? bytes)
        { if (bytes is null) { Withdrawals++; Image = null; } else { Image = bytes.ToArray(); Publications++; } }
    }
}

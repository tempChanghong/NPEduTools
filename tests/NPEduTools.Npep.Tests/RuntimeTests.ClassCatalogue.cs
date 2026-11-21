using System.Net;
using System.Text.Json.Nodes;
using NPEduTools.Contracts;
using NPEduTools.Integrations.Npep;

namespace NPEduTools.Npep.Tests;

public sealed partial class RuntimeTests
{
    [Fact]
    public void LookupFieldsStayOffLegacyCommandsAndAcceptFullUnicodeNames()
    {
        var inspect = new NpepCommand("inspect", 0, "https://npep.test");
        var oldWire = System.Text.Json.JsonSerializer.SerializeToNode(inspect, Protocol.Json)!.AsObject();
        Assert.False(oldWire.ContainsKey("schoolQuery") || oldWire.ContainsKey("classQuery") || oldWire.ContainsKey("catalogueAfter"));
        var fullName = string.Concat(Enumerable.Repeat("𠮷", 191));
        var find = inspect with {Action = "find-class", SchoolQuery = fullName, ClassQuery = "一班",
            ServerInstanceId = NpepProtocol.Id(), DeploymentEpoch = NpepProtocol.Id()};
        Assert.True(NpepContract.Valid(find));
        Assert.False(NpepContract.Valid(find with {SchoolQuery = fullName + "中"}));
        Assert.False(NpepContract.Valid(find with {ClassQuery = "班\u200b级"}));
        Assert.False(NpepContract.Valid(find with {ManagementSecret = "test-secret"}));
        Assert.False(NpepContract.Valid(inspect with {CatalogueAfter = "screen-1"}));
    }

    private static HttpResponseMessage CatalogueReply(HttpRequestMessage request, JsonObject data)
    {
        var envelope = new JsonObject { ["requestId"] = request.Headers.GetValues("X-Request-Id").Single(),
            ["protocolVersion"] = "0.16", ["serverTime"] = "2026-01-01T00:00:00.000Z", ["data"] = data };
        return new(HttpStatusCode.OK) { Content = new StringContent(envelope.ToJsonString()) };
    }

    private static JsonObject Preview(FakeServer server, int count)
    {
        string longName = new('教', 190);
        var rows = new JsonArray();
        for (int i = 0; i < count; i++)
            rows.Add(new JsonObject { ["schoolName"] = longName, ["gradeName"] = "高二", ["className"] = longName,
                ["screenName"] = longName, ["schoolId"] = "school", ["classId"] = "room",
                ["screenBindingId"] = "screen-" + i, ["preauthorized"] = false });
        return new() { ["serverInstanceId"] = server.Info.Text("serverInstanceId"), ["deploymentEpoch"] = server.Info.Text("deploymentEpoch"),
            ["items"] = rows, ["truncated"] = false };
    }

    [Fact]
    public async Task LargeLegacyPreviewStillFitsStatusFrameAndDoesNotAdvertiseLookup()
    {
        using var dir = new TestDirectory();
        var server = new FakeServer();
        var large = Preview(server, 200);
        await using var runtime = new NpepRuntime(() => new(dir.Path, origin => new NpepApi(origin, new Handler(request =>
            request.RequestUri!.AbsolutePath.EndsWith("/catalog") ? Task.FromResult(CatalogueReply(request, large.Copy())) : server.Send(request)))), "test", Sample);
        var state = await Command(runtime, "inspect");
        Assert.Equal("VERIFIED", state.Connection);
        Assert.Null(state.Catalogue!["supportsLookup"]);
        Assert.True((bool)state.Catalogue["truncated"]!);
        Assert.InRange(state.Catalogue["items"]!.AsArray().Count, 1, 199);
        using var frame = new MemoryStream();
        await Protocol.WriteAsync(frame, runtime.Handle(new(1, Guid.NewGuid(), "npep.status")), CancellationToken.None);
        frame.Position = 0;
        Assert.True(JsonNode.DeepEquals(state.Catalogue, (await Protocol.ReadAsync<HostResponse>(frame, CancellationToken.None)).Npep!.Catalogue));
    }

    [Fact]
    public async Task FailedLookupClearsOldRowsCanRetryAndRequiresInspectionWhenEpochChanges()
    {
        using var dir = new TestDirectory();
        var server = new FakeServer();
        int attempts = 0;
        await using var runtime = new NpepRuntime(() => new(dir.Path, origin => new NpepApi(origin, new Handler(async request =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/info")) return await server.Send(request);
            var reply = Preview(server, 1);
            reply["supportsLookup"] = true;
            if (request.RequestUri.AbsolutePath.EndsWith("/lookup"))
            {
                if (++attempts == 1) throw new HttpRequestException();
                reply["schoolQuery"] = "SCH";
                reply["classQuery"] = "ROOM";
                reply["after"] = null;
                reply["nextCursor"] = null;
                reply["items"]![0]!["schoolCode"] = attempts == 2 ? "WRONG" : "SCH";
                reply["items"]![0]!["classCode"] = "ROOM";
                if (attempts == 4) reply["deploymentEpoch"] = NpepProtocol.Id();
            }
            return CatalogueReply(request, reply);
        }))), "test", Sample);
        var initial = await Command(runtime, "inspect");
        var find = new NpepCommand("find-class", initial.Revision, initial.Origin, ServerInstanceId: server.Info.Text("serverInstanceId"),
            DeploymentEpoch: server.Info.Text("deploymentEpoch"), SchoolQuery: "SCH", ClassQuery: "ROOM");
        HostResponse Send(NpepCommand c) => runtime.Handle(new(1, Guid.NewGuid(), "npep.command", Npep: c with {Revision = runtime.Snapshot().Revision}));
        Assert.Equal("Rejected", Send(find with {Origin = "https://elsewhere.test"}).Outcome);
        foreach (string? error in new[] { "NETWORK_UNAVAILABLE", "INVALID_RESPONSE", null, "INSTANCE_MISMATCH" })
        {
            var accepted = Send(find);
            Assert.Equal("Accepted", accepted.Outcome);
            Assert.Empty(accepted.Npep!.Catalogue!["items"]!.AsArray());
            await Until(() => !runtime.Snapshot().Busy);
            var state = runtime.Snapshot();
            Assert.Equal(error, state.Error);
            if (error == "INSTANCE_MISMATCH") { Assert.Null(state.Catalogue); Assert.Null(state.Server); }
            else
            {
                Assert.Equal("VERIFIED", state.Connection);
                Assert.Equal(error is null ? 1 : 0, state.Catalogue!["items"]!.AsArray().Count);
            }
        }
        Assert.Equal("Rejected", Send(find).Outcome);
    }

    [Fact]
    public async Task ReassignedScreenCannotBeConfirmedAfterRestart()
    {
        using var dir = new TestDirectory();
        var server = new FakeServer();
        using (var device = new NpepDevice(dir.Path, server.Api))
        {
            await device.BeginClassAsync("https://npep.test", server.Info, "教室", "test", server.Approval.Text("screenBindingId"),
                null, CancellationToken.None, server.Approval.Text("schoolId"), "original-class");
            await device.PollApprovalAsync();
        }
        using var restarted = new NpepDevice(dir.Path, server.Api);
        Assert.Equal("BINDING_CHANGED", (await Assert.ThrowsAsync<NpepException>(() => restarted.ConfirmRequestedClassAsync(CancellationToken.None))).Code);
        Assert.Equal("BINDING_CHANGED", (await Assert.ThrowsAsync<NpepException>(() => restarted.ConfirmAsync(server.Approval.Text("approvalId")))).Code);
        Assert.Null(server.Confirm);
    }
}

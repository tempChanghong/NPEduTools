using System.Net;
using System.Text.Json.Nodes;
using NPEduTools.Contracts;
using NPEduTools.Integrations.Npep;

namespace NPEduTools.Npep.Tests;

public sealed class ReplacementAdmissionTests
{
    [Fact]
    public void ReplacementTargetRoundTripsInsideTheExistingIpcCatalogue()
    {
        var state = new NpepState(1, "CREATING", "STOPPED", "", Catalogue: new JsonObject { ["replacementTarget"] =
            new JsonObject { ["screenBindingId"] = "front", ["screenName"] = "一班前屏", ["deviceName"] = "新机" } });
        var wire = System.Text.Json.JsonSerializer.SerializeToNode(state, Protocol.Json)!.AsObject();
        Assert.False(wire.ContainsKey("requestedScreenBindingId") || wire.ContainsKey("requestedScreenName") || wire.ContainsKey("requestedDeviceName"));
        var restored = System.Text.Json.JsonSerializer.Deserialize<NpepState>(wire, Protocol.Json)!;
        Assert.Equal("front", restored.RequestedScreenBindingId);
        Assert.Equal("一班前屏", restored.RequestedScreenName);
        Assert.Equal("新机", restored.RequestedDeviceName);
    }
    private sealed class School
    {
        public readonly FakeServer Server = new();
        public readonly List<JsonObject> Replacements = [];
        public int ReplacementStatus = 201;
        public bool DropReply, Cancelled, LoseOrdinary;
        public string Binding => Server.Approval.Text("screenBindingId");
        public NpepCommand Command => new("replacement-class", 0, "https://npep.test", "新机", Server.Info.Text("serverInstanceId"),
            Server.Info.Text("deploymentEpoch"), ScreenBindingId: Binding, ManagementSecret: "class-pass");
        public NpepDevice Device(string path) => new(path, origin => new NpepApi(origin, new Handler(Send)));
        public async Task Occupied(NpepDevice device)
        {
            var e = await Assert.ThrowsAsync<NpepException>(() => device.BeginClassAsync("https://npep.test", Server.Info, "新机", "test",
                Binding, "class-pass", default, Server.Approval.Text("schoolId"), Server.Approval.Text("administrativeClassId"), "一班 · 前屏"));
            Assert.Equal("BINDING_OCCUPIED", e.Code);
        }
        private async Task<HttpResponseMessage> Send(HttpRequestMessage request)
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith("/pairings/password"))
            {
                if (LoseOrdinary) throw new HttpRequestException("response lost");
                return Fixtures.Reply(request, new(), "BINDING_OCCUPIED", 409);
            }
            if (path.EndsWith("/pairings/replacement"))
            {
                Assert.Equal("0.16", request.Headers.GetValues("X-NPEP-Version").Single());
                var body = JsonNode.Parse(await request.Content!.ReadAsStringAsync())!.AsObject();
                Replacements.Add(body);
                if (DropReply) { DropReply = false; throw new HttpRequestException("response lost"); }
                if (ReplacementStatus == 426) return new(HttpStatusCode.UpgradeRequired);
                var envelope = new JsonObject { ["protocolVersion"] = "0.16", ["requestId"] = body.Text("requestId"),
                    ["serverTime"] = "2026-11-21T00:00:00.000Z" };
                if (ReplacementStatus == 201)
                {
                    envelope["data"] = Fixtures.Data("createdPairingResponse");
                    Server.Create = body;
                }
                else envelope["error"] = new JsonObject { ["code"] = "MANAGEMENT_SECRET_INVALID", ["message"] = "invalid", ["retryAfterSeconds"] = null };
                return new((HttpStatusCode)ReplacementStatus) {Content = new StringContent(envelope.ToJsonString())};
            }
            if (Cancelled && path.EndsWith(Server.Approval.Text("pairingId")))
                return Fixtures.Reply(request, new() { ["pairingId"] = Server.Approval.Text("pairingId"), ["state"] = "CANCELLED" });
            return await Server.Send(request);
        }
    }

    [Fact]
    public async Task LostReplacementReplyResumesIdenticalRequestAndRequiresLocalConfirmation()
    {
        using var dir = new TestDirectory();
        var school = new School {DropReply = true};
        using (var initial = school.Device(dir.Path)) await school.Occupied(initial);
        using (var candidate = school.Device(dir.Path))
        {
            Assert.Equal("一班 · 前屏", candidate.View()["replacementTarget"]!.AsObject().Text("screenName"));
            foreach (var changed in new[] {school.Command with {ScreenBindingId = "other"}, school.Command with {DeploymentEpoch = NpepProtocol.Id()},
                         school.Command with {Origin = "https://other.test"}, school.Command with {DeviceName = "other"}})
                Assert.Equal("LOCAL_CONFIRMATION_REQUIRED", (await Assert.ThrowsAsync<NpepException>(() => candidate.RequestReplacementAsync(changed, default))).Code);
            Assert.Empty(school.Replacements);
            await Assert.ThrowsAsync<HttpRequestException>(() => candidate.RequestReplacementAsync(school.Command, default));
            Assert.DoesNotContain("class-pass", candidate.View().ToJsonString());
        }
        using var resumed = school.Device(dir.Path);
        await Assert.ThrowsAsync<NpepException>(() => resumed.RequestReplacementAsync(school.Command, default));
        await resumed.ResumeCreateAsync();
        Assert.Equal(2, school.Replacements.Count);
        Assert.True(JsonNode.DeepEquals(school.Replacements[0], school.Replacements[1]));
        await resumed.PollApprovalAsync();
        var vault = Path.Combine(dir.Path, "npep.credentials.dpapi");
        File.SetLastWriteTimeUtc(vault, DateTime.UtcNow.AddDays(-1));
        var lastWrite = File.GetLastWriteTimeUtc(vault);
        await resumed.PollApprovalAsync();
        Assert.Equal(lastWrite, File.GetLastWriteTimeUtc(vault));
        await resumed.ConfirmRequestedClassAsync(default);
        Assert.Equal("APPROVED", resumed.View().Text("state"));
        Assert.Null(school.Server.Confirm);
        await resumed.ConfirmAsync(school.Server.Approval.Text("approvalId"));
        Assert.Equal("ACTIVE", resumed.View().Text("state"));
    }

    [Fact]
    public async Task CorrectedPasswordAndRemoteRejectionSurviveRestart()
    {
        using var dir = new TestDirectory();
        var school = new School {ReplacementStatus = 403};
        using (var candidate = school.Device(dir.Path))
        {
            await school.Occupied(candidate);
            var wrong = school.Command with {ManagementSecret = "wrong-password"};
            Assert.Equal("MANAGEMENT_SECRET_INVALID", (await Assert.ThrowsAsync<NpepException>(() => candidate.RequestReplacementAsync(wrong, default))).Code);
        }
        using (var candidate = school.Device(dir.Path))
        {
            Assert.Equal("MANAGEMENT_SECRET_INVALID", candidate.View().Text("pairingError"));
            await Assert.ThrowsAsync<NpepException>(() => candidate.ResumeCreateAsync());
            Assert.Single(school.Replacements);
            school.ReplacementStatus = 201;
            await candidate.RequestReplacementAsync(school.Command, default);
            Assert.NotEqual(school.Replacements[0].Text("requestId"), school.Replacements[1].Text("requestId"));
            school.Cancelled = true;
            Assert.Equal("PAIRING_CANCELLED", (await Assert.ThrowsAsync<NpepException>(() => candidate.PollApprovalAsync())).Code);
        }
        using var stopped = school.Device(dir.Path);
        Assert.Equal("PAIRING_CANCELLED", stopped.View().Text("pairingError"));
        Assert.Null(stopped.View()["approval"]);
        Assert.False(school.Server.Active);
        Assert.Equal("CANCELLED", await stopped.UnpairAsync());
        Assert.DoesNotContain(school.Server.Requests, request => request.Path.EndsWith("/revoke"));
    }

    [Fact]
    public async Task UncertainOrdinaryRetryCannotBeConvertedAndOlderKvIsReported()
    {
        using var dir = new TestDirectory();
        var school = new School {ReplacementStatus = 426};
        using var candidate = school.Device(dir.Path);
        await school.Occupied(candidate);
        school.LoseOrdinary = true;
        await Assert.ThrowsAsync<HttpRequestException>(() => candidate.ResumeCreateAsync());
        await Assert.ThrowsAsync<NpepException>(() => candidate.RequestReplacementAsync(school.Command, default));
        Assert.Empty(school.Replacements);
        school.LoseOrdinary = false;
        await Assert.ThrowsAsync<NpepException>(() => candidate.ResumeCreateAsync());
        Assert.Equal("REPLACEMENT_UNSUPPORTED", (await Assert.ThrowsAsync<NpepException>(() => candidate.RequestReplacementAsync(school.Command, default))).Code);
        Assert.False(school.Server.Active);
    }
}

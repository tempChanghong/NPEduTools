using NPEduTools.Integrations.Npep;

namespace NPEduTools.Npep.Tests;

public sealed class ScreenPairingTests
{
    [Fact]
    public async Task WebCodeFixesAssignmentButStillRequiresLocalConfirmation()
    {
        using var dir = new TestDirectory(); var server = new FakeServer();
        using var device = new NpepDevice(dir.Path, server.Api);
        await device.ClaimScreenPairingAsync("https://npep.test", server.Info, "测试屏", "test", "ABCD2345");
        Assert.Equal("SCREEN", device.View().Text("pairingSource"));
        Assert.Equal("PENDING", device.View().Text("state"));
        Assert.Equal("ABCD2345", server.Create!.Text("userCode"));
        Assert.DoesNotContain(server.Requests, r => r.Path == "pairings");
        await device.PollApprovalAsync();
        Assert.Equal("APPROVED", device.View().Text("state"));
        Assert.False(server.Active);
        await Assert.ThrowsAsync<NpepException>(() => device.ConfirmAsync(NpepProtocol.Id()));
        await device.ConfirmAsync(server.Approval.Text("approvalId"));
        Assert.Equal("ACTIVE", device.View().Text("state"));
        Assert.DoesNotContain(server.Create!.Text("pairingSecret"), device.View().ToJsonString());
        Assert.DoesNotContain(server.Confirm!.Text("deviceSecret"), device.View().ToJsonString());
    }

    [Fact]
    public async Task LostClaimReplyRecoversSameCodeAndCandidateAfterRestart()
    {
        using var dir = new TestDirectory(); var server = new FakeServer { LoseCreateAfterCommit = true };
        using (var device = new NpepDevice(dir.Path, server.Api))
        {
            await Assert.ThrowsAsync<HttpRequestException>(() => device.ClaimScreenPairingAsync("https://npep.test", server.Info, "测试屏", "test", "ABCD2345"));
            Assert.Equal("CREATING", device.View().Text("state"));
        }
        var candidate = server.Create!.Copy();
        using var recovered = new NpepDevice(dir.Path, server.Api);
        await recovered.ResumeCreateAsync();
        Assert.True(NpepProtocol.Equal(candidate, server.Create));
        Assert.Equal("SCREEN", recovered.View().Text("pairingSource"));
        await recovered.PollApprovalAsync();
        await recovered.ConfirmAsync(server.Approval.Text("approvalId"));
        Assert.Equal(2, server.Requests.Count(r => r.Path == "pairings/claim"));
        Assert.DoesNotContain(server.Requests, r => r.Path == "pairings");
    }
}

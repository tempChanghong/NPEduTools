using NPEduTools.Integrations.Npep;

namespace NPEduTools.Npep.Tests;

public sealed class ClassAdmissionTests
{
    [Theory]
    [InlineData(null,"pairings/apply","CLASS_APPLY")]
    [InlineData("school-password","pairings/password","CLASS_PASSWORD")]
    public async Task RequestedClassIsFencedAndSecretNeverAppearsInRedactedView(string? secret,string path,string source)
    {
        using var directory=new TestDirectory();var server=new FakeServer();
        using var device=new NpepDevice(directory.Path,server.Api);
        string binding=server.Approval.Text("screenBindingId");
        await device.BeginClassAsync("https://npep.test",server.Info,"测试屏","test",binding,secret);
        Assert.Contains(server.Requests,x=>x.Path==path);Assert.Equal(source,device.View().Text("pairingSource"));
        if(secret is not null)Assert.DoesNotContain(secret,device.View().ToJsonString());
        await device.PollApprovalAsync();await device.ConfirmRequestedClassAsync(CancellationToken.None);
        Assert.Equal("ACTIVE",device.View().Text("state"));
    }
    [Fact]
    public async Task ApprovalForDifferentClassNeverAutoConfirms()
    {
        using var directory=new TestDirectory();var server=new FakeServer();
        using var device=new NpepDevice(directory.Path,server.Api);
        await device.BeginClassAsync("https://npep.test",server.Info,"测试屏","test","different-class",null);
        await device.PollApprovalAsync();
        Assert.Equal("BINDING_CHANGED",(await Assert.ThrowsAsync<NpepException>(()=>device.ConfirmRequestedClassAsync(CancellationToken.None))).Code);
        Assert.False(server.Active);
    }
    [Fact]
    public async Task LostPasswordPairingReplyRecoversOriginalRequestAndDoesNotLeakPassword()
    {
        using var directory=new TestDirectory();var server=new FakeServer{LoseCreateAfterCommit=true};
        using(var device=new NpepDevice(directory.Path,server.Api))
        {await Assert.ThrowsAsync<HttpRequestException>(()=>device.BeginClassAsync("https://npep.test",server.Info,"测试屏","test",server.Approval.Text("screenBindingId"),"school-password"));Assert.DoesNotContain("school-password",device.View().ToJsonString());}
        var original=server.Create!.Copy();using var resumed=new NpepDevice(directory.Path,server.Api);
        await resumed.ResumeCreateAsync();Assert.True(NpepProtocol.Equal(original,server.Create));
        await resumed.PollApprovalAsync();await resumed.ConfirmRequestedClassAsync(CancellationToken.None);Assert.Equal("ACTIVE",resumed.View().Text("state"));
    }
    [Fact]
    public async Task RemotePlainHttpCannotTransmitManagementPassword()
    {
        using var directory=new TestDirectory();var server=new FakeServer();using var device=new NpepDevice(directory.Path,server.Api);
        var e=await Assert.ThrowsAsync<NpepException>(()=>device.BeginClassAsync("http://npep.test",server.Info,"测试屏","test",server.Approval.Text("screenBindingId"),"school-password"));
        Assert.Equal("HTTPS_REQUIRED",e.Code);Assert.DoesNotContain(server.Requests,x=>x.Path=="pairings/password");
    }
}

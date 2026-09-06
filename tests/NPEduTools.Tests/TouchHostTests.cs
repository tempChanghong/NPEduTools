using System.Runtime.Versioning;
using NPEduTools.Contracts;

namespace NPEduTools.Tests;

[SupportedOSPlatform("windows")]
public sealed class TouchHostTests
{
    private static async Task<HostResponse> Request(string pipe, string action)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        return await HostClient.RequestAsync(pipe, new HostRequest(Protocol.Version, Guid.NewGuid(), "presentation.touch." + action), deadline.Token);
    }

    [Fact]
    public async Task ControlsAreIdempotentAndTwoHostsCannotRunTheSameAssist()
    {
        string first = "NPEduTools.Test.touch." + Guid.NewGuid().ToString("N"), second = first + ".second";
        await using var host1 = await TestProcess.StartAsync("NPEduTools.Host", false, "--pipe", first, "--classisland-pipe", first + ".absent");
        await using var host2 = await TestProcess.StartAsync("NPEduTools.Host", false, "--pipe", second, "--classisland-pipe", second + ".absent");
        Assert.False((await Request(first, "status")).TouchAssist!.Running);
        Assert.Equal("Rejected", (await Request(first, "resume")).Outcome);
        Assert.True((await Request(first, "compat.on")).TouchAssist!.AllowUnmarkedMouse);
        Assert.True((await Request(first, "enable")).TouchAssist!.Running);
        Assert.True((await Request(first, "enable")).TouchAssist!.Running);
        Assert.Equal("Failed", (await Request(second, "enable")).Outcome);
        Assert.True((await Request(first, "pause")).TouchAssist!.Paused);
        Assert.True((await Request(first, "pause")).TouchAssist!.Paused);
        Assert.False((await Request(first, "resume")).TouchAssist!.Paused);
        Assert.False((await Request(first, "disable")).TouchAssist!.Running);
        Assert.False((await Request(first, "disable")).TouchAssist!.Running);
        Assert.True((await Request(second, "enable")).TouchAssist!.Running);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        Assert.Equal("Succeeded", (await HostClient.RequestAsync(second, "host.stop", deadline.Token)).Outcome);
        await host2.WaitForExitAsync();
        Assert.True((await Request(first, "enable")).TouchAssist!.Running);
        Assert.False((await Request(first, "disable")).TouchAssist!.Running);
    }
}

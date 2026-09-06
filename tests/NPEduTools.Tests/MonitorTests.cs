using System.Diagnostics;
using System.Runtime.Versioning;
using NPEduTools.Contracts;

namespace NPEduTools.Tests;

[SupportedOSPlatform("windows")]
public sealed class MonitorTests
{
    private static string Pipe() => "NPEduTools.Test." + Guid.NewGuid().ToString("N");
    private static async Task<WatchSnapshot> UntilAsync(IAsyncEnumerator<WatchSnapshot> stream, Func<WatchSnapshot, bool> match)
    {
        while (await stream.MoveNextAsync())
            if (match(stream.Current)) return stream.Current;
        throw new InvalidOperationException("Subscription ended before expected state.");
    }

    [Fact]
    public async Task SubscriptionPersistsWithoutClientsAndResynchronizesAfterTargetRestart()
    {
        string host = Pipe(), ci = Pipe();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(55));
        await using var server = await TestProcess.StartAsync("NPEduTools.Host", false, "--pipe", host, "--classisland-pipe", ci);
        WatchSnapshot before;
        await using (var peer = await TestProcess.StartAsync("NPEduTools.ClassIsland.TestPeer", true, ci, "healthy"))
        {
            await using (var first = HostClient.WatchAsync(host, deadline.Token).GetAsyncEnumerator())
                before = await UntilAsync(first, s => s.Outcome == "Succeeded");
            // Exceed the old one-shot worker's 20-second lifespan, with no App connected.
            await Task.Delay(TimeSpan.FromSeconds(22), deadline.Token);
            await using var reopened = HostClient.WatchAsync(host, deadline.Token).GetAsyncEnumerator();
            var after = await UntilAsync(reopened, s => s.Outcome == "Succeeded");
            Assert.Equal(before.StreamId, after.StreamId);
            Assert.Equal(before.ConnectionId, after.ConnectionId);
            Assert.True(after.Sequence > before.Sequence);
            Assert.True(after.Status!.ObservedEvents["classisland.lessonsService.onClass"] >
                before.Status!.ObservedEvents["classisland.lessonsService.onClass"]);
        }
        await using var observer = HostClient.WatchAsync(host, deadline.Token).GetAsyncEnumerator();
        var offline = await UntilAsync(observer, s => s.Outcome != "Succeeded");
        Assert.Null(offline.Status);
        await using var replacement = await TestProcess.StartAsync("NPEduTools.ClassIsland.TestPeer", true, ci, "empty");
        var restored = await UntilAsync(observer, s => s.Outcome == "Succeeded");
        Assert.Equal(before.StreamId, restored.StreamId);
        Assert.NotEqual(before.ConnectionId, restored.ConnectionId);
        Assert.Equal("None", restored.Status!.State);
        Assert.Null(restored.Status.Subject);
        Assert.False(restored.Status.IsClassPlanLoaded);
    }

    [Fact]
    public async Task SubscribersShareAConnectionAndCannotConsumeAllRequestSlots()
    {
        string host = Pipe(), ci = Pipe();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await using var peer = await TestProcess.StartAsync("NPEduTools.ClassIsland.TestPeer", true, ci, "healthy");
        await using var server = await TestProcess.StartAsync("NPEduTools.Host", false, "--pipe", host, "--classisland-pipe", ci);
        await using var first = HostClient.WatchAsync(host, deadline.Token).GetAsyncEnumerator();
        var a = await UntilAsync(first, s => s.Outcome == "Succeeded");
        await using var second = HostClient.WatchAsync(host, deadline.Token).GetAsyncEnumerator();
        var b = await UntilAsync(second, s => s.Outcome == "Succeeded");
        Assert.Equal(a.ConnectionId, b.ConnectionId);
        Assert.Equal(a.StreamId, b.StreamId);
        await using var third = HostClient.WatchAsync(host, deadline.Token).GetAsyncEnumerator();
        var rejected = await UntilAsync(third, _ => true);
        Assert.Equal("SubscriptionLimit", rejected.ErrorCode);
        Assert.Equal("Succeeded", (await HostClient.RequestAsync(host, "host.ping", deadline.Token)).Outcome);
        Assert.Equal("Succeeded", (await HostClient.RequestAsync(host, "host.stop", deadline.Token)).Outcome);
        Assert.Null((await UntilAsync(first, s => s.Outcome == "Stopped")).Status);
        Assert.Null((await UntilAsync(second, s => s.Outcome == "Stopped")).Status);
        Assert.False(await first.MoveNextAsync());
        Assert.False(await second.MoveNextAsync());
        await server.WaitForExitAsync();
    }

    [Theory]
    [InlineData("hang")]
    [InlineData("drop")]
    [InlineData("error")]
    public async Task MonitorClearsFailedStateAndRecoversWithoutNewClientRequest(string mode)
    {
        string host = Pipe(), ci = Pipe();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var server = await TestProcess.StartAsync("NPEduTools.Host", false, "--pipe", host, "--classisland-pipe", ci);
        await using var watch = HostClient.WatchAsync(host, deadline.Token).GetAsyncEnumerator();
        await using (var peer = await TestProcess.StartAsync("NPEduTools.ClassIsland.TestPeer", true, ci, mode))
        {
            var failed = await UntilAsync(watch, s => s.Outcome is not ("Connecting" or "Succeeded"));
            Assert.Null(failed.Status);
            Assert.NotNull(failed.ErrorCode);
            Assert.Equal("Succeeded", (await HostClient.RequestAsync(host, "host.ping", deadline.Token)).Outcome);
        }
        await using var replacement = await TestProcess.StartAsync("NPEduTools.ClassIsland.TestPeer", true, ci, "healthy");
        var restored = await UntilAsync(watch, s => s.Outcome == "Succeeded");
        Assert.Equal("数学", restored.Status!.Subject);
    }

    [Fact]
    public async Task MonitorWorkerExitsWhenParentLeaseExpires()
    {
        var info = TestProcess.StartInfo("NPEduTools.Host", false, "--monitor-worker", "--classisland-pipe", Pipe());
        info.RedirectStandardInput = true;
        using var worker = Process.Start(info)!;
        var error = worker.StandardError.ReadToEndAsync();
        try
        {
            // Keep stdin open, but never renew the lease. Even a stuck Connect must exit.
            await worker.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(12));
            Assert.Equal(124, worker.ExitCode);
            await error;
        }
        finally { if (!worker.HasExited) { worker.Kill(true); await worker.WaitForExitAsync(); } }
    }
}

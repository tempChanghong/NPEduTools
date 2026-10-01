using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.Versioning;
using System.Text.Json;
using NPEduTools.Contracts;

namespace NPEduTools.Tests;

[SupportedOSPlatform("windows")]
[Collection(ProcessIntegrationCollection.Name)]
public sealed class IntegrationTests
{
    private static string UniquePipe() => "NPEduTools.Test." + Guid.NewGuid().ToString("N");

    private static async Task<HostResponse> QueryAsync(string host, string capability = "classisland.status", int timeout = 5000, int observe = 0)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var pipe = new NamedPipeClientStream(".", host, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        await pipe.ConnectAsync(deadline.Token);
        var request = new HostRequest(Protocol.Version, Guid.NewGuid(), capability, timeout, observe);
        await Protocol.WriteAsync(pipe, request, deadline.Token);
        var response = await Protocol.ReadAsync<HostResponse>(pipe, deadline.Token);
        Assert.Equal(request.RequestId, response.RequestId);
        return response;
    }

    [Fact]
    public async Task QueriesRealIpcContractAndReceivesCourseEvents()
    {
        string host = UniquePipe(), ci = UniquePipe();
        await using var peer = await TestProcess.StartAsync("NPEduTools.ClassIsland.TestPeer", true, ci, "healthy");
        await using var server = await TestProcess.StartAsync("NPEduTools.Host", false, "--pipe", host, "--classisland-pipe", ci);
        var response = await QueryAsync(host, observe: 600);
        Assert.Equal("Succeeded", response.Outcome);
        Assert.NotNull(response.Status);
        Assert.Equal("数学", response.Status.Subject);
        Assert.Equal("OnClass", response.Status.State);
        Assert.True(response.Status.IsClassPlanLoaded);
        Assert.Equal(2, response.Status.CurrentSelectedIndex);
        Assert.True(response.Status.ObservedEvents["classisland.lessonsService.onClass"] > 0);
    }

    [Fact]
    public async Task EmptyLessonIsAValidSuccessRatherThanDisconnection()
    {
        string host = UniquePipe(), ci = UniquePipe();
        await using var peer = await TestProcess.StartAsync("NPEduTools.ClassIsland.TestPeer", true, ci, "empty");
        await using var server = await TestProcess.StartAsync("NPEduTools.Host", false, "--pipe", host, "--classisland-pipe", ci);
        var response = await QueryAsync(host);
        Assert.Equal("Succeeded", response.Outcome);
        Assert.NotNull(response.Status);
        Assert.Null(response.Status.Subject);
        Assert.False(response.Status.IsClassPlanLoaded);
    }

    [Fact]
    public async Task MissingServerTimesOutThenRecoversWhenServerAppears()
    {
        string host = UniquePipe(), ci = UniquePipe();
        await using var server = await TestProcess.StartAsync("NPEduTools.Host", false, "--pipe", host, "--classisland-pipe", ci);
        var watch = Stopwatch.StartNew();
        var missing = await QueryAsync(host, timeout: 600);
        Assert.Equal("TimedOut", missing.Outcome);
        Assert.Null(missing.Status);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(5));
        Assert.Equal("Succeeded", (await QueryAsync(host, "host.ping")).Outcome);
        await using var peer = await TestProcess.StartAsync("NPEduTools.ClassIsland.TestPeer", true, ci, "healthy");
        Assert.Equal("Succeeded", (await QueryAsync(host)).Outcome);
    }

    [Theory]
    [InlineData("hang", "TimedOut")]
    [InlineData("drop", "Unavailable")]
    [InlineData("error", "Failed")]
    public async Task RemoteFailuresNeverReturnDefaultLessonSuccess(string mode, string expected)
    {
        string host = UniquePipe(), ci = UniquePipe();
        await using var peer = await TestProcess.StartAsync("NPEduTools.ClassIsland.TestPeer", true, ci, mode);
        await using var server = await TestProcess.StartAsync("NPEduTools.Host", false, "--pipe", host, "--classisland-pipe", ci);
        var response = await QueryAsync(host);
        Assert.Equal(expected, response.Outcome);
        Assert.Null(response.Status);
        Assert.Equal("Succeeded", (await QueryAsync(host, "host.ping")).Outcome);
    }

    [Fact]
    public async Task ClientCanDisconnectAndReconnectWithoutRestartingHost()
    {
        string host = UniquePipe(), ci = UniquePipe();
        await using var peer = await TestProcess.StartAsync("NPEduTools.ClassIsland.TestPeer", true, ci, "healthy");
        await using var server = await TestProcess.StartAsync("NPEduTools.Host", false, "--pipe", host, "--classisland-pipe", ci);
        Assert.Equal("Succeeded", (await QueryAsync(host)).Outcome);
        // Each call creates a new connection; no stale proxy or snapshot is reused.
        Assert.Equal("Succeeded", (await QueryAsync(host)).Outcome);
    }

    [Fact]
    public async Task TargetRestartDoesNotLeaveAStaleConnection()
    {
        string host = UniquePipe(), ci = UniquePipe();
        await using var server = await TestProcess.StartAsync("NPEduTools.Host", false, "--pipe", host, "--classisland-pipe", ci);
        await using (var first = await TestProcess.StartAsync("NPEduTools.ClassIsland.TestPeer", true, ci, "healthy"))
            Assert.Equal("OnClass", (await QueryAsync(host)).Status?.State);
        Assert.Equal("TimedOut", (await QueryAsync(host, timeout: 500)).Outcome);
        await using (var second = await TestProcess.StartAsync("NPEduTools.ClassIsland.TestPeer", true, ci, "empty"))
        {
            var response = await QueryAsync(host);
            Assert.Equal("Succeeded", response.Outcome);
            Assert.Equal("None", response.Status?.State);
        }
    }

    [Fact]
    public async Task AbandonedRequestDoesNotBreakHost()
    {
        string host = UniquePipe(), ci = UniquePipe();
        await using var peer = await TestProcess.StartAsync("NPEduTools.ClassIsland.TestPeer", true, ci, "healthy");
        await using var server = await TestProcess.StartAsync("NPEduTools.Host", false, "--pipe", host, "--classisland-pipe", ci);
        await using (var abandoned = new NamedPipeClientStream(".", host, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly))
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await abandoned.ConnectAsync(deadline.Token);
            await Protocol.WriteAsync(abandoned, new HostRequest(Protocol.Version, Guid.NewGuid(), "classisland.status", 5000, 500), deadline.Token);
            // Close without receiving the response. The Host must survive either read/write race.
        }
        Assert.Equal("Succeeded", (await QueryAsync(host, "host.ping")).Outcome);
        using var recovery = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        HostResponse response;
        do
        {
            await Task.Delay(100, recovery.Token);
            response = await QueryAsync(host);
        } while (response.ErrorCode == "ResourceBusy" && !recovery.IsCancellationRequested);
        Assert.Equal("Succeeded", response.Outcome);
    }

    [Fact]
    public async Task CliRunsAcrossProcessBoundary()
    {
        string host = UniquePipe(), ci = UniquePipe();
        await using var peer = await TestProcess.StartAsync("NPEduTools.ClassIsland.TestPeer", true, ci, "healthy");
        await using var server = await TestProcess.StartAsync("NPEduTools.Host", false, "--pipe", host, "--classisland-pipe", ci);
        using var cli = Process.Start(TestProcess.StartInfo("NPEduTools.Cli", false, "status", "--pipe", host, "--timeout-ms", "5000"))!;
        var output = cli.StandardOutput.ReadToEndAsync();
        var error = cli.StandardError.ReadToEndAsync();
        await cli.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(cli.ExitCode == 0, await error);
        var response = JsonSerializer.Deserialize<HostResponse>(await output, Protocol.Json);
        Assert.Equal("数学", response?.Status?.Subject);
    }

    [Fact]
    public async Task MalformedClientDoesNotBreakNextConnection()
    {
        string host = UniquePipe();
        await using var server = await TestProcess.StartAsync("NPEduTools.Host", false, "--pipe", host, "--classisland-pipe", UniquePipe());
        await using (var pipe = new NamedPipeClientStream(".", host, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly))
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await pipe.ConnectAsync(deadline.Token);
            await pipe.WriteAsync(new byte[] { 255, 255, 255, 127 }, deadline.Token);
            // Wait for the server to reject the malformed frame before making the next request.
            Assert.Equal(0, await pipe.ReadAsync(new byte[1], deadline.Token));
        }
        Assert.Equal("Succeeded", (await QueryAsync(host, "host.ping")).Outcome);
    }
}

using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.Versioning;
using NPEduTools.Contracts;

namespace NPEduTools.Tests;

[SupportedOSPlatform("windows")]
[Collection(ProcessIntegrationCollection.Name)]
public sealed class HostReadinessTests
{
    private static string UniquePipe() => "NPEduTools.Test." + Guid.NewGuid().ToString("N");

    [Fact]
    public async Task FailedPipeBindingDoesNotAnnounceReadiness()
    {
        string host = UniquePipe();
        using var reserved = new NamedPipeServerStream(host, PipeDirection.InOut, 1,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        using var process = Process.Start(TestProcess.StartInfo("NPEduTools.Host", false,
            "--pipe", host, "--classisland-pipe", UniquePipe()))!;
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        try
        {
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(1, process.ExitCode);
            Assert.Contains("Host transport failed: IOException", await error);
            Assert.DoesNotContain("Host ready:", await output);
        }
        finally
        {
            if (!process.HasExited) process.Kill(true);
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    [Fact]
    public async Task ReadyHostImmediatelyAcceptsPing()
    {
        string host = UniquePipe();
        await using var server = await TestProcess.StartAsync("NPEduTools.Host", false,
            "--pipe", host, "--classisland-pipe", UniquePipe());
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await using var pipe = new NamedPipeClientStream(".", host, PipeDirection.InOut,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        await pipe.ConnectAsync(1500, deadline.Token);
        var request = new HostRequest(Protocol.Version, Guid.NewGuid(), "host.ping", 1000, 0);
        await Protocol.WriteAsync(pipe, request, deadline.Token);
        var response = await Protocol.ReadAsync<HostResponse>(pipe, deadline.Token);
        Assert.Equal(request.RequestId, response.RequestId);
        Assert.Equal("Succeeded", response.Outcome);
    }
}

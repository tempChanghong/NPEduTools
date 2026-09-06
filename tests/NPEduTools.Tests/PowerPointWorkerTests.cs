using System.Diagnostics;
using System.Runtime.Versioning;
using NPEduTools.PowerPoint.Diagnostics;

namespace NPEduTools.Tests;

[SupportedOSPlatform("windows")]
public sealed class PowerPointWorkerTests
{
    [Fact]
    public async Task SilentChildTimesOutAndSessionCanStop()
    {
        var start = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),
            "WindowsPowerShell", "v1.0", "powershell.exe"))
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardInput = true };
        start.ArgumentList.Add("-NoProfile");
        start.ArgumentList.Add("-NonInteractive");
        start.ArgumentList.Add("-Command");
        start.ArgumentList.Add("Start-Sleep -Seconds 30");
        var session = new ProbeSession(() => start, TimeSpan.FromMilliseconds(300));
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var timedOut = new TaskCompletionSource<ShowSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        var running = session.RunAsync(snapshot => timedOut.TrySetResult(snapshot), stop.Token);
        try
        {
            var snapshot = await timedOut.Task.WaitAsync(stop.Token);
            Assert.Equal("ProbeTimeout", snapshot.Status);
            Assert.Empty(session.Latest.Snapshot.Windows);
        }
        finally { stop.Cancel(); await running.WaitAsync(TimeSpan.FromSeconds(3)); }
    }

    [Fact]
    public async Task WorkerExitsWhenParentInputCloses()
    {
        string dotnet = Environment.GetEnvironmentVariable("NPEEDUTOOLS_DOTNET_HOST") ?? "dotnet";
        var start = new ProcessStartInfo(dotnet)
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true };
        start.ArgumentList.Add(typeof(TapTracker).Assembly.Location);
        start.ArgumentList.Add("--com-worker");
        using var worker = Process.Start(start)!;
        try
        {
            var output = worker.StandardOutput.ReadToEndAsync();
            worker.StandardInput.Close();
            await worker.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(4));
            await output;
            Assert.Equal(0, worker.ExitCode);
        }
        finally { if (!worker.HasExited) worker.Kill(); }
    }
}

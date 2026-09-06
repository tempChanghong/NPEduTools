using NPEduTools.Core;
using NPEduTools.Host;

namespace NPEduTools.Tests;

public sealed class WorkerTests
{
    [Fact]
    public async Task CancellationCleansUpAndReleasesResourceGate()
    {
        string missing = "NPEduTools.Test." + Guid.NewGuid().ToString("N");
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var reader = new IsolatedStatusReader(_ =>
        {
            started.TrySetResult();
            return TestProcess.StartInfo("NPEduTools.Host", false, "--ipc-worker", "--classisland-pipe", missing);
        });
        using var cancellation = new CancellationTokenSource();
        var pending = reader.ReadAsync(new(TimeSpan.FromSeconds(5), TimeSpan.Zero), cancellation.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var concurrent = await reader.ReadAsync(new(TimeSpan.FromSeconds(1), TimeSpan.Zero), default);
        Assert.Equal("ResourceBusy", concurrent.ErrorCode);
        cancellation.Cancel();
        Assert.Equal("Cancelled", (await pending.WaitAsync(TimeSpan.FromSeconds(5))).Outcome);
        var retry = await reader.ReadAsync(new(TimeSpan.FromMilliseconds(300), TimeSpan.Zero), default);
        Assert.Equal("TimedOut", retry.Outcome);
    }
}

using NPEduTools.Contracts;
using NPEduTools.Integrations.Npep;

namespace NPEduTools.Npep.Tests;

public sealed partial class RuntimeTests
{
    [Fact]
    public async Task Slow_status_sample_is_discarded_and_reporter_recovers_without_repairing()
    {
        using var directory = new TestDirectory();
        var server = new FakeServer();
        int samples = 0;
        HostResponse ReadSample()
        {
            // Reproduce the real monotonic sample-age rejection, without sensors or
            // changing the five-second wire freshness limit.
            if (Interlocked.Increment(ref samples) == 1) Thread.Sleep(5300);
            return Sample();
        }
        await using var runtime = new NpepRuntime(() => new(directory.Path, server.Api), "sample-recovery", ReadSample);
        await Command(runtime, "inspect"); await Command(runtime, "pair", server);
        await Command(runtime, "poll"); await Command(runtime, "confirm", server);
        await Until(() => runtime.Snapshot().Error == "SAMPLE_TOO_OLD", TimeSpan.FromSeconds(12));
        Assert.Equal("ACTIVE", runtime.Snapshot().State);
        Assert.Equal("OFFLINE", runtime.Snapshot().Connection);
        Assert.DoesNotContain(server.Requests, request => request.Path == "device/status");
        await Until(() => runtime.Snapshot().Connection == "ONLINE", TimeSpan.FromSeconds(15));
        Assert.True(Volatile.Read(ref samples) >= 2);
        Assert.Null(runtime.Snapshot().Error);
        Assert.Single(server.Requests, request => request.Path == "device/sessions");
        Assert.Single(server.Requests, request => request.Path == "device/status");
    }
}

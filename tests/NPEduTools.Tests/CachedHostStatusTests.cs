using System.Runtime.Versioning;
using NPEduTools.Contracts;
using NPEduTools.Core;
using NPEduTools.Host;

namespace NPEduTools.Tests;

[SupportedOSPlatform("windows")]
public sealed class CachedHostStatusTests
{
    private sealed class ForbiddenReader : ILessonStatusReader
    {
        public int Calls;
        public Task<StatusResult> ReadAsync(StatusQuery query, CancellationToken cancellationToken)
        { Calls++; throw new InvalidOperationException("Cached status must not start a lesson probe."); }
    }

    [Fact]
    public async Task CacheRequestCannotStartClockProbeOrLessonReader()
    {
        int clockStarts = 0;
        await using var clock = new SchoolClockMonitor(() => { clockStarts++; throw new InvalidOperationException("Must not start"); }, _ => { });
        var reader = new ForbiddenReader();
        string name = "NPEduTools.Test.Cached." + Guid.NewGuid().ToString("N");
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var server = new PipeServer(name, reader, _ => { }, schoolClock: clock);
        Task running = server.RunAsync(cancellation.Token);
        try
        {
            var result = await HostClient.RequestAsync(name, "host.cached-status", cancellation.Token);
            Assert.Equal("Succeeded", result.Outcome);
            Assert.Equal("Unavailable", result.SchoolClock?.State);
            Assert.Null(result.ClassroomMode);
            Assert.Equal(0, reader.Calls);
        }
        finally
        {
            await cancellation.CancelAsync();
            try { await running; } catch (OperationCanceledException) { }
        }
        Assert.Equal(0, clockStarts);
    }

    [Fact]
    public void CachedRequestRejectsObservationWindowAndMutationParameters()
    {
        Assert.Null(Protocol.Validate(new(1, Guid.NewGuid(), "host.cached-status")));
        Assert.NotNull(Protocol.Validate(new(1, Guid.NewGuid(), "host.cached-status", ObserveMs: 100)));
        Assert.NotNull(Protocol.Validate(new(1, Guid.NewGuid(), "host.cached-status", AutoStartEnabled: true)));
    }
}

using NPEduTools.Contracts;
using NPEduTools.Core;
using NPEduTools.Host;

namespace NPEduTools.Tests;

public sealed partial class Dly2202Tests
{
    [Theory]
    [InlineData("list", false)] [InlineData("read", false)] [InlineData("read", true)]
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public async Task Host_meter_deadline_returns_a_correlated_timeout_and_allows_explicit_retry(string action, bool opening)
    {
        if (!OperatingSystem.IsWindows()) return;
        bool slow = true; var connections = new List<Connection>();
        var backend = new Backend
        {
            OnEnumerate = () => { if (slow && !opening) Thread.Sleep(1000); },
            Create = device =>
            {
                if (slow && opening) Thread.Sleep(1000);
                var connection = new Connection(device); connections.Add(connection); return connection;
            }
        };
        var meter = new SoundMeterService(() => backend, _ => null);
        string pipe = "NPEduTools.Test.meter-deadline." + Guid.NewGuid().ToString("N");
        using var lifetime = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var running = new PipeServer(pipe, new MeterForbiddenReader(), _ => { }, soundMeter: meter).RunAsync(lifetime.Token);
        try
        {
            var request = Meter(action) with { TimeoutMs = 250, ObserveMs = 0 };
            Assert.Null(Protocol.Validate(request));
            var response = await HostClient.RequestAsync(pipe, request, lifetime.Token);
            Assert.Equal(request.RequestId, response.RequestId);
            Assert.Equal("Rejected", response.Outcome); Assert.Equal("METER_TIMEDOUT", response.ErrorCode);
            Assert.Null(response.SoundMeter?.Reading);
            Assert.Equal(opening ? 1 : 0, backend.Opens);
            Assert.All(connections, c => { Assert.True(c.Disposed); Assert.Empty(c.Writes); });
            Assert.Equal("Succeeded", (await HostClient.RequestAsync(pipe, "host.ping", lifetime.Token)).Outcome);
            slow = false;
            var retried = await HostClient.RequestAsync(pipe, Meter(action), lifetime.Token);
            Assert.Equal("Succeeded", retried.Outcome);
            if (action == "read") Assert.Equal(60.5, retried.SoundMeter!.Reading!.Decibels);
            else { Assert.Single(retried.SoundMeter!.Devices!); Assert.Equal(0, backend.Opens); }
            Assert.All(connections, c => Assert.True(c.Disposed));
        }
        finally { await lifetime.CancelAsync(); try { await running; } catch (OperationCanceledException) { } }
    }

    [Fact]
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public async Task Host_shutdown_during_meter_enumeration_is_not_relabelled_as_request_timeout()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var lifetime = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var client = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var backend = new Backend { OnEnumerate = () => lifetime.Cancel() };
        var meter = new SoundMeterService(() => backend, _ => null);
        string pipe = "NPEduTools.Test.meter-shutdown." + Guid.NewGuid().ToString("N");
        var running = new PipeServer(pipe, new MeterForbiddenReader(), _ => { }, soundMeter: meter).RunAsync(lifetime.Token);
        try
        {
            await Assert.ThrowsAnyAsync<IOException>(() => HostClient.RequestAsync(pipe, Meter("list"), client.Token));
            Assert.Equal(0, backend.Opens);
            backend.OnEnumerate = null;
            Assert.Equal("Succeeded", (await meter.HandleAsync(Meter("list"), default)).Outcome);
        }
        finally { await lifetime.CancelAsync(); try { await running; } catch (OperationCanceledException) { } }
    }

    private sealed class MeterForbiddenReader : ILessonStatusReader
    {
        public Task<StatusResult> ReadAsync(StatusQuery query, CancellationToken token)
            => throw new InvalidOperationException("Meter diagnostics cannot request lessons or recording");
    }
}

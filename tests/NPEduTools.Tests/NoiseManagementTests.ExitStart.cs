using NPEduTools.Contracts;

namespace NPEduTools.Tests;

public sealed partial class NoiseManagementTests
{
    private static HostResponse ManualStart(Fixture f, bool remote)
    {
        var state = f.Noise.Snapshot();
        return remote ? f.Noise.RemoteCommand(Guid.NewGuid(), "START", state.InstanceId, state.Revision, null, 600)
            : f.Noise.Handle(f.Command("start", "synthetic"));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task PreparedOrFinalShutdownRejectsNewManualCaptureWithoutOpeningMicrophone(bool remote, bool final)
    {
        await using var f = new Fixture();
        var stop = new HostRequest(1, Guid.NewGuid(), "host.stop");
        Assert.Equal("Succeeded", f.Noise.ManagementHandle(stop with { Capability = "noise.management.prepare-exit" }).Outcome);
        if (final) { Assert.Null(f.Noise.AuthorizeInterruption(stop)); f.Time.Ms += 120000; }
        var before = f.Noise.Snapshot(); var result = ManualStart(f, remote);
        Assert.Equal("Rejected", result.Outcome); Assert.Equal("NoiseBusy", result.ErrorCode);
        Assert.Equal(before.Revision, f.Noise.Snapshot().Revision);
        Assert.Null(f.Noise.Snapshot().SessionId); Assert.Equal(0, f.Creates);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExpiredPreparationAllowsExplicitManualStartWithoutAddingScheduleProtection(bool remote)
    {
        await using var f = new Fixture();
        Assert.Equal("Succeeded", f.Noise.ManagementHandle(new(1, Guid.NewGuid(), "noise.management.prepare-exit")).Outcome);
        f.Time.Ms = 59999; Assert.Equal("NoiseBusy", ManualStart(f, remote).ErrorCode);
        Assert.Equal(0, f.Creates);
        f.Time.Ms = 60000; Assert.Equal("Accepted", ManualStart(f, remote).Outcome);
        await Until(() => f.Noise.Snapshot().State == "Active");
        Assert.Equal(1, f.Creates); Assert.False(f.Noise.Protection().Protected);
        Assert.Equal("Accepted", f.Noise.Handle(f.Command("stop")).Outcome);
    }

    [Fact]
    public async Task AbortedShutdownAllowsManualStartAfterReservationExpiry()
    {
        await using var f = new Fixture();
        var stop = new HostRequest(1, Guid.NewGuid(), "host.stop");
        f.Noise.ManagementHandle(stop with { Capability = "noise.management.prepare-exit" });
        Assert.Null(f.Noise.AuthorizeInterruption(stop)); f.Time.Ms = 60000;
        Assert.Equal("NoiseBusy", ManualStart(f, false).ErrorCode);
        f.Noise.AbortShutdown(); Assert.Equal("Accepted", ManualStart(f, false).Outcome);
        await Until(() => f.Noise.Snapshot().State == "Active"); Assert.False(f.Noise.Protection().Protected);
    }
}

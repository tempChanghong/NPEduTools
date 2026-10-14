using NPEduTools.Contracts;

namespace NPEduTools.Tests;

public sealed partial class NoiseCalibrationTests
{
    [Fact]
    public async Task Compact_quick_status_is_read_only_and_distinguishes_context_and_input_changes()
    {
        await using var f = new Fixture();
        Assert.Equal("Uncalibrated", f.Noise.CalibrationStatusSnapshot(Scope, Study).State);
        Assert.Equal(0, f.Creates);
        f.Command("begin"); await f.SampleAsync("Acceptable", -50); await f.SampleAsync("Discipline", -25);
        Assert.Equal("Succeeded", f.Command("confirm", medium: -40, high: -30).Outcome);
        var saved = f.Noise.CalibrationStatusSnapshot(Scope, Study);
        Assert.Equal(("Saved", "Quick"), (saved.State, saved.Method)); Assert.Equal(2, f.Creates);
        Assert.Equal("SCHOOL_CONFIGURATION_CHANGED", f.Noise.CalibrationStatusSnapshot(Scope, Study with { Version = new string('b', 64) }).Reason);
        Assert.Equal("SCHOOL_CONFIGURATION_UNCONFIRMED", f.Noise.CalibrationStatusSnapshot(null, null).Reason);
        var normal = f.Noise.Snapshot();
        f.Noise.Handle(new(1, Guid.NewGuid(), "noise.command", Noise: new("start", normal.InstanceId, normal.Revision, "mic")));
        await Until(() => f.Capture is { Started: true, Disposed: false });
        f.Capture!.Emit(-45);
        Assert.Equal("Ready", f.Noise.CalibrationStatusSnapshot(Scope, Study).State);
        f.Time.Milliseconds += 1500;
        Assert.Equal("Saved", f.Noise.CalibrationStatusSnapshot(Scope, Study).State);
        f.Capture.Signature += "-changed"; f.Capture.Emit(-45);
        var changed = f.Noise.CalibrationStatusSnapshot(Scope, Study);
        Assert.Equal(("Invalidated", "INPUT_SETTINGS_CHANGED"), (changed.State, changed.Reason));
    }
}

using NPEduTools.Contracts;

namespace NPEduTools.Tests;

public sealed partial class NoiseCalibrationTests
{
    [Fact]
    public async Task Same_microphone_reconnect_keeps_quick_profile_and_rechecks_its_signature()
    {
        await using var f = new Fixture(); f.Command("begin");
        await f.SampleAsync("Acceptable", -55); await f.SampleAsync("Discipline", -20);
        f.Command("confirm", medium: -40, high: -30);
        var profile = f.State.Profile!;
        string path = Path.Combine(f.DirectoryPath, "noise-quick-calibration.json"), original = File.ReadAllText(path);
        f.Noise.StartScheduled(Guid.NewGuid()); await Until(() => f.Noise.Snapshot().State == "Active");
        f.Capture!.EndpointDisconnected = true; f.Capture.Unplug(); await Until(() => !f.Noise.CaptureBusy);
        Assert.Equal(profile, f.State.Profile); Assert.Equal(original, File.ReadAllText(path));
        var before = f.Noise.Snapshot(); f.Noise.Handle(new(1, Guid.NewGuid(), "noise.command", Noise: new("select", before.InstanceId, before.Revision, "mic")));
        f.Noise.StartScheduled(Guid.NewGuid()); await Until(() => f.Noise.Snapshot().State == "Active");
        f.Time.Milliseconds += 100; f.Capture!.Emit(-50);
        Assert.Equal(-40, f.Noise.ResolveCalibration(Scope, Study.Version, Policy).MediumLineDbfs);
        Assert.Equal(profile, f.State.Profile); Assert.Equal(original, File.ReadAllText(path));
        f.Capture.Signature += "|changed-input"; f.Time.Milliseconds += 100; f.Capture.Emit(-50);
        Assert.Equal(Policy.MediumLineDbfs, f.Noise.ResolveCalibration(Scope, Study.Version, Policy).MediumLineDbfs);
    }
}

using NPEduTools.Contracts;
using NPEduTools.Host;

namespace NPEduTools.Tests;

public sealed partial class NoiseReferenceCalibrationTests
{
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task Confirmed_endpoint_disconnect_preserves_reference_until_the_same_input_is_verified(bool reload)
    {
        await using var f = new Fixture(); await f.PrepareAsync(); f.Command("confirm");
        var profile = f.State.Profile; string path = Path.Combine(f.DirectoryPath, "noise-reference-calibration.json");
        string original = File.ReadAllText(path);
        await StartFormal(f); FormalFrames(f, 60);
        f.Capture!.EndpointDisconnected = true; f.Capture.Disconnect();
        await Until(() => !f.Noise.CaptureBusy);
        Assert.Null(f.State.InvalidationReason); Assert.Equal(profile, f.State.Profile);
        Assert.Equal(original, File.ReadAllText(path));
        var pending = f.Noise.CalibrationStatusSnapshot(null, null);
        Assert.Equal("DEVICE_RECONNECT_REQUIRED", pending.Reason);
        if (reload)
        {
            Capture? capture = null; // Re-instantiate the saved-state path; this is not a physical Host restart.
            await using var next = new NoiseService(_ => capture = new Capture(), () => [new("mic", "Synthetic")], f.Time, f.DirectoryPath);
            Assert.Equal("DEVICE_RECONNECT_REQUIRED", next.CalibrationStatusSnapshot(null, null).Reason);
            Assert.Equal("Accepted", next.StartScheduled(Guid.NewGuid()).Outcome);
            await Until(() => next.Snapshot().State == "Active");
            for (int i = 0; i < 60; i++) { f.Time.Advance(.1); capture!.EmitNormal(); capture.Emit(-30, true); }
            Assert.Equal(60, next.Snapshot().Estimate!.CurrentDbA!.Value, 6);
            next.StopScheduled(next.Snapshot().SessionId!.Value); await Until(() => !next.CaptureBusy);
        }
        else
        {
            var before = f.Noise.Snapshot();
            Assert.Equal("Succeeded", f.Noise.Handle(new(1, Guid.NewGuid(), "noise.command",
                Noise: new("select", before.InstanceId, before.Revision, "mic"))).Outcome);
            await StartFormal(f); FormalFrames(f, 60);
            Assert.Equal(60, f.Noise.Snapshot().Estimate!.CurrentDbA!.Value, 6);
            await StopFormal(f);
        }
        Assert.False(File.Exists(Path.Combine(f.DirectoryPath, "noise-reference-use.pending")));
    }

    [Fact]
    public async Task Failed_disconnect_marker_write_keeps_conservative_reload_evidence()
    {
        await using var f = new Fixture(); await f.PrepareAsync(); f.Command("confirm"); await StartFormal(f);
        string marker = Path.Combine(f.DirectoryPath, "noise-reference-use.pending");
        using (var held = new FileStream(marker, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            f.Capture!.EndpointDisconnected = true; f.Capture.Disconnect(); await Until(() => !f.Noise.CaptureBusy);
            Assert.Equal("REFERENCE_STORE_UNAVAILABLE", f.State.Error);
        }
        await using var reload = new NoiseService(_ => new Capture(), () => [], f.Time, f.DirectoryPath);
        Assert.Equal("INPUT_SETTINGS_UNAVAILABLE", reload.ReferenceHandle(new(1, Guid.NewGuid(), "noise.reference.status")).NoiseReference!.InvalidationReason);
    }

    [Fact]
    public async Task Reconnected_endpoint_with_different_gain_still_permanently_invalidates_the_reference()
    {
        await using var f = new Fixture(); await f.PrepareAsync(); f.Command("confirm");
        await StartFormal(f); f.Capture!.EndpointDisconnected = true; f.Capture.Disconnect();
        await Until(() => !f.Noise.CaptureBusy);
        f.NormalSignature = Signature.Replace("gain=.7", "gain=.8");
        await StartFormal(f); FormalFrames(f, 60);
        Assert.Equal("INPUT_SETTINGS_CHANGED", f.State.InvalidationReason);
        Assert.Null(f.Noise.Snapshot().Estimate!.CurrentDbA);
        await StopFormal(f);
        f.NormalSignature = Signature; await StartFormal(f); FormalFrames(f, 60);
        Assert.Null(f.Noise.Snapshot().Estimate!.CurrentDbA);
        await StopFormal(f);
    }

    [Fact]
    public async Task A_real_input_change_before_disconnection_cannot_be_relabelled_as_recoverable()
    {
        await using var f = new Fixture(); await f.PrepareAsync(); f.Command("confirm"); await StartFormal(f);
        f.Capture!.Signature = Signature.Replace("gain=.7", "gain=.8"); FormalFrames(f, 1);
        Assert.Equal("INPUT_SETTINGS_CHANGED", f.State.InvalidationReason);
        f.Capture.EndpointDisconnected = true; f.Capture.Disconnect(); await Until(() => !f.Noise.CaptureBusy);
        await StartFormal(f); FormalFrames(f, 60); Assert.Null(f.Noise.Snapshot().Estimate!.CurrentDbA); await StopFormal(f);
    }
}

public sealed class NoiseCaptureCleanupTests
{
    [Theory]
    [InlineData(unchecked((int)0x88890004))] [InlineData(unchecked((int)0x8007048F))]
    public void Endpoint_removal_during_stop_still_releases_and_flushes_every_resource(int code)
    {
        var steps = new List<string>();
        NoiseCaptureCleanup.Release(() => { steps.Add("stop"); throw new System.Runtime.InteropServices.COMException("Synthetic disconnect", code); },
            () => steps.Add("capture"), () => steps.Add("flush"), () => steps.Add("endpoint"));
        Assert.Equal(["stop", "capture", "flush", "endpoint"], steps);
    }
    [Fact]
    public void Unexpected_cleanup_error_is_preserved_after_all_release_attempts()
    {
        var steps = new List<string>(); var expected = new IOException("Synthetic cleanup failure");
        Assert.Same(expected, Assert.Throws<IOException>(() => NoiseCaptureCleanup.Release(() => throw expected,
            () => steps.Add("capture"), () => steps.Add("flush"), () => steps.Add("endpoint"))));
        Assert.Equal(["capture", "flush", "endpoint"], steps);
    }
}

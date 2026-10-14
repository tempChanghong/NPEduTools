using System.Runtime.Versioning;
using System.Text.Json;
using NPEduTools.Contracts;
using NPEduTools.Host;

namespace NPEduTools.Tests;

public sealed partial class NoiseReferenceCalibrationTests
{
    [Fact]
    public async Task Compact_reference_status_tracks_formal_use_invalidation_and_clear_without_sampling()
    {
        await using var f = new Fixture(); await f.PrepareAsync(); Assert.Equal("Succeeded", f.Command("confirm").Outcome);
        int captures = f.ReferenceCreates;
        Assert.Equal(("Saved", "ManualReference"), (f.Noise.CalibrationStatusSnapshot(null, null).State, f.Noise.CalibrationStatusSnapshot(null, null).Method));
        Assert.Equal(captures, f.ReferenceCreates); Assert.Equal(0, f.NormalCreates);
        await StartFormal(f); FormalFrames(f, 2); f.Noise.Snapshot();
        Assert.Equal("Ready", f.Noise.CalibrationStatusSnapshot(null, null).State);
        f.Capture!.Signature += "-changed"; f.Noise.Snapshot();
        var invalid = f.Noise.CalibrationStatusSnapshot(null, null);
        Assert.Equal(("Invalidated", "INPUT_SETTINGS_CHANGED"), (invalid.State, invalid.Reason));
        await StopFormal(f); Assert.Equal("Invalidated", f.Noise.CalibrationStatusSnapshot(null, null).State);
        Assert.Equal("Succeeded", f.Command("clear").Outcome);
        Assert.Equal("Uncalibrated", f.Noise.CalibrationStatusSnapshot(null, null).State);
    }
    [Fact]
    public async Task Compact_status_does_not_call_corrupt_reference_uncalibrated()
    {
        await using var f = new Fixture();
        File.WriteAllText(Path.Combine(f.DirectoryPath, "noise-reference-calibration.json"), "corrupt fixture");
        Assert.Equal(("Unavailable", "REFERENCE_STORE_UNAVAILABLE"),
            (f.Noise.CalibrationStatusSnapshot(null, null).State, f.Noise.CalibrationStatusSnapshot(null, null).Reason));
        Assert.Equal(0, f.ReferenceCreates); Assert.Equal(0, f.NormalCreates);
    }
    [Fact, SupportedOSPlatform("windows")]
    public async Task Compact_status_is_opt_in_and_contains_no_device_signatures_samples_or_saved_audio()
    {
        await using var f = new Fixture(); await f.PrepareAsync(); f.Command("confirm");
        string name = "NPEduTools.Test.CalibrationStatus." + Guid.NewGuid().ToString("N");
        using var lifetime = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        var server = new PipeServer(name, new ForbiddenReader(), _ => { }, noise: f.Noise); var running = server.RunAsync(lifetime.Token);
        try
        {
            var old = await HostClient.RequestAsync(name, "noise.status", lifetime.Token); Assert.Null(old.NoiseCalibrationStatus);
            var current = await HostClient.RequestAsync(name, "noise.status.calibration", lifetime.Token);
            Assert.Equal("Succeeded", current.Outcome); Assert.Equal("Saved", current.NoiseCalibrationStatus!.State);
            Assert.Equal(current.Noise!.InstanceId, current.NoiseCalibrationStatus.InstanceId);
            Assert.Equal(current.Noise.Revision, current.NoiseCalibrationStatus.NoiseRevision);
            string json = JsonSerializer.Serialize(current.NoiseCalibrationStatus, Protocol.Json);
            Assert.True(json.Length < 500); Assert.DoesNotContain("signature", json, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("samples", json, StringComparison.OrdinalIgnoreCase);
            Assert.Null(current.NoiseCalibration); Assert.Null(current.NoiseReference);
            Assert.Equal(2, f.ReferenceCreates); Assert.Equal(0, f.NormalCreates);
        }
        finally { await lifetime.CancelAsync(); try { await running; } catch (OperationCanceledException) { } }
    }
}

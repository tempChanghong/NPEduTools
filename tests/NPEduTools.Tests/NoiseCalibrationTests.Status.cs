using NPEduTools.Contracts;
using NPEduTools.Core;
using NPEduTools.Host;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace NPEduTools.Tests;

public sealed partial class NoiseCalibrationTests
{
    [Fact, System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public async Task Cached_calibration_context_keeps_valid_quick_binding_and_rejects_expired_policy_without_ticking()
    {
        await using var f = new Fixture();
        var configuration = JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures/study-device-cases.json")))![0]!["value"]!.DeepClone().AsObject();
        string scope = configuration["identity"]!.ToJsonString(), version = configuration["version"]!.GetValue<string>();
        string context = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(scope + "\n" + version)));
        var profile = new NoiseCalibrationProfile(1, "mic", new Capture().Signature, context, -50, -40,
            DateTimeOffset.UtcNow, [Sample("Acceptable", -60), Sample("Discipline", -30)]);
        File.WriteAllText(Path.Combine(f.DirectoryPath, "noise-quick-calibration.json"), JsonSerializer.Serialize(new { version = 1, profile }, Protocol.Json));
        int modeReads = 0; bool brokenMode = false;
        await using var study = new StudySettingsService(() => new(Guid.NewGuid(), Guid.NewGuid(), 1, 0,
            new(2026, 10, 12, 18, 0, 0, TimeSpan.Zero), 0, "Advancing", "synthetic"), () => false, f.Time, false,
            mode: () => { modeReads++; return brokenMode ? throw new InvalidOperationException("Synthetic mode unavailable") : "Daily"; });
        study.BindStudy(scope); study.ApplyStudy(configuration); int expectedReads = modeReads; brokenMode = true;
        var noise = f.Noise.Snapshot(); f.Noise.Handle(new(1, Guid.NewGuid(), "noise.command", Noise: new("start", noise.InstanceId, noise.Revision, "mic")));
        await Until(() => f.Capture is { Started: true, Disposed: false }); f.Capture!.Emit(-45);
        string pipe = "NPEduTools.Test.QuickCachedContext." + Guid.NewGuid().ToString("N");
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        var server = new PipeServer(pipe, new PassiveReader(), _ => { }, noise: f.Noise, study: study); var running = server.RunAsync(stop.Token);
        try
        {
            var fresh = await HostClient.RequestAsync(pipe, "noise.status.calibration", stop.Token);
            Assert.Equal("Ready", fresh.NoiseCalibrationStatus!.State); Assert.Equal("Quick", fresh.NoiseCalibrationStatus.Method);
            f.Time.Milliseconds += 15000; f.Capture.Emit(-45);
            var expired = await HostClient.RequestAsync(pipe, "noise.status.calibration", stop.Token);
            Assert.Equal(("Saved", "SCHOOL_CONFIGURATION_UNCONFIRMED"), (expired.NoiseCalibrationStatus!.State, expired.NoiseCalibrationStatus.Reason));
            Assert.Equal("Active", expired.Noise!.State); Assert.Equal(expectedReads, modeReads); Assert.Equal(1, f.Creates);
        }
        finally { await stop.CancelAsync(); try { await running; } catch (OperationCanceledException) { } }
    }
    private sealed class PassiveReader : ILessonStatusReader
    { public Task<StatusResult> ReadAsync(StatusQuery query, CancellationToken token) => throw new InvalidOperationException("No ClassIsland query"); }
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

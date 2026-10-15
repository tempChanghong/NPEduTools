using System.Text.Json.Nodes;
using System.Buffers.Binary;
using NPEduTools.Core;
using NPEduTools.Contracts;
using NPEduTools.Host;
using NPEduTools.Integrations.Npep;

namespace NPEduTools.Tests;

public sealed partial class NoiseReferenceCalibrationTests
{
    [Fact]
    public async Task Background_manual_statistics_use_estimated_units_and_stop_classifying_an_invalid_reference_without_a_display()
    {
        await using var f = new Fixture(); await f.PrepareAsync(); Assert.Equal("Succeeded", f.Command("confirm").Outcome);
        var policy = new StudyNoise(true, -40, -50, 600, 3, new(65, 50));
        var settings = new StudySettings(new("Asia/Shanghai", "2026-10-01", null, [], []), new("StatusTrend", 600),
            new(false, "OwnClass", [], 60), policy, new(false, 3, 30, 30, true, false));
        var school = new StudyHostState("version-a", "APPLIED", "", "", "Outside", "OUTSIDE_WINDOW", true,
            null, null, null, settings);
        await using var manual = new ManualStudyService(f.Noise, () => ("synthetic-class", true, school), () => false,
            Path.Combine(f.DirectoryPath, "manual"), f.Time, false);
        var status = manual.Status();
        Assert.Equal("Succeeded", manual.Handle(new(1, Guid.NewGuid(), "manual.study.command",
            ManualStudy: new("start", status.InstanceId, status.Revision, DeviceId: "mic"))).Outcome);
        await Until(() => f.Noise.Snapshot().State == "Active"); manual.Tick();
        for (int i = 0; i < 40; i++) { FormalFrames(f, 1, aDigital: -24); manual.Tick(); }
        var before = f.Noise.StudyReportSnapshot()!;
        Assert.Equal(0, before.Raw.ThresholdSampledSeconds);
        Assert.Equal(4, before.Estimated!.HighSeconds, 6);
        Assert.Equal(4, before.Estimated.ThresholdSampledSeconds, 6);
        f.Capture!.Signature = Signature.Replace("gain=.7", "gain=.8");
        for (int i = 0; i < 10; i++) { FormalFrames(f, 1, aDigital: -24); manual.Tick(); }
        var invalidated = f.Noise.StudyReportSnapshot()!;
        Assert.Equal(before.Estimated.HighSeconds, invalidated.Estimated!.HighSeconds, 6);
        Assert.Equal(0, invalidated.Raw.ThresholdSampledSeconds);
        Assert.Equal(1, invalidated.Raw.SampledSeconds - before.Raw.SampledSeconds, 6);
        Assert.Equal("INPUT_SETTINGS_CHANGED", invalidated.EstimateReason);
        Assert.Equal("Active", f.Noise.Snapshot().State);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task Estimated_threshold_qualification_survives_unrelated_session_observers(bool manual)
    {
        await using var f = new Fixture(); await f.PrepareAsync(); Assert.Equal("Succeeded", f.Command("confirm").Outcome);
        if (manual)
        {
            Assert.Equal("Accepted", f.Noise.StartManualStudy(Guid.NewGuid(), "mic", 10800).Outcome);
            await Until(() => f.Noise.Snapshot().State == "Active");
        }
        else await StartFormal(f);
        var id = f.Noise.Snapshot().SessionId!.Value;
        var policy = new StudyNoise(true, -40, -50, 600, 3, new(65, 50));
        f.Noise.StudyThresholds(id, policy);
        for (int i = 0; i < 40; i++)
        {
            FormalFrames(f, 1, aDigital: -24);
            Assert.Equal(("None", false), f.Noise.StudyThresholds(null, null));
            Assert.Equal(("None", false), f.Noise.StudyThresholds(Guid.NewGuid(), policy with { Estimated = null }));
        }
        Assert.Equal(("High", true), f.Noise.StudyThresholds(id, policy));
        var report = f.Noise.StudyReportSnapshot()!;
        Assert.Equal(0, report.Raw.ThresholdSampledSeconds);
        Assert.Equal(report.Estimated!.SampledSeconds, report.Estimated.HighSeconds, 8);
    }

    private static async Task StartFormal(Fixture f, bool scheduled = true)
    {
        var s = f.Noise.Snapshot();
        var r = scheduled ? f.Noise.StartScheduled(Guid.NewGuid()) : f.Noise.Handle(new(1, Guid.NewGuid(), "noise.command",
            Noise: new("start", s.InstanceId, s.Revision, "mic")));
        Assert.Equal("Accepted", r.Outcome);
        await Until(() => f.Capture is { Started: true, Disposed: false } && f.Noise.Snapshot().State == "Active");
    }
    private static async Task StopFormal(Fixture f)
    {
        var s = f.Noise.Snapshot(); f.Noise.StopScheduled(s.SessionId!.Value);
        await Until(() => !f.Noise.CaptureBusy);
    }
    private static void FormalFrames(Fixture f, int count, double aDigital = -30, bool settled = true, double clipping = 0)
    {
        for (int i = 0; i < count; i++)
        { f.Time.Advance(.1); f.Capture!.EmitNormal(); f.Capture.Emit(aDigital, settled, clipping); }
    }

    [Fact]
    public async Task Default_acoustic_50_65_thresholds_use_only_valid_estimated_levels()
    {
        await using var f = new Fixture(); await f.PrepareAsync(); Assert.Equal("Succeeded", f.Command("confirm").Outcome);
        await StartFormal(f);
        var session = f.Noise.Snapshot().SessionId;
        var policy = new StudyNoise(true, -90, -89, 600, 3, new(65, 50));
        f.Noise.StudyThresholds(session, policy);
        FormalFrames(f, 40, aDigital: -41); // 49 estimated dB(A); raw signal alone would cross the raw fallback.
        Assert.Equal(("None", false), f.Noise.StudyThresholds(session, policy));
        FormalFrames(f, 40, aDigital: -35);
        Assert.Equal(("Medium", false), f.Noise.StudyThresholds(session, policy));
        FormalFrames(f, 40, aDigital: -24);
        Assert.Equal(("High", true), f.Noise.StudyThresholds(session, policy));
        f.Capture!.Signature = Signature.Replace("gain=.7", "gain=.8"); FormalFrames(f, 1, aDigital: -24);
        Assert.Equal(("None", false), f.Noise.StudyThresholds(session, policy));
        Assert.Null(f.Noise.Snapshot().Estimate!.CurrentDbA);
        await StopFormal(f);
    }
    [Fact]
    public async Task FormalMonitoringKeepsRawDigitalAndEstimatedWeightedChannelsAndReportsSeparate()
    {
        await using var f = new Fixture(); await f.PrepareAsync(); Assert.Equal("Succeeded", f.Command("confirm").Outcome);
        var transport = new NoiseTransport(f.Noise, f.DirectoryPath); transport.Bind("synthetic-school-class");
        await StartFormal(f);
        var session = f.Noise.Snapshot().SessionId;
        var policy = new StudyNoise(true, -15, -25, 600, 5, new(55, 45));
        Assert.Equal(("None", false), f.Noise.StudyThresholds(session, policy));
        FormalFrames(f, 60);
        var state = f.Noise.Snapshot();
        Assert.Equal(-30, state.CurrentDbfs!.Value, 6); Assert.Equal(60, state.Estimate!.CurrentDbA!.Value, 6);
        Assert.Equal(("High", true), f.Noise.StudyThresholds(session, policy));
        Assert.Equal("Slow", state.Estimate.TimeWeighting); Assert.Equal("pcm-energy-v1", state.Algorithm);
        Assert.Equal(64, state.Estimate.ProfileId.Length);
        var rawPolicy = policy with { Estimated = null };
        f.Noise.StudyThresholds(session, rawPolicy); FormalFrames(f, 60);
        Assert.Equal(("None", false), f.Noise.StudyThresholds(session, rawPolicy));
        NpepNoiseProtocol.Validate("status", transport.Observe());
        await StopFormal(f);
        var report = transport.Reports().Single()!.AsObject(); NpepNoiseProtocol.Validate("report", report);
        Assert.Equal(-30, report["summary"]!["energyMeanDbfs"]!.GetValue<double>(), 6);
        Assert.Equal(60, report["estimate"]!["summary"]!["energyMeanDbA"]!.GetValue<double>(), 6);
        Assert.Null(report["estimate"]!["currentDbA"]);
        Assert.DoesNotContain("Signature", report.ToJsonString(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("pcmBytes", report.ToJsonString());
    }

    [Fact]
    public async Task WarmupClippingOutOfRangeAndInvalidFramesNeverTriggerAcousticThresholds()
    {
        await using var f = new Fixture(); await f.PrepareAsync(); f.Command("confirm"); await StartFormal(f);
        var s = f.Noise.Snapshot(); var p = new StudyNoise(true, -90, -90, 600, 5, new(55, 45));
        f.Noise.StudyThresholds(s.SessionId, p);
        FormalFrames(f, 60, settled: false); Assert.Null(f.Noise.Snapshot().Estimate!.CurrentDbA);
        Assert.Equal(("None", false), f.Noise.StudyThresholds(s.SessionId, p));
        FormalFrames(f, 60, clipping: .002); Assert.Equal(0, f.Noise.Snapshot().Estimate!.Summary.SampledSeconds);
        FormalFrames(f, 60, aDigital: -10 + 100); // Outside instrument range, no extrapolated result.
        Assert.Null(f.Noise.Snapshot().Estimate!.CurrentDbA);
        Assert.Equal(("None", false), f.Noise.StudyThresholds(s.SessionId, p));
        FormalFrames(f, 60); Assert.Equal(("High", true), f.Noise.StudyThresholds(s.SessionId, p));
        FormalFrames(f, 1, aDigital: double.NaN); Assert.Null(f.Noise.Snapshot().Estimate!.CurrentDbA);
        Assert.Equal(("None", false), f.Noise.StudyThresholds(s.SessionId, p));
        Assert.InRange(f.Noise.Snapshot().Estimate!.Summary.Coverage, .24, .26);
        Assert.NotNull(f.Noise.Snapshot().CurrentDbfs);
        f.Time.Advance(1); Assert.Null(f.Noise.Snapshot().Estimate!.CurrentDbA);
        await StopFormal(f);
    }

    [Theory]
    [InlineData("gain=.7", "gain=.8")]
    [InlineData("mute=False", "mute=True")]
    [InlineData("48000", "44100")]
    [InlineData("|1|32|", "|2|32|")]
    public async Task InputChangeInvalidatesPersistentlyEvenAfterRestorationAndHostRestart(string before, string after)
    {
        await using var f = new Fixture(); await f.PrepareAsync(); f.Command("confirm"); await StartFormal(f);
        var session = f.Noise.Snapshot().SessionId; var policy = new StudyNoise(true, -90, -89, 600, 5, new(55, 45));
        f.Noise.StudyThresholds(session, policy); FormalFrames(f, 60);
        double savedSeconds = f.Noise.Snapshot().Estimate!.Summary.SampledSeconds;
        f.Capture!.Signature = Signature.Replace(before, after); FormalFrames(f, 1);
        Assert.Equal("Invalidated", f.Noise.Snapshot().Estimate!.State);
        Assert.Null(f.Noise.Snapshot().Estimate!.CurrentDbA); Assert.NotNull(f.Noise.Snapshot().CurrentDbfs);
        Assert.Equal(("None", false), f.Noise.StudyThresholds(session, policy));
        f.Capture.Signature = Signature; FormalFrames(f, 60);
        Assert.Equal(savedSeconds, f.Noise.Snapshot().Estimate!.Summary.SampledSeconds);
        await StopFormal(f);
        await using var restarted = new NoiseService(_ => new Capture(), () => [new("mic", "fake")], f.Time, f.DirectoryPath);
        var s = restarted.Snapshot(); restarted.Handle(new(1, Guid.NewGuid(), "noise.command", Noise: new("start", s.InstanceId, s.Revision, "mic")));
        await Until(() => restarted.Snapshot().State == "Active");
        Assert.Equal("INPUT_SETTINGS_CHANGED", restarted.Snapshot().Estimate!.Reason);
        var refState = restarted.ReferenceHandle(new(1, Guid.NewGuid(), "noise.reference.status")).NoiseReference!;
        Assert.Null(refState.Error); Assert.Equal("INPUT_SETTINGS_CHANGED", refState.InvalidationReason);
    }

    [Fact]
    public async Task SelectingAnotherEndpointWithdrawsReferenceEvenIfOriginalIsReselected()
    {
        await using var f = new Fixture(); await f.PrepareAsync(); f.Command("confirm");
        foreach (var device in new[] { "different-microphone", "mic" })
        {
            var s = f.Noise.Snapshot(); Assert.Equal("Succeeded", f.Noise.Handle(new(1, Guid.NewGuid(), "noise.command",
                Noise: new("select", s.InstanceId, s.Revision, device))).Outcome);
        }
        Assert.Equal("DEVICE_CHANGED", f.State.InvalidationReason);
        await StartFormal(f); FormalFrames(f, 60);
        Assert.Null(f.Noise.Snapshot().Estimate!.CurrentDbA); Assert.Equal("DEVICE_CHANGED", f.Noise.Snapshot().Estimate!.Reason);
        await StopFormal(f);
        await f.PrepareAsync(); Assert.Equal("Succeeded", f.Command("confirm").Outcome);
        Assert.Null(f.State.InvalidationReason);
        await StartFormal(f); FormalFrames(f, 60); Assert.Equal(60, f.Noise.Snapshot().Estimate!.CurrentDbA!.Value, 6);
        await StopFormal(f);
    }

    [Fact]
    public async Task UncalibratedCaptureNeverReinterpretsRawSignalAsAcousticUnits()
    {
        await using var f = new Fixture(); await StartFormal(f);
        var s = f.Noise.Snapshot(); var p = new StudyNoise(true, -90, -89, 600, 5, new(55, 45));
        f.Noise.StudyThresholds(s.SessionId, p); FormalFrames(f, 60);
        Assert.Null(f.Noise.Snapshot().Estimate); Assert.Equal(("None", false), f.Noise.StudyThresholds(s.SessionId, p));
        await StopFormal(f);
    }

    [Fact]
    public async Task UnsupportedNewFormatStillInvalidatesReferenceBeforeAttemptingWeightedProcessing()
    {
        await using var f = new Fixture(); await f.PrepareAsync(); f.Command("confirm");
        f.NormalSignature=Signature.Replace("48000","32000");f.NormalReferenceUnavailable=true;
        await StartFormal(f); FormalFrames(f,60);
        Assert.Equal(0,f.Capture!.ReferenceEnableCalls);
        Assert.Equal("INPUT_SETTINGS_CHANGED",f.Noise.Snapshot().Estimate!.Reason);
        Assert.NotNull(f.Noise.Snapshot().CurrentDbfs);
        await StopFormal(f);
        f.NormalSignature=Signature;f.NormalReferenceUnavailable=false;
        await StartFormal(f);FormalFrames(f,60);Assert.Null(f.Noise.Snapshot().Estimate!.CurrentDbA);
        await StopFormal(f);
    }

    [Fact]
    public async Task ActualWeightedDspChangesEstimatedLevelWhileEqualRawEnergyStaysEqual()
    {
        await using var f = new Fixture(); await f.PrepareAsync(realDsp:true); f.Command("confirm"); await StartFormal(f);
        var raw = new NoiseMeter(48000,1,32,true,f.Capture!.EmitRaw);
        var weighted = new ReferenceNoiseMeter(48000,1,32,true,f.Capture.EmitFrame);
        byte[] pcm = new byte[4800*4];
        for (int i=0;i<4800;i++) BinaryPrimitives.WriteSingleLittleEndian(pcm.AsSpan(i*4), (float)(Math.Sqrt(.002)*Math.Sin(2*Math.PI*100*i/48000)));
        for (int i=0;i<200;i++) { f.Time.Advance(.1); weighted.Push(pcm); raw.Push(pcm); }
        var state=f.Noise.Snapshot();
        Assert.InRange(state.CurrentDbfs!.Value,-30.01,-29.99);
        Assert.InRange(state.Estimate!.CurrentDbA!.Value,40,42);
        Assert.InRange(state.Estimate.Summary.Coverage,.74,.76);
        await StopFormal(f);
    }

    [Fact]
    public async Task CrashCheckpointAndReloadKeepRawSummaryReferenceVersionAndPartialEstimateTogether()
    {
        await using var f = new Fixture(); await f.PrepareAsync(); f.Command("confirm");
        var first = new NoiseTransport(f.Noise,f.DirectoryPath); first.Bind("synthetic-binding"); await StartFormal(f);
        FormalFrames(f,60,settled:false); FormalFrames(f,60);
        first.Observe();
        await using var emptyHost = new NoiseService(_=>throw new Exception("No capture"),()=>[]);
        var restored = new NoiseTransport(emptyHost,f.DirectoryPath);
        var report = restored.Reports().Single()!.AsObject(); NpepNoiseProtocol.Validate("report",report);
        Assert.Equal("Interrupted",report["outcome"]!.GetValue<string>());
        Assert.Equal(f.Noise.Snapshot().Estimate!.ProfileId,report["estimate"]!["profileId"]!.GetValue<string>());
        Assert.Equal(.5,report["estimate"]!["summary"]!["coverage"]!.GetValue<double>(),6);
        Assert.Equal(-30,report["summary"]!["energyMeanDbfs"]!.GetValue<double>(),6);
        await StopFormal(f);
    }
}

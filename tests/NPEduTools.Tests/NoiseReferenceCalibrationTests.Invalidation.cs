using NPEduTools.Contracts;

namespace NPEduTools.Tests;

public sealed partial class NoiseReferenceCalibrationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailedInvalidationWriteCannotReactivateOldReferenceAfterReload(bool quick)
    {
        await using var f = new Fixture(); await f.PrepareAsync(); f.Command("confirm");
        if (quick) { QuickCommand(f, "begin"); QuickCommand(f, "start"); await Until(() => f.Capture is { Started: true, Disposed: false }); }
        else await StartFormal(f);
        string path = System.IO.Path.Combine(f.DirectoryPath, "noise-reference-calibration.json");
        string before = File.ReadAllText(path);
        using (var held = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            f.Capture!.Signature = Signature.Replace("gain=.7", "gain=.8"); f.Time.Advance(.1);
            if (quick) f.Capture.EmitNormal(); else f.Capture.Emit(-30, true);
            Assert.Equal("REFERENCE_STORE_UNAVAILABLE", f.State.Error);
            Assert.Equal(before, File.ReadAllText(path));
            if (quick) await Until(() => !f.Noise.CaptureBusy); else await StopFormal(f);
        }
        Assert.True(File.Exists(System.IO.Path.Combine(f.DirectoryPath, "noise-reference-use.pending")));
        Capture? next = null;
        await using var restarted = new NPEduTools.Host.NoiseService(_ => next = new Capture(), () => [new("mic", "fake")], f.Time, f.DirectoryPath);
        Assert.Equal("INPUT_SETTINGS_UNAVAILABLE", restarted.ReferenceHandle(new(1, Guid.NewGuid(), "noise.reference.status")).NoiseReference!.InvalidationReason);
        var s = restarted.Snapshot(); restarted.Handle(new(1, Guid.NewGuid(), "noise.command", Noise: new("start", s.InstanceId, s.Revision, "mic")));
        await Until(() => restarted.Snapshot().State == "Active"); f.Time.Advance(.1); next!.EmitNormal(); next.Emit(-30, true);
        Assert.NotNull(restarted.Snapshot().CurrentDbfs); Assert.Null(restarted.Snapshot().Estimate!.CurrentDbA);
        Assert.Equal(("None", false), restarted.StudyThresholds(restarted.Snapshot().SessionId, new(true, -90, -80, 600, 5, new(55, 45))));
    }
    [Fact]
    public async Task CleanReferenceUseClearsMarkerAndReloadStillAllowsVerifiedEstimate()
    {
        await using var f = new Fixture(); await f.PrepareAsync(); f.Command("confirm"); await StartFormal(f);
        string marker = System.IO.Path.Combine(f.DirectoryPath, "noise-reference-use.pending"); Assert.True(File.Exists(marker));
        FormalFrames(f, 60); await StopFormal(f); Assert.False(File.Exists(marker));
        await StartFormal(f); FormalFrames(f, 60); Assert.Equal(60, f.Noise.Snapshot().Estimate!.CurrentDbA!.Value, 6); await StopFormal(f);
    }
    [Fact]
    public async Task MarkerMustBeWritableBeforeEnablingEstimate()
    {
        await using var f = new Fixture(); await f.PrepareAsync(); f.Command("confirm");
        string marker = System.IO.Path.Combine(f.DirectoryPath, "noise-reference-use.pending"); Directory.CreateDirectory(marker);
        var creates = f.NormalCreates;
        Assert.Equal("Accepted", f.Noise.StartScheduled(Guid.NewGuid()).Outcome);
        await Until(() => !f.Noise.CaptureBusy);
        Assert.Equal("Faulted", f.Noise.Snapshot().State); Assert.Equal(creates, f.NormalCreates);
        Assert.Null(f.Noise.Snapshot().Estimate); Assert.Equal("REFERENCE_STORE_UNAVAILABLE", f.State.Error);
    }
    [Fact]
    public async Task InputChangeBetweenFinalFrameAndStopStillInvalidatesReference()
    {
        await using var f = new Fixture(); await f.PrepareAsync(); f.Command("confirm"); await StartFormal(f); FormalFrames(f, 60);
        f.Capture!.Signature = Signature.Replace("gain=.7", "gain=.8"); await StopFormal(f);
        Assert.Equal("INPUT_SETTINGS_CHANGED", f.State.InvalidationReason);
        Assert.Equal("Invalidated", f.Noise.Snapshot().Estimate!.State);
        Assert.Null(f.Noise.Snapshot().Estimate!.CurrentDbA);
    }
    [Fact]
    public async Task SelectionInvalidationIsDurableEvenWithoutRunningCaptureAndWithLockedProfile()
    {
        await using var f = new Fixture(); await f.PrepareAsync(); f.Command("confirm");
        string path = System.IO.Path.Combine(f.DirectoryPath, "noise-reference-calibration.json");
        using (var held = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            var s = f.Noise.Snapshot();
            Assert.Equal("Succeeded", f.Noise.Handle(new(1, Guid.NewGuid(), "noise.command", Noise: new("select", s.InstanceId, s.Revision, "other"))).Outcome);
            s = f.Noise.Snapshot();
            Assert.Equal("Succeeded", f.Noise.Handle(new(1, Guid.NewGuid(), "noise.command", Noise: new("select", s.InstanceId, s.Revision, "mic"))).Outcome);
        }
        await using var restarted = new NPEduTools.Host.NoiseService(_ => new Capture(), () => [], f.Time, f.DirectoryPath);
        Assert.Equal("INPUT_SETTINGS_UNAVAILABLE", restarted.ReferenceHandle(new(1, Guid.NewGuid(), "noise.reference.status")).NoiseReference!.InvalidationReason);
    }
    private static HostResponse QuickCommand(Fixture f, string action)
    {
        var state = f.Noise.CalibrationHandle(new(1, Guid.NewGuid(), "noise.calibration.status")).NoiseCalibration!;
        return f.Noise.CalibrationHandle(new(1, Guid.NewGuid(), "noise.calibration.command",
            NoiseCalibration: new(action, state.InstanceId, state.Revision, Secret,
                action == "begin" ? null : state.DraftId,
                action == "start" ? "AdministratorVoice" : null, action == "start" ? "Acceptable" : null)));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task CalibrationInputChangesWithdrawOldReferenceEvenWhenSettingsAreRestored(bool quick, bool unreadable)
    {
        await using var f = new Fixture(); await f.PrepareAsync(); f.Command("confirm");
        if (quick)
        {
            Assert.Equal("Succeeded", QuickCommand(f, "begin").Outcome);
            Assert.Equal("Accepted", QuickCommand(f, "start").Outcome);
            await Until(() => f.Capture is { Started: true, Disposed: false });
        }
        else { f.Command("begin"); await f.StartAsync(); }
        f.Capture!.Signature = Signature.Replace("gain=.7", "gain=.8");
        f.Capture.SignatureUnavailable = unreadable;
        f.Time.Advance(.1);
        if (quick) f.Capture.EmitNormal(); else f.Capture.Emit(-40, false);
        await Until(() => !f.Noise.CaptureBusy);
        string reason = unreadable ? "INPUT_SETTINGS_UNAVAILABLE" : "INPUT_SETTINGS_CHANGED";
        Assert.Equal(reason, f.State.InvalidationReason);
        // The factory now reports the original settings again, including after a Host restart.
        await StartFormal(f); FormalFrames(f, 60);
        Assert.Null(f.Noise.Snapshot().Estimate!.CurrentDbA);
        await StopFormal(f);
        await using var restarted = new NPEduTools.Host.NoiseService(_ => new Capture(), () => [new("mic", "fake")], f.Time, f.DirectoryPath);
        Assert.Equal(reason, restarted.ReferenceHandle(new(1, Guid.NewGuid(), "noise.reference.status")).NoiseReference!.InvalidationReason);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancellingCalibrationWithUnchangedInputPreservesConfirmedReference(bool quick)
    {
        await using var f = new Fixture(); await f.PrepareAsync(); f.Command("confirm");
        var profile = f.State.Profile;
        if (quick)
        {
            QuickCommand(f, "begin"); QuickCommand(f, "start");
            await Until(() => f.Capture is { Started: true, Disposed: false });
            f.Time.Advance(.1); f.Capture!.EmitNormal(); QuickCommand(f, "cancel");
        }
        else
        {
            f.Command("begin"); await f.StartAsync();
            f.Time.Advance(.1); f.Capture!.Emit(-40, false); f.Command("cancel");
        }
        await Until(() => !f.Noise.CaptureBusy);
        Assert.Null(f.State.InvalidationReason); Assert.Equal(profile, f.State.Profile);
        await StartFormal(f); FormalFrames(f, 60);
        Assert.Equal(60, f.Noise.Snapshot().Estimate!.CurrentDbA!.Value, 6);
        await StopFormal(f);
    }

    [Fact]
    public async Task QuickCalibrationOpeningWithChangedSettingsWithdrawsReferenceWithoutEnablingWeightedDsp()
    {
        await using var f = new Fixture(); await f.PrepareAsync(); f.Command("confirm");
        f.NormalSignature = Signature.Replace("gain=.7", "gain=.8");
        QuickCommand(f, "begin"); Assert.Equal("Accepted", QuickCommand(f, "start").Outcome);
        await Until(() => f.Capture is { Started: true, Disposed: false });
        Assert.Equal(0, f.Capture!.ReferenceEnableCalls);
        Assert.Equal("INPUT_SETTINGS_CHANGED", f.State.InvalidationReason);
        QuickCommand(f, "cancel"); await Until(() => !f.Noise.CaptureBusy);
        f.NormalSignature = Signature;
        await StartFormal(f); FormalFrames(f, 60);
        Assert.Null(f.Noise.Snapshot().Estimate!.CurrentDbA);
        await StopFormal(f);
    }
}

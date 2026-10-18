using System.Buffers.Binary;
using System.Runtime.Versioning;
using System.Text.Json;
using NPEduTools.Contracts;
using NPEduTools.Core;
using NPEduTools.Host;
using NPEduTools.Integrations.Npep;

namespace NPEduTools.Tests;

public sealed partial class NoiseReferenceCalibrationTests
{
    private const string Secret = "reference-administrator-2026";
    private const string Signature = ReferenceNoiseMeter.AlgorithmVersion + "|48000|1|32|IeeeFloat|gain=.7|mute=False";
    private static readonly DateTimeOffset Epoch = new(2026, 10, 11, 0, 0, 0, TimeSpan.Zero);
    private static NoiseReferenceSample Point(string role, double digital, double reference) =>
        new(Guid.NewGuid(), role, 10, 1, digital, digital - .2, digital + .2, 0, "Good", Epoch, Epoch.AddSeconds(10), reference, null, null, true);
    private static NoiseReferenceDraft Draft(params NoiseReferenceSample[] samples) =>
        new(Guid.NewGuid(), "mic", "DLY-2202", "Slow", 1, 1, Signature, samples);

    [Fact]
    public void OffsetUsesPrimaryAndSecondLevelChecksFixedUnitSlope()
    {
        var evaluation = NoiseReferenceCalibration.Evaluate(Draft(Point("Primary", -40, 50), Point("Check", -30.2, 60)));
        Assert.True(evaluation.Ready); Assert.Equal(90, evaluation.OffsetDb);
        Assert.InRange(evaluation.CheckErrorDb!.Value, -.201, -.199);
        Assert.False(NoiseReferenceCalibration.Evaluate(Draft(Point("Primary", -40, 50), Point("Check", -34, 60))).Ready);
    }
    [Theory]
    [InlineData(-39, 51)]
    [InlineData(-30, 45)]
    [InlineData(-40, 60)]
    public void SimilarOrOppositeLevelsDoNotProveSensitivity(double digital, double reference)
        => Assert.False(NoiseReferenceCalibration.Evaluate(Draft(Point("Primary", -40, 50), Point("Check", digital, reference))).Ready);

    [Theory]
    [InlineData("Interrupted")]
    [InlineData("Invalid")]
    [InlineData("Incomplete")]
    [InlineData("Clipping")]
    public void BadQualityNeverConfirmsEvenMatchingReadings(string quality)
        => Assert.False(NoiseReferenceCalibration.Evaluate(Draft(Point("Primary", -40, 50) with { Quality = quality }, Point("Check", -30, 60))).Ready);

    [Fact]
    public void UnstableSignalOrReferenceAndUnconfirmedWindowAreRejected()
    {
        var first = Point("Primary", -40, 50); var second = Point("Check", -30, 60);
        Assert.False(NoiseReferenceCalibration.Evaluate(Draft(first with { MinimumDbfs = -45 }, second)).Ready);
        Assert.False(NoiseReferenceCalibration.Evaluate(Draft(first with { ReferenceMinimumDbA = 45, ReferenceMaximumDbA = 52 }, second)).Ready);
        Assert.False(NoiseReferenceCalibration.Evaluate(Draft(first with { SameWindowConfirmed = false }, second)).Ready);
        Assert.False(NoiseReferenceCalibration.Evaluate(Draft(first with { ReferenceMinimumDbA = 49 }, second)).Ready);
        Assert.False(NoiseReferenceCalibration.Evaluate(Draft(first, first)).Ready);
        Assert.False(NoiseReferenceCalibration.Evaluate(Draft(first) with { StabilityDb = double.NaN }).Ready);
        Assert.False(NoiseReferenceCalibration.Evaluate(Draft(first with { DigitalDbfs = double.NaN }, second)).Ready);
        Assert.False(NoiseReferenceCalibration.Evaluate(Draft(first with { Coverage = 0 }, second)).Ready);
    }

    [Theory]
    [InlineData("Fast", -26.98970004336)]
    [InlineData("Slow", -30)]
    public void SettledTenSecondStatisticsUseChosenTimeWeightedEnergy(string mode, double expected)
    {
        var collector = new NoiseReferenceCalibration("Primary", mode, Epoch);
        for (int i = 0; i < 150; i++) collector.Receive(new(.1, .1, .002, .001, .1, 0, false, i >= 50), (i + 1) / 10d);
        var sample = collector.Finish(15, false);
        Assert.Equal("Good", sample.Quality); Assert.InRange(sample.Seconds, 9.9999, 10.0001);
        Assert.InRange(Math.Abs(sample.DigitalDbfs!.Value - expected), 0, 1e-8);
        Assert.Equal(Epoch.AddSeconds(5), sample.WindowStartedAt); Assert.Equal(Epoch.AddSeconds(15), sample.WindowEndedAt);
        Assert.True(collector.Complete);
    }

    [Theory]
    [InlineData(0.002, false, "Clipping")]
    [InlineData(0, true, "Invalid")]
    public void CollectorRejectsClippingAndInvalidWindows(double clipping, bool invalid, string expected)
    {
        var collector = new NoiseReferenceCalibration("Primary", "Slow", Epoch);
        for (int i = 0; i < 150; i++) collector.Receive(new(.1, .001, .001, .001, .1, clipping, invalid, i >= 50), (i + 1) / 10d);
        Assert.Equal(expected, collector.Finish(15, false).Quality);
    }

    [Fact]
    public void WarmupMissingSilenceBurstGapsAndOverrunAreNotGoodSamples()
    {
        foreach (string variant in new[] { "NoWarmup", "Silence", "Floor", "Burst", "Gap", "Overrun" })
        {
            var collector = new NoiseReferenceCalibration("Primary", "Slow", Epoch);
            for (int i = 0; i < 150; i++)
            {
                double energy = variant == "Silence" ? 0 : variant == "Floor" ? 1e-20 : .001;
                double elapsed = variant == "Burst" ? 20 : variant == "Gap" ? (i + 1) / 5d : (i + 1) / 10d;
                collector.Receive(new(.1, energy, energy, energy, .1, 0, false, variant != "NoWarmup" && i >= 50), elapsed);
            }
            Assert.NotEqual("Good", collector.Finish(variant is "Gap" or "Overrun" ? 30 : variant == "Burst" ? 20 : 15, false).Quality);
        }
    }

    [Fact]
    public void ContractRequiresExplicitLimitsModeMatchingAndCompleteReferenceRanges()
    {
        var command = new NoiseReferenceCommand("begin", Guid.NewGuid(), 0, Secret,
            Instrument: "DLY-2202", TimeWeighting: "Slow", StabilityDb: 1, CheckToleranceDb: 1);
        Assert.True(NoiseReferenceContract.Valid(command));
        Assert.False(NoiseReferenceContract.Valid(command with { StabilityDb = null }));
        Assert.False(NoiseReferenceContract.Valid(command with { CheckToleranceDb = double.NaN }));
        Assert.False(NoiseReferenceContract.Valid(command with { TimeWeighting = "LAeq" }));
        Assert.False(NoiseReferenceContract.Valid(command with { Role = "Primary" }));
        Assert.False(NoiseReferenceContract.Valid(command with { Instrument = "bad\nmodel" }));
        var reading = new NoiseReferenceCommand("reference", Guid.NewGuid(), 0, Secret, Guid.NewGuid(),
            SampleId: Guid.NewGuid(), ReferenceDbA: 50, SameWindowConfirmed: true);
        Assert.True(NoiseReferenceContract.Valid(reading));
        Assert.False(NoiseReferenceContract.Valid(reading with { SameWindowConfirmed = false }));
        Assert.False(NoiseReferenceContract.Valid(reading with { ReferenceMinimumDbA = 49 }));
        Assert.False(NoiseReferenceContract.Valid(reading with { ReferenceDbA = 29.9 }));
        Assert.False(NoiseReferenceContract.Valid(reading with { ReferenceDbA = double.PositiveInfinity }));
        Assert.Null(Protocol.Validate(new(1, Guid.NewGuid(), "noise.reference.command", NoiseReference: reading)));
        Assert.NotNull(Protocol.Validate(new(1, Guid.NewGuid(), "noise.status", NoiseReference: reading)));
        Assert.NotNull(Protocol.Validate(new(1, Guid.NewGuid(), "noise.reference.status", ObserveMs: 1)));
        Assert.DoesNotContain("noiseReference", JsonSerializer.Serialize(new HostResponse(1, Guid.NewGuid(), "Succeeded", null, "old"), Protocol.Json));
    }

    private sealed class Clock : TimeProvider
    {
        private long _ms;
        public override long TimestampFrequency => 1000;
        public override long GetTimestamp() => Interlocked.Read(ref _ms);
        public override DateTimeOffset GetUtcNow() => Epoch.AddMilliseconds(GetTimestamp());
        public void Advance(double seconds) => Interlocked.Add(ref _ms, (long)Math.Round(seconds * 1000));
    }
    private sealed class Capture : IReferenceNoiseCapture
    {
        public string DeviceName => "Synthetic reference microphone";
        private string _signature = NoiseReferenceCalibrationTests.Signature;
        public bool SignatureUnavailable;
        public string Signature
        {
            get => SignatureUnavailable ? throw new IOException("Synthetic input metadata unavailable") : _signature;
            set => _signature = value;
        }
        public bool Disposed;
        public bool Started;
        public bool CleanupFails;
        public ManualResetEventSlim? CleanupGate;
        public bool ReferenceUnavailable;
        public int ReferenceEnableCalls;
        public void EnableReference() { ReferenceEnableCalls++; if (ReferenceUnavailable) throw new ArgumentException("Synthetic unsupported A format"); }
        public event Action<NoiseFrame>? Frame;
        public event Action<ReferenceNoiseFrame>? ReferenceFrame;
        public event Action<string>? Failed;
        public void Start() => Started = true;
        public void Emit(double level, bool settled, double clipping = 0) => ReferenceFrame?.Invoke(
            new(.1, Math.Pow(10, level / 10), Math.Pow(10, (level + 3.0103) / 10), Math.Pow(10, level / 10), .1, clipping, false, settled));
        public void EmitFrame(ReferenceNoiseFrame frame) => ReferenceFrame?.Invoke(frame);
        public void EmitNormal() => Frame?.Invoke(new(.1, .001, .1, 0, false));
        public void EmitRaw(NoiseFrame frame) => Frame?.Invoke(frame);
        public void Disconnect() => Failed?.Invoke("Synthetic reference disconnect");
        public void Dispose() { CleanupGate?.Wait(TimeSpan.FromSeconds(6)); Disposed = true; if (CleanupFails) throw new IOException("Synthetic cleanup failure"); }
    }
    private sealed class Fixture : IAsyncDisposable
    {
        public readonly string DirectoryPath = Path.Combine(Path.GetTempPath(), "npep-reference-" + Guid.NewGuid());
        public readonly Clock Time = new();
        public readonly NoiseManagementStore Management;
        public readonly NoiseService Noise;
        public Capture? Capture;
        public int NormalCreates, ReferenceCreates, Reports;
        public bool Paused, ThrowPaused, DeviceMissing, WrongFormat;
        public string? NormalSignature;
        public bool NormalReferenceUnavailable;
        public Fixture(string deviceId = "mic", string? signature = null)
        {
            Management = new(DirectoryPath, Time); Assert.Null(Management.Configure(null, Secret, false));
            Noise = new(_ => { NormalCreates++; return Capture = new() { Signature = NormalSignature ?? Signature, ReferenceUnavailable = NormalReferenceUnavailable }; },
                () => DeviceMissing ? [] : [new(deviceId, "Synthetic microphone")], Time, DirectoryPath, Management,
                referenceCreate: _ => { ReferenceCreates++; if (WrongFormat) throw new ArgumentException("Synthetic unsupported format"); return Capture = new() { Signature = signature ?? Signature }; },
                referencePaused: () => ThrowPaused ? throw new IOException("Synthetic unavailable runtime") : Paused);
            var s = Noise.Snapshot(); Assert.Equal("Succeeded", Noise.Handle(new(1, Guid.NewGuid(), "noise.command", Noise: new("select", s.InstanceId, s.Revision, deviceId))).Outcome);
            Noise.Completed += _ => Reports++;
        }
        public NoiseReferenceState State => Noise.ReferenceHandle(new(1, Guid.NewGuid(), "noise.reference.status")).NoiseReference!;
        public NoiseReferenceCommand Make(string action, string? role = null, Guid? sample = null, double? reference = null, string secret = Secret)
        {
            var state = State;
            return new(action, state.InstanceId, state.Revision, secret, action is "begin" or "clear" ? null : state.Draft?.Id,
                action == "begin" ? "DLY-2202" : null, action == "begin" ? "Slow" : null,
                action == "begin" ? 1 : null, action == "begin" ? 1 : null, role, sample,
                ReferenceDbA: reference, SameWindowConfirmed: action == "reference");
        }
        public HostResponse Send(NoiseReferenceCommand cmd, StudyHostState? study = null) => Noise.ReferenceHandle(
            new(1, Guid.NewGuid(), "noise.reference.command", NoiseReference: cmd), study);
        public HostResponse Command(string action, string? role = null, Guid? sample = null, double? reference = null) => Send(Make(action, role, sample, reference));
        public async Task StartAsync(string role = "Primary")
        { Assert.Equal("Accepted", Command("start", role).Outcome); await Until(() => Capture is { Started: true, Disposed: false }); }
        public async Task SampleAsync(string role, double level, bool realDsp = false)
        {
            await StartAsync(role);
            ReferenceNoiseMeter? meter = realDsp ? new(48000, 1, 32, true, Capture!.EmitFrame) : null;
            byte[] buffer = new byte[4800 * 4];
            if (realDsp)
                for (int i = 0; i < 4800; i++) BinaryPrimitives.WriteSingleLittleEndian(buffer.AsSpan(i * 4),
                    (float)(Math.Sqrt(2 * Math.Pow(10, level / 10)) * Math.Sin(2 * Math.PI * 1000 * i / 48000)));
            for (int i = 0; i < 150; i++) { Time.Advance(.1); if (meter is not null) meter.Push(buffer); else Capture!.Emit(level, i >= 50); }
            await Until(() => State.State == "Ready"); Assert.True(Capture!.Disposed);
        }
        public async Task PrepareAsync(bool realDsp = false)
        {
            Assert.Equal("Succeeded", Command("begin").Outcome);
            await SampleAsync("Primary", -40, realDsp);
            Assert.Equal("Succeeded", Command("reference", sample: State.Draft!.Samples.Single().Id, reference: 50).Outcome);
            await SampleAsync("Check", -30, realDsp);
            Assert.Equal("Succeeded", Command("reference", sample: State.Draft!.Samples.Single(s => s.Role == "Check").Id, reference: 60).Outcome);
        }
        public async ValueTask DisposeAsync() { await Noise.DisposeAsync(); Directory.Delete(DirectoryPath, true); }
    }
    private static async Task Until(Func<bool> test)
    { using var timeout = new CancellationTokenSource(6000); while (!test()) await Task.Delay(10, timeout.Token); }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FullReferenceFlowSavesOnlyStatisticsAndDoesNotApplyOldUnits(bool realDsp)
    {
        await using var f = new Fixture(); await f.PrepareAsync(realDsp);
        Assert.True(f.State.Evaluation.Ready); Assert.Equal("Succeeded", f.Command("confirm").Outcome);
        Assert.NotNull(f.State.Profile); Assert.True(f.State.FreshSignatureMatched);
        Assert.Equal(0, f.NormalCreates); Assert.Equal(2, f.ReferenceCreates); Assert.Equal(0, f.Reports);
        var normal = f.Noise.Snapshot(); Assert.Null(normal.CurrentDbfs); Assert.Null(normal.Summary); Assert.Null(normal.SessionId);
        var policy = new StudyNoise(true, -15, -25, 600, 5);
        Assert.Equal(policy, f.Noise.ResolveCalibration("school/class", new string('a', 64), policy));
        string json = File.ReadAllText(Path.Combine(f.DirectoryPath, "noise-reference-calibration.json"));
        Assert.DoesNotContain(Secret, json); Assert.DoesNotContain("pcm", json, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(2, f.State.Profile!.Samples.Count); Assert.Equal("ManualReference", f.State.Profile.Method);
        await using var restarted = new NoiseService(_ => throw new Exception("No startup capture"), () => [], f.Time, f.DirectoryPath, f.Management,
            referenceCreate: _ => throw new Exception("No startup reference"));
        var reloaded = restarted.ReferenceHandle(new(1, Guid.NewGuid(), "noise.reference.status")).NoiseReference!;
        Assert.NotNull(reloaded.Profile); Assert.False(reloaded.FreshSignatureMatched);
        Assert.Equal("REFERENCE_DEVICE_SETTINGS_CHANGED", restarted.ReferenceHandle(new(1, Guid.NewGuid(), "noise.reference.command",
            NoiseReference: new("confirm", reloaded.InstanceId, reloaded.Revision, Secret, reloaded.Draft!.Id))).ErrorCode);
        Assert.Equal("Succeeded", f.Command("clear").Outcome); Assert.Null(f.State.Profile);
    }

    [Fact]
    public async Task StatusDraftAndInvalidSecretNeverOpenAnyDevice()
    {
        await using var f = new Fixture(); _ = f.State;
        Assert.Equal("MANAGEMENT_SECRET_INVALID", f.Send(f.Make("begin", secret: "wrong-password-2026")).ErrorCode);
        Assert.Equal("Succeeded", f.Command("begin").Outcome);
        Assert.Equal(0, f.ReferenceCreates); Assert.Equal(0, f.NormalCreates);
        Assert.Equal("REFERENCE_CHECK_FAILED", f.Command("confirm").ErrorCode);
    }

    [Theory]
    [InlineData("Preparing")]
    [InlineData("Active")]
    public async Task SchoolWindowPreventsStartingReference(string phase)
    {
        await using var f = new Fixture(); f.Command("begin");
        var study = new StudyHostState(null, "APPLIED", "", "", phase, "", true, null, null, null, null);
        Assert.Equal("REFERENCE_SCHEDULE_BUSY", f.Send(f.Make("start", "Primary"), study).ErrorCode);
        Assert.Equal(0, f.ReferenceCreates);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RuntimePauseOrUnreadableFlagsRejectReference(bool broken)
    {
        await using var f = new Fixture(); f.Command("begin"); f.Paused = !broken; f.ThrowPaused = broken;
        Assert.Equal("Busy", f.State.State); Assert.Equal("REFERENCE_SCHEDULE_BUSY", f.Command("start", "Primary").ErrorCode);
        Assert.Equal(0, f.ReferenceCreates);
    }

    [Fact]
    public async Task OrdinaryCaptureIsNotStoppedOrTakenOverByReference()
    {
        await using var f = new Fixture(); f.Command("begin"); var state = f.Noise.Snapshot();
        Assert.Equal("Accepted", f.Noise.Handle(new(1, Guid.NewGuid(), "noise.command", Noise: new("start", state.InstanceId, state.Revision, "mic"))).Outcome);
        await Until(() => f.Noise.Snapshot().State == "Active");
        Assert.Equal("NoiseBusy", f.Command("start", "Primary").ErrorCode);
        Assert.Equal("Active", f.Noise.Snapshot().State); Assert.False(f.Capture!.Disposed); Assert.Equal(0, f.ReferenceCreates);
    }

    [Fact]
    public async Task ScheduledStartInterruptsReferenceAndWaitsForRelease()
    {
        await using var f = new Fixture(); f.Command("begin"); await f.StartAsync(); var reference = f.Capture!;
        Assert.Equal("Rejected", f.Noise.StartScheduled(Guid.NewGuid()).Outcome);
        await Until(() => f.State.State == "Ready"); Assert.True(reference.Disposed);
        Assert.Equal("Interrupted", Assert.Single(f.State.Draft!.Samples).Quality); Assert.Equal(0, f.Reports);
        Assert.Equal("Accepted", f.Noise.StartScheduled(Guid.NewGuid()).Outcome);
        await Until(() => f.Noise.Snapshot().State == "Active"); Assert.Equal(1, f.NormalCreates);
    }

    [Fact]
    public async Task CancelAndRemoteStopOwnershipCannotStopOtherSession()
    {
        await using var f = new Fixture(); f.Command("begin"); await f.StartAsync();
        var state = f.Noise.Snapshot();
        Assert.Equal("CalibrationOwned", f.Noise.Handle(new(1, Guid.NewGuid(), "noise.command", Noise: new("stop", state.InstanceId, state.Revision))).ErrorCode);
        Assert.Equal("Succeeded", f.Command("cancel").Outcome);
        await Until(() => f.State.State == "Ready"); Assert.Equal("Interrupted", f.State.Draft!.Samples.Single().Quality);
        Assert.Equal(0, f.Reports);
    }

    [Fact]
    public async Task InputGainChangeDuringSamplingInvalidatesAndDisposes()
    {
        await using var f = new Fixture(); f.Command("begin"); await f.StartAsync();
        f.Capture!.Signature = Signature.Replace("gain=.7", "gain=.8"); f.Time.Advance(.1); f.Capture.Emit(-40, false);
        await Until(() => f.State.State == "Ready");
        Assert.True(f.Capture.Disposed); Assert.Equal("Interrupted", f.State.Draft!.Samples.Single().Quality);
        Assert.False(f.State.FreshSignatureMatched); Assert.Equal(0, f.Reports);
    }

    [Fact]
    public async Task RuntimePauseDuringReferenceCancelsWithoutDependingOnStatusPolling()
    {
        await using var f = new Fixture(); f.Command("begin"); await f.StartAsync(); f.Paused = true;
        await Until(() => f.Capture!.Disposed); f.Paused = false;
        Assert.Equal("Interrupted", f.State.Draft!.Samples.Single().Quality); Assert.Equal(0, f.Reports);
    }

    [Fact]
    public async Task StartingOrdinaryCaptureWithdrawsReferenceFreshnessButPreservesProfile()
    {
        await using var f = new Fixture(); await f.PrepareAsync(); f.Command("confirm"); var profile = f.State.Profile;
        var state = f.Noise.Snapshot();
        Assert.Equal("Accepted", f.Noise.Handle(new(1, Guid.NewGuid(), "noise.command", Noise: new("start", state.InstanceId, state.Revision, "mic"))).Outcome);
        await Until(() => f.Noise.Snapshot().State == "Active");
        Assert.False(f.State.FreshSignatureMatched); Assert.Equal(profile, f.State.Profile);
    }

    [Fact]
    public async Task ProfileValidationRejectsTamperedOffsetAndPreservesTheFile()
    {
        await using var f = new Fixture(); await f.PrepareAsync(); f.Command("confirm");
        string path = Path.Combine(f.DirectoryPath, "noise-reference-calibration.json");
        var file = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(path))!;
        file["profile"]!["offsetDb"] = 999;
        string text = file.ToJsonString(); File.WriteAllText(path, text);
        await using var reloaded = new NoiseService(_ => throw new Exception("No capture"), () => [], f.Time, f.DirectoryPath, f.Management);
        var state = reloaded.ReferenceHandle(new(1, Guid.NewGuid(), "noise.reference.status")).NoiseReference!;
        Assert.Equal("REFERENCE_STORE_UNAVAILABLE", state.Error); Assert.Null(state.Profile);
        Assert.Equal(text, File.ReadAllText(path));
    }

    [Fact]
    public async Task MismatchedAlgorithmAtOpenNeverStartsFallbackCapture()
    {
        string directory = Path.Combine(Path.GetTempPath(), "npep-reference-signature-" + Guid.NewGuid());
        var management = new NoiseManagementStore(directory); Assert.Null(management.Configure(null, Secret, false));
        var capture = new Capture { Signature = "pcm-energy-v1|48000|1|32|float" };
        int normal = 0;
        try
        {
            await using var noise = new NoiseService(_ => { normal++; return new Capture(); }, () => [new("mic", "Synthetic")],
                directory: directory, management: management, referenceCreate: _ => capture);
            var original = noise.Snapshot(); noise.Handle(new(1, Guid.NewGuid(), "noise.command", Noise: new("select", original.InstanceId, original.Revision, "mic")));
            var state = noise.ReferenceHandle(new(1, Guid.NewGuid(), "noise.reference.status")).NoiseReference!;
            var draft = noise.ReferenceHandle(new(1, Guid.NewGuid(), "noise.reference.command", NoiseReference:
                new("begin", state.InstanceId, state.Revision, Secret, Instrument: "DLY-2202", TimeWeighting: "Slow", StabilityDb: 1, CheckToleranceDb: 1))).NoiseReference!;
            noise.ReferenceHandle(new(1, Guid.NewGuid(), "noise.reference.command", NoiseReference: new("start", draft.InstanceId, draft.Revision, Secret, draft.Draft!.Id, Role: "Primary")));
            await Until(() => capture.Disposed);
            Assert.Equal(0, normal); Assert.Equal("Idle", noise.Snapshot().State);
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task DifferentInputSignatureCannotMixSamplesOrReplaceConfirmedProfile()
    {
        await using var f = new Fixture(); await f.PrepareAsync(); f.Command("confirm"); var profile = f.State.Profile;
        f.Command("start", "Check"); await Until(() => f.Capture is { Disposed: false });
        // Stop explicitly: signature changes must be observed in a real reference frame.
        f.Capture!.Signature = Signature + "|changed"; f.Time.Advance(.1); f.Capture.Emit(-30, false);
        await Until(() => f.State.State == "Ready");
        Assert.Equal(profile, f.State.Profile); Assert.False(f.State.Evaluation.Ready); Assert.False(f.State.FreshSignatureMatched);
    }

    [Theory]
    [InlineData("Disconnect")]
    [InlineData("NoData")]
    [InlineData("Deadline")]
    public async Task FailuresWithoutAnOpenWindowReleaseCaptureAndNeverReport(string action)
    {
        await using var f = new Fixture(); f.Command("begin"); await f.StartAsync();
        if (action == "Disconnect") f.Capture!.Disconnect(); else f.Time.Advance(action == "Deadline" ? 21 : 4);
        // Do not poll the reference snapshot, whose safety deadline is an extra guard.
        await Until(() => f.Capture!.Disposed);
        Assert.False(f.State.Evaluation.Ready); Assert.Equal(0, f.Reports);
        Assert.NotEqual("Good", f.State.Draft!.Samples.Single().Quality);
    }

    [Fact]
    public async Task UnavailableOrUnsupportedFormatNeverFallsBackToAnotherMicrophoneOrOldMeter()
    {
        await using var f = new Fixture(); f.Command("begin"); f.DeviceMissing = true;
        Assert.Equal("NoiseDeviceUnavailable", f.Command("start", "Primary").ErrorCode); Assert.Equal(0, f.ReferenceCreates);
        f.DeviceMissing = false; f.WrongFormat = true;
        Assert.Equal("Accepted", f.Command("start", "Primary").Outcome);
        await Until(() => f.State.Draft!.Samples.Count == 1 && f.State.State == "Ready");
        Assert.Contains("未回退", f.State.CaptureMessage); Assert.Equal(0, f.NormalCreates); Assert.Equal(0, f.Reports);
    }

    [Fact]
    public async Task StaleRevisionSampleIdentityAndExpiredSignatureCannotConfirm()
    {
        await using var f = new Fixture(); f.Command("begin"); var stale = f.Make("start", "Primary");
        f.Command("begin"); Assert.Equal("REFERENCE_CHANGED", f.Send(stale).ErrorCode);
        await f.PrepareAsync();
        Assert.Equal("REFERENCE_CHANGED", f.Command("reference", sample: Guid.NewGuid(), reference: 50).ErrorCode);
        f.Time.Advance(601); Assert.Equal("REFERENCE_DEVICE_SETTINGS_CHANGED", f.Command("confirm").ErrorCode);
        Assert.Null(f.State.Profile);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StaleBeginCannotReplaceNewReferenceSamples(bool differentInstance)
    {
        await using var f = new Fixture();
        Assert.Equal("Succeeded", f.Command("begin").Outcome);
        var confirmed = f.Make("begin");
        Assert.Equal("Succeeded", f.Command("begin").Outcome);
        await f.SampleAsync("Primary", -40);
        var current = f.State;
        var command = differentInstance ? confirmed with { InstanceId = Guid.NewGuid(), Revision = current.Revision } : confirmed;
        Assert.Equal("REFERENCE_CHANGED", f.Send(command).ErrorCode);
        Assert.Equal(current.Revision, f.State.Revision);
        Assert.Equal(current.Draft, f.State.Draft);
    }

    [Theory]
    [InlineData("{broken")]
    [InlineData("{\"version\":99}")]
    [InlineData("oversize")]
    public async Task InvalidStoresArePreservedWithoutCaptureOrNewActivation(string content)
    {
        await using var f = new Fixture(); string path = Path.Combine(f.DirectoryPath, "noise-reference-calibration.json");
        if (content == "oversize") content = new string('x', 65537);
        File.WriteAllText(path, content);
        Assert.Equal("REFERENCE_STORE_UNAVAILABLE", f.State.Error);
        Assert.Equal("REFERENCE_STORE_UNAVAILABLE", f.Command("begin").ErrorCode);
        Assert.Equal(content, File.ReadAllText(path)); Assert.Equal(0, f.ReferenceCreates);
    }

    [Fact]
    public async Task FailedSaveDoesNotActivateAnInMemoryProfile()
    {
        await using var f = new Fixture(); await f.PrepareAsync();
        string path = Path.Combine(f.DirectoryPath, "noise-reference-calibration.json"); File.Delete(path); Directory.CreateDirectory(path);
        Assert.Equal("REFERENCE_STORE_UNAVAILABLE", f.Command("confirm").ErrorCode);
        Assert.Null(f.State.Profile);
    }

    [Fact]
    public async Task OversizedSerializedProfilePreservesReadableDraftAndDoesNotActivate()
    {
        string signature = ReferenceNoiseMeter.AlgorithmVersion + "|" + new string('\u4e2d', 4000);
        await using var f = new Fixture(new string('\u4e2d', 2048), signature);
        await f.PrepareAsync(); Assert.True(f.State.Evaluation.Ready);
        string path = Path.Combine(f.DirectoryPath, "noise-reference-calibration.json");
        string before = File.ReadAllText(path); Assert.True(new FileInfo(path).Length <= 64 * 1024);
        Assert.Equal("REFERENCE_STORE_UNAVAILABLE", f.Command("confirm").ErrorCode);
        Assert.Null(f.State.Profile); Assert.Equal(before, File.ReadAllText(path));
        Assert.Empty(Directory.GetFiles(f.DirectoryPath, "*.tmp"));
        await using var restarted = new NoiseService(_ => throw new Exception("No capture"), () => [],
            f.Time, f.DirectoryPath, f.Management);
        var state = restarted.ReferenceHandle(new(1, Guid.NewGuid(), "noise.reference.status")).NoiseReference!;
        Assert.Null(state.Error); Assert.NotNull(state.Draft); Assert.Null(state.Profile);
    }

    [Fact]
    public async Task FailedCleanupBlocksFurtherCapturesAndInvalidatesReference()
    {
        await using var f = new Fixture(); f.Command("begin"); await f.StartAsync(); f.Capture!.CleanupFails = true;
        f.Command("cancel"); await Until(() => f.State.State == "Ready");
        Assert.Equal("NoiseCleanupRequired", f.Command("start", "Check").ErrorCode);
        Assert.False(f.State.FreshSignatureMatched); Assert.Equal(0, f.Reports);
    }

    [Fact]
    public async Task LegacyStatusRemainsSchemaCompatibleAndOutboxDoesNotCheckpointReference()
    {
        await using var f = new Fixture(); var transport = new NoiseTransport(f.Noise, Path.Combine(f.DirectoryPath, "npep"));
        transport.Bind("synthetic-school/class"); f.Command("begin"); await f.StartAsync();
        Assert.True(f.Noise.CaptureBusy);
        var wire = transport.Observe(); NpepNoiseProtocol.Validate("status", wire);
        Assert.Equal("Idle", wire["state"]!.GetValue<string>()); Assert.Null(wire["sessionId"]); Assert.Null(wire["summary"]);
        Assert.Empty(transport.Reports());
        var file = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(Path.Combine(f.DirectoryPath, "npep", "noise-outbox.json")))!;
        Assert.Null(file["checkpoint"]);
        f.Command("cancel"); await Until(() => !f.Noise.CaptureBusy);
        NpepNoiseProtocol.Validate("status", transport.Observe()); Assert.Empty(transport.Reports());
    }

    [Fact]
    public async Task ActualSchedulerTreatsReferenceAsPreemptibleNotAsManualMonitoring()
    {
        await using var f = new Fixture(); f.Command("begin"); await f.StartAsync(); var reference = f.Capture!;
        var connection = Guid.NewGuid(); var bridge = Guid.NewGuid(); long sequence = 0;
        // Synthetic Thursday school wall time. No real school or configuration is touched.
        var schoolBase = new DateTimeOffset(2026, 10, 1, 19, 0, 0, TimeSpan.Zero);
        await using var scheduler = new NoiseScheduleService(f.Noise, () => new(connection, bridge, ++sequence, 0,
            schoolBase.AddMilliseconds(f.Time.GetTimestamp()), 0, "Advancing", "synthetic school time"), () => false,
            Path.Combine(f.DirectoryPath, "scheduler"), f.Time, background: false);
        scheduler.Bind("synthetic-school/class", true);
        scheduler.Confirm(System.Text.Json.Nodes.JsonNode.Parse("""
            {"policy":{"version":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","source":"Grade",
            "rules":[{"days":[4],"start":"19:00","end":"21:30"}]},"leaseSeconds":86400,"confirmed":true,"command":null}
            """)!.AsObject());
        for (int i = 0; i < 3; i++) { f.Time.Advance(.5); scheduler.Tick(); }
        await Until(() => reference.Disposed && !f.Noise.CaptureBusy);
        Assert.Equal("Interrupted", f.State.Draft!.Samples.Single().Quality);
        f.Time.Advance(.5); scheduler.Tick(); await Until(() => f.Noise.Snapshot().State == "Active");
        Assert.Equal("Schedule", scheduler.Observe()["owner"]!.GetValue<string>());
        Assert.Equal(1, f.NormalCreates); Assert.Equal(0, f.Reports);
    }

    [Fact]
    [SupportedOSPlatform("windows")]
    public async Task NamedPipeExposesReferenceOnlyOnExplicitNewCapabilities()
    {
        await using var f = new Fixture(); string name = "NPEduTools.Test.Reference." + Guid.NewGuid().ToString("N");
        using var lifetime = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        var server = new PipeServer(name, new ForbiddenReader(), _ => { }, noise: f.Noise); var running = server.RunAsync(lifetime.Token);
        try
        {
            var status = await HostClient.RequestAsync(name, "noise.reference.status", lifetime.Token); Assert.NotNull(status.NoiseReference);
            var begin = f.Make("begin");
            var reply = await HostClient.RequestAsync(name, new HostRequest(1, Guid.NewGuid(), "noise.reference.command", NoiseReference: begin), lifetime.Token);
            Assert.Equal("Succeeded", reply.Outcome); Assert.NotNull(reply.NoiseReference!.Draft);
            var normal = await HostClient.RequestAsync(name, "noise.status", lifetime.Token); Assert.Null(normal.NoiseReference);
            Assert.Equal(0, f.ReferenceCreates); Assert.Equal(0, f.NormalCreates);
        }
        finally { await lifetime.CancelAsync(); try { await running; } catch (OperationCanceledException) { } }
    }
    private sealed class ForbiddenReader : ILessonStatusReader
    {
        public Task<StatusResult> ReadAsync(StatusQuery query, CancellationToken token) => throw new InvalidOperationException("No ClassIsland probing");
    }
}

using System.Buffers.Binary;
using System.Text.Json;
using System.Text.Json.Nodes;
using NPEduTools.Contracts;
using NPEduTools.Core;
using NPEduTools.Host;
using NPEduTools.Integrations.Npep;

namespace NPEduTools.Tests;

public sealed partial class NoiseReferenceCalibrationTests
{
    private sealed class UsbClock : TimeProvider
    {
        private readonly object _gate = new();
        private long _milliseconds;
        private readonly List<ManualTimer> _timers = [];
        public override long TimestampFrequency => 1000;
        public override long GetTimestamp() => Interlocked.Read(ref _milliseconds);
        public override DateTimeOffset GetUtcNow() => Epoch.AddMilliseconds(GetTimestamp());
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new ManualTimer(this, callback, state);
            lock (_gate) { _timers.Add(timer); timer.Change(dueTime, period); }
            return timer;
        }
        public void Advance(double seconds)
        {
            List<ManualTimer> due;
            lock (_gate)
            {
                Interlocked.Add(ref _milliseconds, (long)Math.Round(seconds * 1000));
                due = _timers.Where(t => !t.Disposed && t.At <= _milliseconds).ToList();
                foreach (var t in due) t.At = t.Period >= 0 ? _milliseconds + t.Period : long.MaxValue;
            }
            foreach (var t in due) t.Callback(t.State);
        }
        private sealed class ManualTimer(UsbClock owner, TimerCallback callback, object? state) : ITimer
        {
            public TimerCallback Callback = callback;
            public object? State = state;
            public long At = long.MaxValue, Period = -1;
            public bool Disposed;
            public bool Change(TimeSpan dueTime, TimeSpan period)
            {
                lock (owner._gate)
                {
                    if (Disposed) return false;
                    At = dueTime == Timeout.InfiniteTimeSpan ? long.MaxValue : owner.GetTimestamp() + (long)dueTime.TotalMilliseconds;
                    Period = period == Timeout.InfiniteTimeSpan ? -1 : (long)period.TotalMilliseconds; return true;
                }
            }
            public void Dispose() { lock (owner._gate) { Disposed = true; owner._timers.Remove(this); } }
            public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
        }
    }
    private sealed class UsbBackend : ISoundMeterHidBackend
    {
        public int Enumerations, Opens, Disposals, Reads, Flushes;
        public int DisconnectAt = int.MaxValue;
        public byte Flags;
        public double Level = 50;
        public bool Missing, BlockRead;
        public TimeProvider? LatencyClock;
        public TimeSpan ResponseDelay;
        public readonly List<long> RequestTimes = [];
        public readonly TaskCompletionSource ReadingBlocked = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly List<byte[]> Writes = [];
        public readonly Queue<byte[]> Queued = new();
        private static SoundMeterHidDevice Device => new("synthetic-usb", "Synthetic USB 2202", Dly2202Protocol.VendorId, Dly2202Protocol.ProductId);
        public IReadOnlyList<SoundMeterHidDevice> Enumerate() { Enumerations++; return Missing ? [] : [Device]; }
        public ISoundMeterHidConnection Open(SoundMeterHidDevice device) { Opens++; return new Connection(this, device); }
        private sealed class Connection(UsbBackend owner, SoundMeterHidDevice device) : ISoundMeterHidConnection
        {
            private bool _disposed;
            public SoundMeterHidDevice Device => device;
            public int InputReportLength => 9;
            public int OutputReportLength => 9;
            public void DiscardQueuedInput() { owner.Flushes++; owner.Queued.Clear(); }
            public ValueTask WriteAsync(ReadOnlyMemory<byte> report, CancellationToken token)
            {
                token.ThrowIfCancellationRequested(); owner.Writes.Add(report.ToArray());
                if (owner.LatencyClock is { } clock) owner.RequestTimes.Add(clock.GetTimestamp());
                var response = new byte[9]; BinaryPrimitives.WriteInt16BigEndian(response.AsSpan(1), (short)Math.Round(owner.Level * 10));
                response[3] = owner.Flags; owner.Queued.Enqueue(response); return ValueTask.CompletedTask;
            }
            public async ValueTask<int> ReadAsync(Memory<byte> target, CancellationToken token)
            {
                owner.Reads++; if (owner.Reads >= owner.DisconnectAt) throw new IOException("Synthetic unplug");
                if (owner.BlockRead) { owner.ReadingBlocked.TrySetResult(); await Task.Delay(Timeout.InfiniteTimeSpan, token); }
                if (owner.ResponseDelay > TimeSpan.Zero) await Task.Delay(owner.ResponseDelay, owner.LatencyClock ?? TimeProvider.System, token);
                token.ThrowIfCancellationRequested(); owner.Queued.Dequeue().CopyTo(target); return 9;
            }
            public void Dispose() { if (!_disposed) { _disposed = true; owner.Disposals++; } }
        }
    }
    private sealed class UsbFixture : IAsyncDisposable
    {
        public readonly string DirectoryPath = Path.Combine(Path.GetTempPath(), "npep-usb-reference-" + Guid.NewGuid());
        public readonly UsbClock Time = new();
        public readonly UsbBackend Backend = new();
        public readonly NoiseManagementStore Management;
        public readonly SoundMeterService Meter;
        public readonly NoiseService Noise;
        public Capture? Capture;
        public bool NoiseDisposed;
        public bool Paused;
        public int Reports, Creates;
        public UsbFixture()
        {
            Management = new(DirectoryPath, Time); Assert.Null(Management.Configure(null, Secret, false));
            Meter = new(() => Backend, Management.VerifyCalibration);
            Noise = new(_ => Capture = new(), () => [new("mic", "Synthetic")],
                Time, DirectoryPath, Management, referenceCreate: _ => { Creates++; return Capture = new(); },
                referencePaused: () => Paused, referenceMeterOpen: Meter.OpenReferenceAsync);
            var s = Noise.Snapshot(); Noise.Handle(new(1, Guid.NewGuid(), "noise.command", Noise: new("select", s.InstanceId, s.Revision, "mic")));
            Noise.Completed += _ => Reports++;
        }
        public NoiseReferenceState State => Noise.ReferenceHandle(new(1, Guid.NewGuid(), "noise.reference.status")).NoiseReference!;
        public HostResponse Command(string action, string? role = null, string secret = Secret, Guid? sample = null)
        {
            var s = State;
            var c = new NoiseReferenceCommand(action, s.InstanceId, s.Revision, secret, action is "begin" or "clear" ? null : s.Draft?.Id,
                Instrument: action == "begin" ? "DLY-2202" : null, TimeWeighting: action == "begin" ? "Slow" : null,
                StabilityDb: action == "begin" ? 1 : null, CheckToleranceDb: action == "begin" ? 1 : null, Role: role,
                SampleId: sample, ReferenceDbA: action == "reference" ? 50 : null, SameWindowConfirmed: action == "reference",
                MeterDevicePath: action == "begin" ? "synthetic-usb" : null);
            return Noise.ReferenceHandle(new(1, Guid.NewGuid(), "noise.reference.command", NoiseReference: c));
        }
        public async Task Start(string role = "Primary")
        {
            var previous = Capture;
            Guid? previousSample = State.Draft?.Samples.SingleOrDefault(s => s.Role == role)?.Id;
            Assert.Equal("Accepted", Command("start", role).Outcome);
            await Until(() => Capture != previous && Capture is { Started: true, Disposed: false } ||
                State.Draft!.Samples.Any(s => s.Role == role && s.Id != previousSample));
        }
        public async Task Frames(int count, double digital = -40, bool changeGain = false)
        {
            for (int i = 0; i < count && Capture is { Disposed: false }; i++)
            {
                Time.Advance(.1); await Task.Delay(3);
                if (changeGain && i == count - 1) Capture.Signature = Signature + "|changed";
                Capture.Emit(digital, i >= 50);
            }
        }
        public async Task Sample(string role, double digital, double reference)
        {
            Backend.Level = reference; await Start(role); await Frames(150, digital);
            await Until(() => State.State == "Ready");
        }
        public async ValueTask DisposeAsync() { if (!NoiseDisposed) await Noise.DisposeAsync(); Directory.Delete(DirectoryPath, true); }
    }
    [Fact]
    public async Task UsbTwoLevelFlowClearsQueuedDataComputesOffsetAndPersistsBetaProfile()
    {
        await using var f = new UsbFixture(); Assert.Equal(0, f.Backend.Enumerations); Assert.Equal(0, f.Creates);
        Assert.Equal("Succeeded", f.Command("begin").Outcome); Assert.Equal(0, f.Backend.Enumerations);
        f.Backend.Queued.Enqueue(new byte[9]); // Stale/invalid input must be flushed, not decoded.
        await f.Sample("Primary", -40, 50); await f.Sample("Check", -30, 60);
        Assert.True(f.State.Evaluation.Ready, JsonSerializer.Serialize(f.State, Protocol.Json));
        Assert.InRange(f.State.Evaluation.OffsetDb!.Value, 89.999, 90.001);
        Assert.Equal("Succeeded", f.Command("confirm").Outcome);
        Assert.Equal("UsbReferenceBeta", f.State.Profile!.Method); Assert.True(f.State.FreshSignatureMatched);
        Assert.All(f.State.Profile.Samples, s => Assert.True(NoiseReferenceContract.MeterEvidenceValid(s.MeterEvidence)));
        Assert.Equal(2, f.Backend.Opens); Assert.Equal(2, f.Backend.Disposals); Assert.Equal(f.Backend.Reads, f.Backend.Flushes);
        Assert.All(f.Backend.Writes, r => Assert.Equal(0xB3, r[1])); Assert.Equal(0, f.Reports);
        string json = File.ReadAllText(Path.Combine(f.DirectoryPath, "noise-reference-calibration.json"));
        Assert.DoesNotContain(Secret, json); Assert.DoesNotContain("pcm", json, StringComparison.OrdinalIgnoreCase);
        await using var reload = new NoiseService(_ => throw new Exception("No startup capture"), () => [], f.Time, f.DirectoryPath, f.Management);
        var state = reload.ReferenceHandle(new(1, Guid.NewGuid(), "noise.reference.status")).NoiseReference!;
        Assert.Null(state.Error); Assert.Equal("UsbReferenceBeta", state.Profile!.Method); Assert.False(state.FreshSignatureMatched);
    }
    [Theory]
    [InlineData(0x10)] [InlineData(0x20)] [InlineData(0x40)] [InlineData(0x80)]
    public async Task IneligibleProbeDoesNotOpenMicrophone(byte flags)
    {
        await using var f = new UsbFixture(); f.Backend.Flags = flags; f.Command("begin"); await f.Start();
        await Until(() => f.State.State == "Ready"); Assert.Equal(0, f.Creates);
        Assert.NotEqual("Good", f.State.Draft!.Samples.Single().Quality); Assert.Null(f.State.Profile);
        Assert.Equal(f.Backend.Opens, f.Backend.Disposals);
    }
    [Theory]
    [InlineData("Disconnect")] [InlineData("Mode")] [InlineData("Gain")] [InlineData("Cancel")]
    [InlineData("Pause")] [InlineData("Gap")]
    public async Task MidWindowFailuresWithdrawReadingStopBothDevicesAndNeverConfirm(string reason)
    {
        await using var f = new UsbFixture(); f.Command("begin"); await f.Start(); await f.Frames(70);
        Assert.NotNull(f.State.Meter!.Reading);
        switch (reason)
        {
            case "Disconnect": f.Backend.DisconnectAt = f.Backend.Reads + 1; break;
            case "Mode": f.Backend.Flags = 0x20; break;
            case "Gain": f.Capture!.Signature = Signature + "|changed"; break;
            case "Cancel": Assert.Equal("Succeeded", f.Command("cancel").Outcome); break;
            case "Pause": f.Paused = true; break;
            case "Gap": f.Time.Advance(3); break;
        }
        await f.Frames(20); await Until(() => f.Capture!.Disposed && f.Backend.Disposals == f.Backend.Opens);
        Assert.Null(f.State.Meter); Assert.False(f.State.Evaluation.Ready); Assert.Null(f.State.Profile);
        Assert.NotEqual("Good", f.State.Draft!.Samples.Single().Quality); Assert.Equal(0, f.Reports);
    }
    [Fact]
    public async Task SharedLeaseDeniesDiagnosticReadAndManualOverrideWhileSampling()
    {
        await using var f = new UsbFixture(); f.Command("begin"); await f.Start();
        var diagnostic = await f.Meter.HandleAsync(new(1, Guid.NewGuid(), "noise.meter.command", SoundMeter: new("read", Secret, "synthetic-usb")), default);
        Assert.Equal("METER_BUSY", diagnostic.ErrorCode); Assert.Equal(1, f.Backend.Opens);
        f.Command("cancel"); await Until(() => f.State.State == "Ready");
        Assert.Equal("REFERENCE_USB_AUTOMATIC", f.Command("reference", sample: f.State.Draft!.Samples.Single().Id).ErrorCode);
    }
    [Fact]
    public async Task FailedSecondLevelCannotSaveEvenThoughCommunicationIsHealthy()
    {
        await using var f = new UsbFixture(); f.Command("begin");
        await f.Sample("Primary", -40, 50); await f.Sample("Check", -34, 60);
        Assert.False(f.State.Evaluation.Ready); Assert.Equal("REFERENCE_CHECK_FAILED", f.Command("confirm").ErrorCode);
        Assert.Null(f.State.Profile);
    }
    [Fact]
    public async Task UsbProfileFeedsFormalAcousticThresholdsReportsAndPersistentInputInvalidation()
    {
        await using var f = new UsbFixture(); f.Command("begin");
        await f.Sample("Primary", -40, 50); await f.Sample("Check", -30, 60); f.Command("confirm");
        var transport = new NoiseTransport(f.Noise, f.DirectoryPath); transport.Bind("synthetic-binding");
        Assert.Equal("Accepted", f.Noise.StartScheduled(Guid.NewGuid()).Outcome);
        await Until(() => f.Noise.Snapshot().State == "Active");
        var session = f.Noise.Snapshot().SessionId;
        var policy = new StudyNoise(true, -90, -89, 600, 5, new(55, 45)); f.Noise.StudyThresholds(session, policy);
        for (int i = 0; i < 60; i++) { f.Time.Advance(.1); f.Capture!.EmitNormal(); f.Capture.Emit(-30, true); }
        var active = f.Noise.Snapshot(); Assert.Equal("UsbReferenceBeta", active.Estimate!.Method);
        Assert.Equal(60, active.Estimate.CurrentDbA!.Value, 5); Assert.Equal(-30, active.CurrentDbfs!.Value, 5);
        Assert.Equal(("High", true), f.Noise.StudyThresholds(session, policy)); NpepNoiseProtocol.Validate("status", transport.Observe());
        f.Capture!.Signature = Signature + "|gain-changed"; f.Time.Advance(.1); f.Capture.EmitNormal(); f.Capture.Emit(-30, true);
        Assert.Equal("INPUT_SETTINGS_CHANGED", f.Noise.Snapshot().Estimate!.Reason);
        Assert.Null(f.Noise.Snapshot().Estimate!.CurrentDbA); Assert.Equal(("None", false), f.Noise.StudyThresholds(session, policy));
        f.Noise.StopScheduled(session!.Value); await Until(() => !f.Noise.CaptureBusy);
        var report = transport.Reports().Single()!.AsObject(); NpepNoiseProtocol.Validate("report", report);
        Assert.Equal("UsbReferenceBeta", report["estimate"]!["method"]!.GetValue<string>());
        Assert.Equal(60, report["estimate"]!["summary"]!["energyMeanDbA"]!.GetValue<double>(), 5);
        Assert.DoesNotContain("synthetic-usb", report.ToJsonString());
        await using var reload = new NoiseService(_ => throw new Exception("No startup capture"), () => [], f.Time, f.DirectoryPath, f.Management);
        var saved = reload.ReferenceHandle(new(1, Guid.NewGuid(), "noise.reference.status")).NoiseReference!;
        Assert.Equal("INPUT_SETTINGS_CHANGED", saved.InvalidationReason); Assert.NotNull(saved.Profile);
    }
    [Fact]
    public async Task CorruptUsbProfileEvidenceAndNullSamplesRemainInactiveWithoutRewritingOriginal()
    {
        await using var f = new UsbFixture(); f.Command("begin");
        await f.Sample("Primary", -40, 50); await f.Sample("Check", -30, 60); f.Command("confirm");
        string path = Path.Combine(f.DirectoryPath, "noise-reference-calibration.json");
        string original = File.ReadAllText(path);
        foreach (string change in new[] { "NullSample", "MissingEvidence", "WrongTiming" })
        {
            var file = JsonNode.Parse(original)!;
            var points = file["profile"]!["samples"]!.AsArray();
            if (change == "NullSample") points[0] = null;
            else if (change == "MissingEvidence") points[0]!["meterEvidence"] = null;
            else points[0]!["meterEvidence"]!["timing"] = "unverified-device-clock";
            string invalid = file.ToJsonString(); File.WriteAllText(path, invalid);
            await using var reloaded = new NoiseService(_ => throw new Exception("No startup capture"), () => [], f.Time, f.DirectoryPath, f.Management);
            var state = reloaded.ReferenceHandle(new(1, Guid.NewGuid(), "noise.reference.status")).NoiseReference!;
            Assert.Equal("REFERENCE_STORE_UNAVAILABLE", state.Error); Assert.Null(state.Profile);
            Assert.Equal(invalid, File.ReadAllText(path));
        }
    }
    [Fact]
    public async Task MissingMeterAndDeniedAdministratorNeverOpenAnotherDevice()
    {
        await using var f = new UsbFixture();
        Assert.Equal("MANAGEMENT_SECRET_INVALID", f.Command("begin", secret: "wrong-admin-2026").ErrorCode);
        Assert.Equal(0, f.Backend.Enumerations); f.Command("begin"); f.Backend.Missing = true;
        await f.Start(); await Until(() => f.State.State == "Ready");
        Assert.Equal(0, f.Backend.Opens); Assert.Equal(0, f.Creates); Assert.Null(f.State.Profile);
    }
    [Fact]
    public async Task HostShutdownCancelsBlockedUsbReadAndReleasesLeaseWithoutLateSample()
    {
        await using var f = new UsbFixture(); f.Command("begin"); f.Backend.BlockRead = true;
        Assert.Equal("Accepted", f.Command("start", "Primary").Outcome);
        await f.Backend.ReadingBlocked.Task.WaitAsync(TimeSpan.FromSeconds(3));
        await f.Noise.DisposeAsync(); f.NoiseDisposed = true; Assert.Equal(0, f.Creates); Assert.Equal(1, f.Backend.Disposals);
        Assert.NotEqual("Good", f.State.Draft!.Samples.Single().Quality); Assert.Equal(0, f.Reports);
    }
}

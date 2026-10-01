using System.Buffers.Binary;
using NPEduTools.Contracts;
using NPEduTools.Core;
using NPEduTools.Host;

namespace NPEduTools.Tests;

public sealed class NoiseTests
{
    [Theory]
    [InlineData(16, false)]
    [InlineData(24, false)]
    [InlineData(32, false)]
    [InlineData(32, true)]
    public void Pcm_formats_and_opposite_channels_preserve_energy(int bits, bool floating)
    {
        var frames = new List<NoiseFrame>();
        var meter = new NoiseMeter(48000, 2, bits, floating, frames.Add);
        var pcm = Samples(9600, bits, floating, i => i % 2 == 0 ? 0.5 : -0.5);
        // Split a window across callbacks.
        meter.Push(pcm.AsSpan(0, pcm.Length / 2)); Assert.Empty(frames);
        meter.Push(pcm.AsSpan(pcm.Length / 2));
        var frame = Assert.Single(frames);
        Assert.Equal(0.1, frame.Seconds, 8); Assert.Equal(0.25, frame.MeanSquare, 8);
        Assert.Equal(-6.0206, NoiseMeter.Dbfs(Math.Sqrt(frame.MeanSquare))!.Value, 4);
        Assert.Equal(0.5, frame.Peak); Assert.False(frame.Invalid);
    }

    [Theory]
    [InlineData(8000)]
    [InlineData(44100)]
    [InlineData(96000)]
    public void Sample_rates_and_final_partial_window_are_measured(int rate)
    {
        var frames = new List<NoiseFrame>();
        var meter = new NoiseMeter(rate, 1, 32, true, frames.Add);
        meter.Push(Samples(rate / 20, 32, true, _ => 0)); meter.Flush();
        var frame = Assert.Single(frames);
        Assert.Equal(0.05, frame.Seconds, 5); Assert.Null(NoiseMeter.Dbfs(Math.Sqrt(frame.MeanSquare)));
        Assert.False(frame.Invalid);
    }

    [Fact]
    public void Nonfinite_pcm_is_invalid_and_full_scale_reports_clipping()
    {
        var frames = new List<NoiseFrame>();
        var meter = new NoiseMeter(8000, 1, 32, true, frames.Add);
        meter.Push(Samples(800, 32, true, i => i == 0 ? double.NaN : 1));
        Assert.True(frames[0].Invalid); Assert.True(frames[0].ClippedRatio > 0.99);
        meter.Push(Samples(800, 32, true, _ => 0.5));
        Assert.False(frames[1].Invalid); Assert.Equal(0, frames[1].ClippedRatio);
        Assert.Throws<ArgumentException>(() => meter.Push(new byte[3]));
        Assert.Throws<ArgumentException>(() => new NoiseMeter(48000, 2, 8, false, _ => { }));
    }

    [Fact]
    public async Task Start_stop_summary_and_stale_commands_are_fenced()
    {
        var clock = new TestClock(); var source = new FakeCapture();
        await using var service = new NoiseService(_ => source, () => [new("mic", "Test microphone")], clock);
        var initial = service.Snapshot();
        var start = Command(initial, "start", "mic");
        Assert.Equal("Accepted", service.Handle(start).Outcome);
        Assert.Equal("NoiseStateChanged", service.Handle(start).ErrorCode);
        await Until(() => service.Snapshot().State == "Active");
        clock.Advance(1); source.Emit(new(0.1, 0.25, 0.5, 0, false));
        source.Emit(new(0.1, 0, 0, 0, false));
        source.Emit(new(0.1, double.NaN, 0, 0, true));
        var active = service.Snapshot();
        Assert.Equal("Invalid", active.Quality); Assert.Null(active.CurrentDbfs);
        Assert.Equal(0.2, active.Summary!.Coverage, 6);
        Assert.Equal(-9.0309, active.Summary.EnergyMeanDbfs!.Value, 4); // Energy, not arithmetic dB mean.
        Assert.Equal("Accepted", service.Handle(Command(active, "stop")).Outcome);
        await Until(() => service.Snapshot().State == "Stopped");
        Assert.True(source.Disposed);
        var stopped = service.Snapshot(); clock.Advance(30);
        source.Emit(new(0.1, 1, 1, 1, false));
        Assert.Equal(stopped.Summary, service.Snapshot().Summary);
        Assert.Equal("NoiseStateChanged", service.Handle(Command(initial, "start", "mic")).ErrorCode);
    }

    [Fact]
    public async Task Stop_during_open_waits_for_cleanup_before_allowing_new_capture()
    {
        using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
        var source = new FakeCapture { OnStart = () => { entered.Set(); release.Wait(TimeSpan.FromSeconds(3)); } };
        await using var service = new NoiseService(_ => source, () => []);
        service.Handle(Command(service.Snapshot(), "start", "mic"));
        Assert.True(entered.Wait(TimeSpan.FromSeconds(2)));
        try
        {
            service.Handle(Command(service.Snapshot(), "stop"));
            Assert.Equal("Stopping", service.Snapshot().State);
            Assert.Equal("NoiseBusy", service.Handle(Command(service.Snapshot(), "start", "other")).ErrorCode);
        }
        finally { release.Set(); }
        await Until(() => service.Snapshot().State == "Stopped"); Assert.True(source.Disposed);
    }

    [Fact]
    public async Task Startup_timeout_does_not_claim_device_was_released()
    {
        var clock = new TestClock();
        using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
        var source = new FakeCapture { OnStart = () => { entered.Set(); release.Wait(TimeSpan.FromSeconds(3)); } };
        await using var service = new NoiseService(_ => source, () => [], clock);
        service.Handle(Command(service.Snapshot(), "start", "mic"));
        Assert.True(entered.Wait(TimeSpan.FromSeconds(2)));
        try
        {
            clock.Advance(6); await Until(() => service.Snapshot().State == "Stopping");
            Assert.Contains("启动超时", service.Snapshot().Message); Assert.False(source.Disposed);
            Assert.Equal("NoiseBusy", service.Handle(Command(service.Snapshot(), "start", "other")).ErrorCode);
        }
        finally { release.Set(); }
        await Until(() => service.Snapshot().State == "Faulted"); Assert.True(source.Disposed);
    }

    [Fact]
    public async Task Missing_device_can_retry_and_old_callbacks_cannot_pollute_new_session()
    {
        var first = new FakeCapture(); var second = new FakeCapture(); int created = 0;
        await using var service = new NoiseService(_ => ++created switch
        { 1 => throw new IOException("missing"), 2 => first, _ => second }, () => []);
        service.Handle(Command(service.Snapshot(), "start", "mic"));
        await Until(() => service.Snapshot().State == "Faulted");
        await Task.Delay(20);
        service.Handle(Command(service.Snapshot(), "start", "mic"));
        await Until(() => service.Snapshot().State == "Active");
        first.Break(); await Until(() => service.Snapshot().State == "Faulted");
        await Task.Delay(20);
        service.Handle(Command(service.Snapshot(), "start", "mic"));
        await Until(() => service.Snapshot().State == "Active");
        first.Emit(new(0.1, 1, 1, 1, false)); first.Break();
        Assert.Equal(0, service.Snapshot().Summary!.Frames); Assert.Equal("Active", service.Snapshot().State);
        second.Emit(new(0.1, 0, 0, 0, false));
        Assert.Equal("DigitalSilence", service.Snapshot().Quality); Assert.Null(service.Snapshot().CurrentDbfs);
    }

    [Fact]
    public async Task Data_gap_is_unknown_then_stops_capture_and_trend_is_bounded()
    {
        var clock = new TestClock(); var source = new FakeCapture();
        await using var service = new NoiseService(_ => source, () => [], clock);
        service.Handle(Command(service.Snapshot(), "start", "mic"));
        await Until(() => service.Snapshot().State == "Active");
        for (int i = 0; i < 300; i++) { clock.Advance(0.5); source.Emit(new(0.1, 0.1, 0.4, 0, false)); }
        Assert.Equal(120, service.Snapshot().Trend.Count);
        clock.Advance(2); Assert.Null(service.Snapshot().CurrentDbfs); Assert.Equal("NoData", service.Snapshot().Quality);
        clock.Advance(2); await Until(() => service.Snapshot().State == "Faulted"); Assert.True(source.Disposed);
        Assert.True(service.Snapshot().Summary!.Coverage < 0.3);
    }

    [Fact]
    public async Task Failed_cleanup_blocks_second_capture_until_host_restart()
    {
        var source = new FakeCapture { OnDispose = () => throw new IOException("driver cleanup failed") };
        await using var service = new NoiseService(_ => source, () => []);
        service.Handle(Command(service.Snapshot(), "start", "mic"));
        await Until(() => service.Snapshot().State == "Active");
        service.Handle(Command(service.Snapshot(), "stop"));
        await Until(() => service.Snapshot().State == "Faulted");
        Assert.Equal("NoiseCleanupRequired", service.Handle(Command(service.Snapshot(), "start", "mic")).ErrorCode);
    }

    [Fact]
    public async Task Enumeration_does_not_capture_and_shutdown_releases_microphone()
    {
        var source = new FakeCapture(); int created = 0;
        var service = new NoiseService(_ => { created++; return source; }, () => [new("mic", "One")]);
        Assert.Single(service.Handle(new(Protocol.Version, Guid.NewGuid(), "noise.devices")).NoiseDevices!);
        Assert.Equal(0, created);
        service.Handle(Command(service.Snapshot(), "start", "mic"));
        await Until(() => service.Snapshot().State == "Active");
        await service.DisposeAsync(); Assert.True(source.Disposed);
        Assert.Equal("NoiseStopping", service.Handle(Command(service.Snapshot(), "start", "mic")).ErrorCode);
    }

    [Fact]
    public void Noise_contract_rejects_wrong_action_fields_and_cross_capability_payloads()
    {
        var command = new NoiseCommand("start", Guid.NewGuid(), 0, "mic");
        var request = new HostRequest(Protocol.Version, Guid.NewGuid(), "noise.command", Noise: command);
        Assert.Null(Protocol.Validate(request));
        Assert.NotNull(Protocol.Validate(request with { Noise = command with { DeviceId = "" } }));
        Assert.NotNull(Protocol.Validate(request with { Noise = command with { Action = "stop" } }));
        Assert.NotNull(Protocol.Validate(request with { Capability = "host.ping" }));
        Assert.NotNull(Protocol.Validate(request with { ObserveMs = 100 }));
        Assert.NotNull(Protocol.Validate(request with { Noise = command with { InstanceId = Guid.Empty } }));
    }

    private static HostRequest Command(NoiseState state, string action, string? device = null) =>
        new(Protocol.Version, Guid.NewGuid(), "noise.command", Noise: new(action, state.InstanceId, state.Revision, device));
    private static async Task Until(Func<bool> condition)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(4));
        while (!condition()) await Task.Delay(10, deadline.Token);
    }
    private sealed class TestClock : TimeProvider
    {
        private long _ms;
        public override long TimestampFrequency => 1000;
        public override long GetTimestamp() => Interlocked.Read(ref _ms);
        public void Advance(double seconds) => Interlocked.Add(ref _ms, (long)(seconds * 1000));
    }
    private sealed class FakeCapture : INoiseCapture
    {
        public string DeviceName => "Test microphone";
        public bool Disposed { get; private set; }
        public Action? OnStart { get; init; }
        public Action? OnDispose { get; init; }
        public event Action<NoiseFrame>? Frame;
        public event Action<string>? Failed;
        public void Start() => OnStart?.Invoke();
        public void Emit(NoiseFrame frame) => Frame?.Invoke(frame);
        public void Break() => Failed?.Invoke("Device disconnected");
        public void Dispose() { OnDispose?.Invoke(); Disposed = true; }
    }
    private static byte[] Samples(int count, int bits, bool floating, Func<int, double> sample)
    {
        byte[] bytes = new byte[count * bits / 8];
        for (int i = 0; i < count; i++)
        {
            var span = bytes.AsSpan(i * bits / 8); double value = sample(i);
            if (floating) BinaryPrimitives.WriteInt32LittleEndian(span, BitConverter.SingleToInt32Bits((float)value));
            else if (bits == 16) BinaryPrimitives.WriteInt16LittleEndian(span, (short)(value * 32768));
            else if (bits == 32) BinaryPrimitives.WriteInt32LittleEndian(span, (int)(value * 2147483648));
            else { int integer = (int)(value * 8388608); span[0] = (byte)integer; span[1] = (byte)(integer >> 8); span[2] = (byte)(integer >> 16); }
        }
        return bytes;
    }
}

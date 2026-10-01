using NPEduTools.Core;
using NPEduTools.Host;
using NPEduTools.Contracts;

namespace NPEduTools.Tests;

public sealed class NoiseSignalPresentationTests
{
    [Theory]
    [InlineData(-160, "≤ -160.0", "输入接近静音")]
    [InlineData(-120, "-120.0", "输入接近静音")]
    [InlineData(-100, "-100.0", "输入接近静音")]
    [InlineData(-99.9, "-99.9", "采样有效")]
    [InlineData(-57, "-57.0", "采样有效")]
    public void Floor_weak_input_and_speech_have_distinct_presentation(double dbfs, string level, string quality)
    {
        Assert.Equal(level, NoiseSignalPresentation.Level(dbfs));
        Assert.Equal(quality, NoiseSignalPresentation.Quality("Good", dbfs));
        Assert.Equal(dbfs <= -100, !string.IsNullOrEmpty(NoiseSignalPresentation.Hint(dbfs)));
        Assert.Equal(dbfs <= -160, NoiseSignalPresentation.Statistic(dbfs).Contains("数值下限"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void Missing_or_invalid_values_are_not_quiet_readings(double? value)
    {
        Assert.Equal("—", NoiseSignalPresentation.Level(value));
        Assert.Equal("—", NoiseSignalPresentation.Statistic(value));
        Assert.False(NoiseSignalPresentation.IsWeak(value));
        Assert.Empty(NoiseSignalPresentation.Hint(value));
    }

    [Fact]
    public void Signal_warning_does_not_override_sampling_faults()
    {
        Assert.Equal("采样无效", NoiseSignalPresentation.Quality("Invalid", -160));
        Assert.Equal("没有新数据", NoiseSignalPresentation.Quality("NoData", -160));
        Assert.Equal("全零信号，检查静音", NoiseSignalPresentation.Quality("DigitalSilence", null));
        Assert.Equal("输入削波", NoiseSignalPresentation.Quality("Clipping", -160));
    }

    [Fact]
    public async Task Tiny_nonzero_capture_then_speech_updates_display_without_changing_sample_energy()
    {
        var capture = new SyntheticCapture();
        await using var service = new NoiseService(_ => capture, () => []);
        var initial = service.Snapshot();
        service.Handle(new(Protocol.Version, Guid.NewGuid(), "noise.command", Noise: new("start", initial.InstanceId, initial.Revision, "synthetic")));
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(4));
        while (service.Snapshot().State != "Active") await Task.Delay(10, deadline.Token);

        capture.Emit(new(0.1, 1e-34, 1e-17, 0, false));
        var quiet = service.Snapshot();
        Assert.Equal(-160, quiet.CurrentDbfs);
        Assert.Equal("输入接近静音", NoiseSignalPresentation.Quality(quiet.Quality, quiet.CurrentDbfs));
        Assert.Contains("数值下限", NoiseSignalPresentation.Hint(quiet.CurrentDbfs));

        double speechEnergy = Math.Pow(10, -57d / 10);
        capture.Emit(new(0.1, speechEnergy, Math.Sqrt(speechEnergy), 0, false));
        var speech = service.Snapshot();
        Assert.Equal(-57, speech.CurrentDbfs!.Value, 6);
        Assert.Equal("采样有效", NoiseSignalPresentation.Quality(speech.Quality, speech.CurrentDbfs));
        Assert.Empty(NoiseSignalPresentation.Hint(speech.CurrentDbfs));
        Assert.Equal(NoiseMeter.Dbfs(Math.Sqrt((1e-34 + speechEnergy) / 2))!.Value, speech.Summary!.EnergyMeanDbfs!.Value, 8);
        Assert.Equal(0.2, speech.Summary.SampledSeconds, 8);

        capture.Emit(new(0.1, 0, 0, 0, false));
        Assert.Null(service.Snapshot().CurrentDbfs);
        Assert.Equal("DigitalSilence", service.Snapshot().Quality);
    }

    private sealed class SyntheticCapture : INoiseCapture
    {
        public string DeviceName => "Synthetic only";
        public event Action<NoiseFrame>? Frame;
        public event Action<string>? Failed { add { } remove { } }
        public void Emit(NoiseFrame frame) => Frame?.Invoke(frame);
        public void Start() { }
        public void Dispose() { }
    }
}

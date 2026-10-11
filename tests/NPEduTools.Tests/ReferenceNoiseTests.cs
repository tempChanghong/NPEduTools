using System.Buffers.Binary;
using NPEduTools.Core;

namespace NPEduTools.Tests;

public sealed class ReferenceNoiseTests
{
    [Theory]
    [InlineData(44100)]
    [InlineData(48000)]
    [InlineData(50001)]
    [InlineData(88200)]
    [InlineData(96000)]
    [InlineData(176400)]
    [InlineData(192000)]
    [InlineData(384000)]
    public void ReferenceBandMatchesIndependentAnalogCurve(int rate)
    {
        var filter = new AWeightingFilter(rate, 1);
        Assert.InRange(Math.Abs(filter.ResponseDb(1000)), 0, 1e-10);
        // Independent analog magnitude, not the implementation's digital transfer expression.
        for (int i = 0; i <= 1000; i++)
        {
            double frequency = 31.5 * Math.Pow(8500 / 31.5, i / 1000d);
            double error = Math.Abs(filter.ResponseDb(frequency) - AnalogDb(frequency));
            Assert.True(error <= 0.35, $"Fs={rate}, f={frequency:F3}, error={error:F6} dB");
        }
    }

    [Theory]
    [InlineData(31.5, -39.4)]
    [InlineData(63, -26.2)]
    [InlineData(125, -16.1)]
    [InlineData(250, -8.6)]
    [InlineData(500, -3.2)]
    [InlineData(1000, 0)]
    [InlineData(2000, 1.2)]
    [InlineData(4000, 1.0)]
    [InlineData(8000, -1.1)]
    public void NominalCurveAgreesWithRoundedReferencePoints(double frequency, double expected)
        // Rounded nominal band centers and table values are a coarse sanity check only.
        // The dense test above compares the exact analog curve at the exact frequency.
        => Assert.InRange(Math.Abs(AnalogDb(frequency) - expected), 0, 0.15);

    [Theory]
    [InlineData(44100, 31.5)]
    [InlineData(44100, 1000)]
    [InlineData(44100, 8500)]
    [InlineData(48000, 63)]
    [InlineData(48000, 1000)]
    [InlineData(48000, 8000)]
    [InlineData(96000, 125)]
    [InlineData(96000, 4000)]
    [InlineData(192000, 1000)]
    [InlineData(384000, 8500)]
    public void StreamingSineAgreesWithTransferResponse(int rate, double frequency)
    {
        var filter = new AWeightingFilter(rate, 1);
        int warmup = rate;
        // An integer number of sine cycles avoids a measurement-window phase bias.
        int cycles = (int)Math.Ceiling(frequency);
        int measured = (int)Math.Round((double)cycles * rate / frequency);
        double inputEnergy = 0, outputEnergy = 0;
        for (int i = 0; i < warmup + measured; i++)
        {
            double input = 0.25 * Math.Sin(2 * Math.PI * frequency * i / rate);
            double output = filter.Process(input, 0);
            if (i >= warmup) { inputEnergy += input * input; outputEnergy += output * output; }
        }
        double actual = 10 * Math.Log10(outputEnergy / inputEnergy);
        Assert.InRange(Math.Abs(actual - filter.ResponseDb(frequency)), 0, 0.005);
    }

    [Theory]
    [InlineData(8000, SoundMeterTimeWeighting.Fast)]
    [InlineData(44100, SoundMeterTimeWeighting.Fast)]
    [InlineData(48000, SoundMeterTimeWeighting.Fast)]
    [InlineData(192000, SoundMeterTimeWeighting.Fast)]
    [InlineData(384000, SoundMeterTimeWeighting.Fast)]
    [InlineData(8000, SoundMeterTimeWeighting.Slow)]
    [InlineData(44100, SoundMeterTimeWeighting.Slow)]
    [InlineData(48000, SoundMeterTimeWeighting.Slow)]
    [InlineData(192000, SoundMeterTimeWeighting.Slow)]
    [InlineData(384000, SoundMeterTimeWeighting.Slow)]
    public void TimeWeightingMatchesAnalyticRiseDecayAndLevelChange(int rate, SoundMeterTimeWeighting mode)
    {
        var weight = new SoundEnergyTimeWeighting(rate, mode);
        double tau = mode == SoundMeterTimeWeighting.Fast ? 0.125 : 1;
        for (int i = 0; i < rate; i++) weight.Process(0.25);
        double expected = 0.25 * (1 - Math.Exp(-1 / tau));
        Assert.InRange(Math.Abs(weight.MeanSquare - expected), 0, 1e-10);
        for (int i = 0; i < rate; i++) weight.Process(0);
        expected *= Math.Exp(-1 / tau);
        Assert.InRange(Math.Abs(weight.MeanSquare - expected), 0, 1e-10);
        for (int i = 0; i < rate; i++) weight.Process(0.01);
        expected = 0.01 + (expected - 0.01) * Math.Exp(-1 / tau);
        Assert.InRange(Math.Abs(weight.MeanSquare - expected), 0, 1e-10);
        weight.Reset();
        Assert.Equal(0, weight.MeanSquare);
    }

    [Theory]
    [InlineData(16, false)]
    [InlineData(24, false)]
    [InlineData(32, false)]
    [InlineData(32, true)]
    public void PcmFormatsProduceNormalizedOneKhzEnergy(int bits, bool floating)
    {
        var frames = Measure(Sine(48000, 1, bits, floating, 6), 48000, 1, bits, floating);
        Assert.Equal(60, frames.Count);
        var last = frames[^1];
        Assert.InRange(Math.Abs(last.DigitalLevelDbfs!.Value - 10 * Math.Log10(0.125)), 0, 0.002);
        Assert.InRange(Math.Abs(last.SlowDigitalLevelDbfs!.Value - last.DigitalLevelDbfs.Value), 0, 0.025);
        Assert.InRange(Math.Abs(last.FastDigitalLevelDbfs!.Value - last.DigitalLevelDbfs.Value), 0, 0.01);
        Assert.True(last.Settled);
        Assert.False(frames[49].Settled);
        Assert.True(frames[50].Settled);
        Assert.All(frames, f => { Assert.False(f.Invalid); Assert.Equal(0, f.ClippedRatio); });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AntiPhaseChannelsDoNotCancelAndSilentChannelHalvesEnergy(bool opposite)
    {
        var mono = Measure(Sine(48000, 1, 32, true, 1), 48000, 1, 32, true)[^1];
        var stereo = Measure(Sine(48000, 2, 32, true, 1, opposite ? -1 : 0), 48000, 2, 32, true)[^1];
        Assert.InRange(Math.Abs(stereo.MeanSquare / mono.MeanSquare - (opposite ? 1 : 0.5)), 0, 1e-10);
    }

    [Fact]
    public void ChannelsHaveIndependentFilterMemory()
    {
        var filter = new AWeightingFilter(48000, 32);
        for (int i = 0; i < 10000; i++)
        {
            filter.Process(i == 0 ? 1 : 0, 0);
            for (int c = 1; c < 32; c++) Assert.Equal(0, filter.Process(0, c));
        }
        filter.Reset();
        Assert.Equal(0, filter.Process(0, 0));
    }

    [Fact]
    public void PacketBoundariesDoNotChangeAnyStatistics()
    {
        byte[] pcm = Sine(50001, 2, 24, false, 1.031, -1);
        var whole = Measure(pcm, 50001, 2, 24, false);
        var chunks = new List<ReferenceNoiseFrame>();
        var meter = new ReferenceNoiseMeter(50001, 2, 24, false, chunks.Add);
        for (int offset = 0; offset < pcm.Length;)
        {
            int length = Math.Min(6 * 137, pcm.Length - offset);
            meter.Push(pcm.AsSpan(offset, length)); offset += length;
        }
        meter.Flush();
        Assert.Equal(whole, chunks);
        Assert.InRange(chunks[^1].Seconds, 0.0309, 0.0311);
        Assert.InRange(Math.Abs(chunks.Sum(f => f.Seconds) - Math.Round(50001 * 1.031) / 50001d), 0, 1e-12);
    }

    [Fact]
    public void ResetDiscardsPartialWindowEnvelopesAndWarmup()
    {
        var frames = new List<ReferenceNoiseFrame>();
        var meter = new ReferenceNoiseMeter(48000, 1, 32, true, frames.Add);
        meter.Push(Sine(48000, 1, 32, true, 6.05));
        Assert.True(frames[^1].Settled);
        meter.Reset(); frames.Clear();
        meter.Push(new byte[4800 * 4]); meter.Flush(); meter.Flush();
        Assert.Single(frames);
        Assert.Equal(0, frames[0].MeanSquare);
        Assert.Equal(0, frames[0].FastMeanSquare);
        Assert.Equal(0, frames[0].SlowMeanSquare);
        Assert.Null(frames[0].DigitalLevelDbfs);
        Assert.False(frames[0].Settled);
    }

    [Theory]
    [InlineData(float.NaN)]
    [InlineData(float.PositiveInfinity)]
    [InlineData(float.NegativeInfinity)]
    [InlineData(1.5f)]
    [InlineData(-1.5f)]
    public void BadSamplesInvalidateWindowResetSignalAndRequireFreshWarmup(float bad)
    {
        var frames = new List<ReferenceNoiseFrame>();
        var meter = new ReferenceNoiseMeter(48000, 2, 32, true, frames.Add);
        meter.Push(Sine(48000, 2, 32, true, 5.1)); frames.Clear();
        var packet = new byte[4800 * 8];
        BinaryPrimitives.WriteSingleLittleEndian(packet, bad);
        meter.Push(packet);
        Assert.True(frames[0].Invalid);
        Assert.False(frames[0].Settled);
        Assert.Null(frames[0].DigitalLevelDbfs);
        Assert.Null(frames[0].FastDigitalLevelDbfs);
        Assert.Null(frames[0].SlowDigitalLevelDbfs);
        Assert.Equal(0, frames[0].MeanSquare);
        meter.Push(new byte[48000 * 6 * 8]);
        Assert.All(frames.Skip(1), f => { Assert.False(f.Invalid); Assert.Equal(0, f.MeanSquare); });
        Assert.False(frames[50].Settled);
        Assert.True(frames[51].Settled);
    }

    [Fact]
    public void ClippingIsRawAndDividedByAllChannelSamples()
    {
        var pcm = new byte[4800 * 2 * 4];
        for (int i = 0; i < 4800; i++) BinaryPrimitives.WriteSingleLittleEndian(pcm.AsSpan(i * 8), 1);
        var frame = Assert.Single(Measure(pcm, 48000, 2, 32, true));
        Assert.Equal(0.5, frame.ClippedRatio);
        Assert.Equal(1, frame.RawPeak);
        Assert.False(frame.Invalid);
    }

    [Fact]
    public void DcSettlesToSilenceInsteadOfRetainingLevel()
    {
        byte[] pcm = new byte[48000 * 6 * 4];
        for (int i = 0; i < pcm.Length; i += 4) BinaryPrimitives.WriteSingleLittleEndian(pcm.AsSpan(i), 0.5f);
        var last = Measure(pcm, 48000, 1, 32, true)[^1];
        Assert.InRange(last.MeanSquare, 0, 1e-25);
        Assert.True(last.Settled);
    }

    [Fact]
    public void MalformedPacketDoesNotConsumeAnySamples()
    {
        var frames = new List<ReferenceNoiseFrame>();
        var meter = new ReferenceNoiseMeter(48000, 2, 24, false, frames.Add);
        Assert.Throws<ArgumentException>(() => meter.Push(new byte[7]));
        meter.Flush(); Assert.Empty(frames);
        meter.Push(new byte[6]); meter.Flush();
        Assert.Equal(1 / 48000d, Assert.Single(frames).Seconds);
    }

    [Fact]
    public void HotPathAllocationsAreBoundedByStatisticsNotSampleCount()
    {
        foreach (int rate in new[] { 48000, 384000 })
        {
            var packet = Sine(rate, 2, 32, true, 0.1);
            int count = 0;
            var meter = new ReferenceNoiseMeter(rate, 2, 32, true, _ => count++);
            for (int i = 0; i < 100; i++) meter.Push(packet);
            // Measure steady state after warm-up. A one-off runtime allocation must not
            // masquerade as per-sample work; repeated sample allocations fail every batch.
            long steadyAllocated = long.MaxValue;
            for (int batch = 0; batch < 3; batch++)
            {
                long before = GC.GetAllocatedBytesForCurrentThread();
                for (int i = 0; i < 100; i++) meter.Push(packet);
                steadyAllocated = Math.Min(steadyAllocated, GC.GetAllocatedBytesForCurrentThread() - before);
            }
            Assert.Equal(400, count);
            Assert.InRange(steadyAllocated, 0, 100 * 128);
        }
    }

    [Fact]
    public void RejectsUnsupportedReferenceFormatsAndInvalidPublicInputs()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new AWeightingFilter(16000, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new AWeightingFilter(384001, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new AWeightingFilter(48000, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new AWeightingFilter(48000, 33));
        Assert.Throws<ArgumentException>(() => new ReferenceNoiseMeter(48000, 1, 16, true, _ => { }));
        Assert.Throws<ArgumentException>(() => new ReferenceNoiseMeter(48000, 1, 8, false, _ => { }));
        Assert.Throws<ArgumentNullException>(() => new ReferenceNoiseMeter(48000, 1, 32, true, null!));
        var filter = new AWeightingFilter(48000, 1);
        Assert.Throws<ArgumentOutOfRangeException>(() => filter.Process(double.NaN, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => filter.Process(0, 1));
        foreach (double frequency in new[] { 0d, -1, 24000, double.NaN, double.PositiveInfinity })
            Assert.Throws<ArgumentOutOfRangeException>(() => filter.ResponseDb(frequency));
        Assert.Throws<ArgumentOutOfRangeException>(() => new SoundEnergyTimeWeighting(7999, SoundMeterTimeWeighting.Slow));
        Assert.Throws<ArgumentOutOfRangeException>(() => new SoundEnergyTimeWeighting(48000, (SoundMeterTimeWeighting)99));
        var weight = new SoundEnergyTimeWeighting(48000, SoundMeterTimeWeighting.Slow);
        foreach (double energy in new[] { -1d, double.NaN, double.PositiveInfinity })
            Assert.Throws<ArgumentOutOfRangeException>(() => weight.Process(energy));
    }

    private static double AnalogDb(double frequency)
    {
        static double Magnitude(double f)
        {
            double squared = f * f;
            return Math.Pow(12194.217, 2) * squared * squared /
                ((squared + Math.Pow(20.598997, 2)) *
                 Math.Sqrt((squared + Math.Pow(107.65265, 2)) * (squared + Math.Pow(737.86223, 2))) *
                 (squared + Math.Pow(12194.217, 2)));
        }
        return 20 * Math.Log10(Magnitude(frequency) / Magnitude(1000));
    }

    private static List<ReferenceNoiseFrame> Measure(byte[] pcm, int rate, int channels, int bits, bool floating)
    {
        var frames = new List<ReferenceNoiseFrame>();
        var meter = new ReferenceNoiseMeter(rate, channels, bits, floating, frames.Add);
        meter.Push(pcm); meter.Flush();
        return frames;
    }

    private static byte[] Sine(int rate, int channels, int bits, bool floating, double seconds, double secondGain = 1)
    {
        int width = bits / 8;
        var bytes = new byte[(int)Math.Round(rate * seconds) * channels * width];
        for (int i = 0; i < bytes.Length / (channels * width); i++)
            for (int c = 0; c < channels; c++)
            {
                double value = 0.5 * Math.Sin(2 * Math.PI * 1000 * i / rate) * (c == 0 ? 1 : secondGain);
                var target = bytes.AsSpan((i * channels + c) * width);
                if (floating) BinaryPrimitives.WriteSingleLittleEndian(target, (float)value);
                else if (width == 2) BinaryPrimitives.WriteInt16LittleEndian(target, (short)Math.Round(value * 32768));
                else if (width == 4) BinaryPrimitives.WriteInt32LittleEndian(target, (int)Math.Round(value * 2147483648));
                else
                {
                    int signed = (int)Math.Round(value * 8388608);
                    target[0] = (byte)signed; target[1] = (byte)(signed >> 8); target[2] = (byte)(signed >> 16);
                }
            }
        return bytes;
    }
}

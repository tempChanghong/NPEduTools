using System.Numerics;

namespace NPEduTools.Core;

/// <summary>
/// Streaming digital approximation of the nominal A curve; no acoustic calibration gain.
/// Reference band: 31.5–8500 Hz. Three bilinear second-order sections with independent channel
/// state, 1 kHz normalization and an upper-band pole adjustment. This is not IEC certification.
/// </summary>
public sealed class AWeightingFilter
{
    public const string AlgorithmVersion = "a-sos-band8500-v1";
    public const double ReferenceBandMinimum = 31.5;
    public const double ReferenceBandMaximum = 8500;
    public const int MinimumSampleRate = 44100;
    public const int MaximumSampleRate = 384000;
    private readonly Section[] _sections;
    private readonly State[] _states;
    private readonly double _gain;
    public int SampleRate { get; }
    public int Channels { get; }

    public AWeightingFilter(int sampleRate, int channels)
    {
        if (sampleRate is < MinimumSampleRate or > MaximumSampleRate)
            throw new ArgumentOutOfRangeException(nameof(sampleRate), "Reference processing requires 44.1–384 kHz.");
        if (channels is < 1 or > 32) throw new ArgumentOutOfRangeException(nameof(channels));
        SampleRate = sampleRate; Channels = channels;
        // Nominal analog pole frequencies from ANSI S1.42-2001 Table 1, in Hz.
        double w1 = 2 * Math.PI * 20.598997, w2 = 2 * Math.PI * 107.65265;
        double w3 = 2 * Math.PI * 737.86223;
        double k = 2.0 * sampleRate;
        // Ordinary bilinear warping increasingly attenuates the upper band at 44.1/48 kHz.
        // Match the double upper pole's attenuation at 8.5 kHz, then normalize the total response
        // at 1 kHz. Lower analog poles are unchanged. Error bounds are tested, not inferred from
        // instrument classes. Above this band the approximation is not promised to match analog A.
        double warp = sampleRate / Math.PI * Math.Tan(Math.PI * ReferenceBandMaximum / sampleRate)
            / ReferenceBandMaximum;
        double w4 = 2 * Math.PI * 12194.217 * warp;
        _sections = [Design(k, w1, w1, false), Design(k, w2, w3, false), Design(k, w4, w4, true)];
        _states = new State[channels * _sections.Length];
        _gain = 1 / Response(1000).Magnitude;
    }

    public double Process(double sample, int channel)
    {
        if ((uint)channel >= (uint)Channels) throw new ArgumentOutOfRangeException(nameof(channel));
        if (!double.IsFinite(sample)) throw new ArgumentOutOfRangeException(nameof(sample));
        int state = channel * 3;
        for (int i = 0; i < 3; i++)
        {
            ref var memory = ref _states[state + i];
            var coefficients = _sections[i];
            double output = coefficients.B0 * sample + memory.Z1;
            memory.Z1 = coefficients.B1 * sample - coefficients.A1 * output + memory.Z2;
            memory.Z2 = coefficients.B2 * sample - coefficients.A2 * output;
            // Well below the reporting floor, prevents prolonged silence from leaving denormals.
            if (Math.Abs(memory.Z1) < 1e-30) memory.Z1 = 0;
            if (Math.Abs(memory.Z2) < 1e-30) memory.Z2 = 0;
            sample = output;
        }
        return sample * _gain;
    }

    public void Reset() => Array.Clear(_states);

    /// <summary>Diagnostic response only; does not read audio or modify channel state.</summary>
    public double ResponseDb(double frequency)
    {
        if (!double.IsFinite(frequency) || frequency <= 0 || frequency >= SampleRate / 2.0)
            throw new ArgumentOutOfRangeException(nameof(frequency));
        return 20 * Math.Log10(Response(frequency).Magnitude * _gain);
    }

    private Complex Response(double frequency)
    {
        var z = Complex.FromPolarCoordinates(1, -2 * Math.PI * frequency / SampleRate);
        Complex result = Complex.One;
        foreach (var s in _sections)
            result *= (s.B0 + s.B1 * z + s.B2 * z * z) / (1 + s.A1 * z + s.A2 * z * z);
        return result;
    }

    private static Section Design(double k, double firstPole, double secondPole, bool lowPass)
    {
        double denominator = (k + firstPole) * (k + secondPole);
        double b0 = (lowPass ? firstPole * secondPole : k * k) / denominator;
        return new(b0, (lowPass ? 2 : -2) * b0, b0,
            2 * (firstPole * secondPole - k * k) / denominator,
            (k - firstPole) * (k - secondPole) / denominator);
    }
    private readonly record struct Section(double B0, double B1, double B2, double A1, double A2);
    private struct State { public double Z1, Z2; }
}

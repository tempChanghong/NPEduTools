namespace NPEduTools.Core;

/// <summary>Sample-domain exponential weighting of squared amplitude, never averaging dB values.</summary>
public sealed class SoundEnergyTimeWeighting
{
    private readonly double _alpha;
    public double MeanSquare { get; private set; }
    public SoundMeterTimeWeighting Mode { get; }
    public double TimeConstantSeconds => Mode == SoundMeterTimeWeighting.Fast ? 0.125 : 1;

    public SoundEnergyTimeWeighting(int sampleRate, SoundMeterTimeWeighting mode)
    {
        if (sampleRate is < 8000 or > AWeightingFilter.MaximumSampleRate)
            throw new ArgumentOutOfRangeException(nameof(sampleRate));
        if (!Enum.IsDefined(mode)) throw new ArgumentOutOfRangeException(nameof(mode));
        Mode = mode;
        _alpha = 1 - Math.Exp(-1 / (sampleRate * TimeConstantSeconds));
    }

    public double Process(double meanSquare)
    {
        if (!double.IsFinite(meanSquare) || meanSquare < 0) throw new ArgumentOutOfRangeException(nameof(meanSquare));
        MeanSquare += _alpha * (meanSquare - MeanSquare);
        if (MeanSquare < 1e-30) MeanSquare = 0;
        return MeanSquare;
    }

    public void Reset() => MeanSquare = 0;
}

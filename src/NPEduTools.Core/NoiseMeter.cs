using System.Buffers.Binary;

namespace NPEduTools.Core;

public sealed record NoiseFrame(double Seconds, double MeanSquare, double Peak, double ClippedRatio, bool Invalid);

/// <summary>100 ms energy windows. Average channel energies, never amplitudes (anti-phase must not cancel).</summary>
public sealed class NoiseMeter
{
    public const double NumericalFloorDbfs = -160;
    private readonly int _sampleRate, _channels, _bytes, _windowSamples;
    private readonly bool _floating;
    private readonly Action<NoiseFrame> _emit;
    private int _count, _clipped;
    private double _squares, _peak;
    private bool _invalid;

    public NoiseMeter(int sampleRate, int channels, int bits, bool floating, Action<NoiseFrame> emit)
    {
        if (sampleRate is < 8000 or > 384000 || channels is < 1 or > 32 ||
            (floating ? bits != 32 : bits is not (16 or 24 or 32))) throw new ArgumentException("Unsupported PCM format.");
        _sampleRate = sampleRate; _channels = channels; _bytes = bits / 8; _floating = floating;
        _windowSamples = sampleRate / 10 * channels; _emit = emit;
    }

    public static double? Dbfs(double amplitude) => amplitude > 0 && double.IsFinite(amplitude)
        ? Math.Max(NumericalFloorDbfs, 20 * Math.Log10(amplitude)) : null;

    public void Push(ReadOnlySpan<byte> pcm)
    {
        if (pcm.Length % (_bytes * _channels) != 0) throw new ArgumentException("Incomplete PCM frame.");
        for (int offset = 0; offset < pcm.Length; offset += _bytes)
        {
            var bytes = pcm[offset..];
            double value = _floating ? BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(bytes)) : _bytes switch
            {
                2 => BinaryPrimitives.ReadInt16LittleEndian(bytes) / 32768d,
                3 => ((bytes[0] | bytes[1] << 8 | bytes[2] << 16) << 8 >> 8) / 8388608d,
                _ => BinaryPrimitives.ReadInt32LittleEndian(bytes) / 2147483648d
            };
            if (!double.IsFinite(value)) _invalid = true;
            else
            {
                _squares += value * value;
                _peak = Math.Max(_peak, Math.Abs(value));
                if (Math.Abs(value) >= 0.999) _clipped++;
            }
            if (++_count == _windowSamples) Emit();
        }
    }

    // Call only after capture has stopped. The final partial window is still real sampled audio.
    public void Flush() { if (_count > 0) Emit(); }

    private void Emit()
    {
        var frame = new NoiseFrame((double)_count / (_sampleRate * _channels), _squares / _count,
            _peak, (double)_clipped / _count, _invalid);
        _count = _clipped = 0; _squares = _peak = 0; _invalid = false;
        _emit(frame);
    }
}

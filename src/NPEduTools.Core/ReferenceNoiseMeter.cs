using System.Buffers.Binary;

namespace NPEduTools.Core;

/// <summary>
/// A-weighted DIGITAL energy only, not SPL or LAeq. F/S are instantaneous time-weighted energy
/// at the last sample of this window; MeanSquare is this window's energy average.
/// </summary>
public sealed record ReferenceNoiseFrame(double Seconds, double MeanSquare, double FastMeanSquare,
    double SlowMeanSquare, double RawPeak, double ClippedRatio, bool Invalid, bool Settled)
{
    public double? DigitalLevelDbfs => Level(MeanSquare);
    public double? FastDigitalLevelDbfs => Level(FastMeanSquare);
    public double? SlowDigitalLevelDbfs => Level(SlowMeanSquare);
    private double? Level(double energy) => Invalid ? null : NoiseMeter.Dbfs(Math.Sqrt(energy));
}

/// <summary>
/// Standalone reference-processing component. Consumes interleaved PCM from a future explicitly
/// managed capture, emits bounded statistics, retains no PCM, and does not alter NoiseMeter.
/// Single producer; callbacks must not recursively Push/Reset this instance.
/// </summary>
public sealed class ReferenceNoiseMeter
{
    public const string AlgorithmVersion = AWeightingFilter.AlgorithmVersion + "|sample-energy-fs-v1";
    public const double WarmupSeconds = 5;
    private readonly int _sampleRate, _channels, _bytes, _windowFrames;
    private readonly bool _floating;
    private readonly Action<ReferenceNoiseFrame> _emit;
    private readonly AWeightingFilter _filter;
    private readonly SoundEnergyTimeWeighting _fast, _slow;
    private int _count, _clipped;
    private long _validFrames;
    private double _squares, _peak;
    private bool _invalid, _windowSettled;

    public ReferenceNoiseMeter(int sampleRate, int channels, int bits, bool floating,
        Action<ReferenceNoiseFrame> emit)
    {
        ArgumentNullException.ThrowIfNull(emit);
        if (floating ? bits != 32 : bits is not (16 or 24 or 32))
            throw new ArgumentException("Unsupported reference PCM format.");
        _filter = new(sampleRate, channels);
        _fast = new(sampleRate, SoundMeterTimeWeighting.Fast);
        _slow = new(sampleRate, SoundMeterTimeWeighting.Slow);
        _sampleRate = sampleRate; _channels = channels; _bytes = bits / 8;
        _windowFrames = sampleRate / 10; _floating = floating; _emit = emit;
    }

    public void Push(ReadOnlySpan<byte> pcm)
    {
        int stride = _bytes * _channels;
        if (pcm.Length % stride != 0) throw new ArgumentException("Incomplete reference PCM frame.", nameof(pcm));
        for (int offset = 0; offset < pcm.Length; offset += stride)
        {
            if (_count == 0) _windowSettled = _validFrames >= _sampleRate * WarmupSeconds;
            double energy = 0;
            bool invalid = false;
            for (int channel = 0; channel < _channels; channel++)
            {
                var bytes = pcm[(offset + channel * _bytes)..];
                double value = _floating ? BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(bytes))
                    : _bytes switch
                    {
                        2 => BinaryPrimitives.ReadInt16LittleEndian(bytes) / 32768d,
                        3 => ((bytes[0] | bytes[1] << 8 | bytes[2] << 16) << 8 >> 8) / 8388608d,
                        _ => BinaryPrimitives.ReadInt32LittleEndian(bytes) / 2147483648d
                    };
                if (!double.IsFinite(value)) { invalid = true; continue; }
                double magnitude = Math.Abs(value);
                _peak = Math.Max(_peak, magnitude);
                if (magnitude >= 0.999) _clipped++;
                if (magnitude > 1) { invalid = true; continue; }
                double weighted = _filter.Process(value, channel);
                energy += weighted * weighted;
            }
            if (invalid || !double.IsFinite(energy))
            {
                _invalid = true; _windowSettled = false;
                ResetSignal(); // Non-finite samples cannot poison later windows or keep old envelopes alive.
            }
            else
            {
                double meanSquare = energy / _channels;
                _squares += meanSquare;
                _fast.Process(meanSquare); _slow.Process(meanSquare);
                _validFrames++;
            }
            if (++_count == _windowFrames) Emit();
        }
    }

    /// <summary>Call after capture stops; emits the actual duration of a final partial window.</summary>
    public void Flush() { if (_count > 0) Emit(); }

    /// <summary>Explicit session/format reset. Format itself is immutable; create a new instance if it changes.</summary>
    public void Reset()
    {
        ResetSignal();
        _count = _clipped = 0; _squares = _peak = 0; _invalid = _windowSettled = false;
    }

    private void ResetSignal() { _filter.Reset(); _fast.Reset(); _slow.Reset(); _validFrames = 0; }

    private void Emit()
    {
        var frame = new ReferenceNoiseFrame((double)_count / _sampleRate, _squares / _count,
            _fast.MeanSquare, _slow.MeanSquare, _peak, (double)_clipped / (_count * _channels),
            _invalid, _windowSettled && !_invalid);
        _count = _clipped = 0; _squares = _peak = 0; _invalid = _windowSettled = false;
        _emit(frame);
    }
}

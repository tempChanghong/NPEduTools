using NPEduTools.Contracts;

namespace NPEduTools.Core;

/// <summary>Bounded minute buckets of statistics, never PCM. Called under the capture lock.</summary>
public sealed class StudyReportAccumulator(string unit)
{
    private sealed class Bucket
    {
        public double Sampled, Energy, Maximum = double.NegativeInfinity;
    }
    private readonly SortedDictionary<int, Bucket> _buckets = [];
    private double _sampled, _energy, _maximum = double.NegativeInfinity, _classified, _mediumClassified, _medium, _high;
    public void Receive(double elapsed, double seconds, double energy, double? maximum, StudyNoise? policy)
    {
        if (!double.IsFinite(elapsed) || elapsed is < 0 or > 10801 || !double.IsFinite(seconds) || seconds is <= 0 or > 1 ||
            !double.IsFinite(energy) || energy < 0 || maximum is { } peak && !double.IsFinite(peak)) return;
        // Use frame duration/energy, not polling count or an arithmetic average of logarithms.
        int index = Math.Min(179, (int)(Math.Max(0, elapsed - seconds / 2) / 60));
        if (!_buckets.TryGetValue(index, out var b)) _buckets[index] = b = new();
        b.Sampled += seconds; b.Energy += energy * seconds; b.Maximum = Math.Max(b.Maximum, maximum ?? double.NegativeInfinity);
        _sampled += seconds; _energy += energy * seconds; _maximum = Math.Max(_maximum, maximum ?? double.NegativeInfinity);
        bool matching = unit == "dBFS" ? policy?.Estimated is null : policy?.Estimated is not null;
        if (!matching || policy?.High is not { } high || energy == 0) return;
        _classified += seconds;
        double level = Db(energy);
        if (level >= high) _high += seconds;
        if (policy.Medium is { } medium) { _mediumClassified += seconds; if (level >= medium) _medium += seconds; }
    }
    private double Db(double energy) => unit == "dBFS" ? Math.Max(NoiseMeter.NumericalFloorDbfs, 10 * Math.Log10(energy)) : 10 * Math.Log10(energy);
    public StudyReportChannel Snapshot() => new(unit, _sampled, _sampled > 0 && _energy > 0 ? Db(_energy / _sampled) : null,
        double.IsFinite(_maximum) ? _maximum : null, _classified, _medium, _high,
        _buckets.Select(p => new StudyReportPoint(p.Key * 60, p.Value.Sampled,
            p.Value.Energy > 0 ? Db(p.Value.Energy / p.Value.Sampled) : null, double.IsFinite(p.Value.Maximum) ? p.Value.Maximum : null)).ToArray(), _mediumClassified);
}

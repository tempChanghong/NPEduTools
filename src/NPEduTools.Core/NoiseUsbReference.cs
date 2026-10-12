using NPEduTools.Contracts;

namespace NPEduTools.Core;

/// <summary>
/// Bounded, same-host-clock observations. Queue flushing cannot establish the instrument's
/// internal sample time. Stable windows and timing checks make this a Beta estimate, not LAeq.
/// </summary>
public sealed class NoiseUsbReference(string mode, string workflow = "Stable")
{
    private sealed record Point(double Requested, double Received, double Level, int Range);
    private readonly List<Point> _points = new(128);
    private double? _lastReceived;
    private int? _range;
    public string? Error { get; private set; }
    public double? LastReceivedSeconds => Error is null ? _lastReceived : null;

    public bool Receive(SoundMeterReading reading, double requested, double received, bool queueDiscarded)
    {
        if (Error is not null) return false;
        if (!queueDiscarded || !double.IsFinite(requested) || !double.IsFinite(received) || requested < 0 ||
            received < requested || received - requested > NoiseReferenceContract.UsbMaximumRoundTripSeconds || _lastReceived is { } last &&
            (requested < last || received <= last || received - last > NoiseReferenceContract.UsbMaximumGapSeconds))
            Error = "USB 读数超时、间隔过大或时间顺序异常，本段不能用于参考。";
        else if (reading.Weighting != SoundMeterWeighting.A || reading.TimeWeighting.ToString() != mode ||
            reading.MaximumHold || reading.LowBattery || !NoiseReferenceContract.Reading(reading.Decibels) ||
            reading.Range is < 0 or > 4 || _range is { } range && range != reading.Range)
            Error = "仪表须为相同 A/F/S、非 MAX、无低电量；量程或模式变化后须重新采样。";
        else if (_points.Count >= 128)
            Error = "USB 采样超出本段上限。";
        if (Error is not null) return false;
        _range ??= reading.Range; _lastReceived = received;
        _points.Add(new(requested, received, reading.Decibels, reading.Range));
        return true;
    }

    public NoiseReferenceSample Attach(NoiseReferenceSample sample, DateTimeOffset sessionStarted)
    {
        if (sample.Quality != "Good" || Error is not null) return sample with { Quality = sample.Quality == "Good" ? "Interrupted" : sample.Quality };
        double start = (sample.WindowStartedAt - sessionStarted).TotalSeconds;
        double end = (sample.WindowEndedAt - sessionStarted).TotalSeconds;
        // Exclude warmup, exchanges crossing a boundary, and data received after this window.
        var points = _points.Where(p => p.Requested >= start && p.Received <= end).ToArray();
        NoiseReferenceSample Invalid()
        {
            Error = $"同期 USB 读数覆盖不足（窗口内 {points.Length} 次），请检查响应速率和时延后重采。";
            return sample with { Quality = "Incomplete" };
        }
        if (points.Length < (workflow == "Field" ? 28 : 8) || end <= start) return Invalid();
        double Centre(Point p) => (p.Requested + p.Received) / 2;
        double first = Centre(points[0]), last = Centre(points[^1]);
        double maximumGap = points.Zip(points.Skip(1), (a, b) => b.Received - a.Received).Max();
        var evidence = new NoiseReferenceMeterEvidence(points.Length, last - first, maximumGap,
            points.Max(p => p.Received - p.Requested), first - start, end - last, points[0].Range);
        if (!NoiseReferenceContract.MeterEvidenceValid(evidence,workflow)) return Invalid();
        // Only stable windows are accepted by Evaluate. Bound edges for the observed ~1 Hz query rate.
        double Energy(Point p) => Math.Pow(10, p.Level / 10);
        double sum = Energy(points[0]) * (first - start) + Energy(points[^1]) * (end - last);
        for (int i = 1; i < points.Length; i++)
            sum += (Energy(points[i - 1]) + Energy(points[i])) / 2 * (Centre(points[i]) - Centre(points[i - 1]));
        double minimum = points.Min(p => p.Level), maximum = points.Max(p => p.Level);
        // Floating-point integration of a constant level can exceed min/max by one ULP.
        double mean = Math.Clamp(10 * Math.Log10(sum / (end - start)), minimum, maximum);
        return sample with { ReferenceDbA = mean,
            ReferenceMinimumDbA = minimum, ReferenceMaximumDbA = maximum,
            SameWindowConfirmed = true, MeterEvidence = evidence };
    }
}

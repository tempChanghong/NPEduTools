using NPEduTools.Contracts;

namespace NPEduTools.Core;

/// <summary>Ten seconds of settled F/S energy statistics, never PCM or fabricated acoustic readings.</summary>
public sealed class NoiseReferenceCalibration(string role, string mode, DateTimeOffset startedAt)
{
    public const double MeasurementSeconds = 10;
    public const double MaximumCaptureSeconds = 20;
    public const double MinimumReferenceSeparationDb = 6; // Engineering check separation; not an accuracy claim.
    public const string Statistic = "mean-energy-of-100ms-time-weighted-endpoints-v1";
    private readonly List<double> _levels = new(128);
    private double _allSeconds, _sampled, _energy, _clipped, _firstWall, _lastWall, _firstSeconds;
    private bool _invalid;
    public double MeasuredSeconds => _sampled;
    public bool Complete => _sampled >= MeasurementSeconds - 1e-8;

    public void Receive(ReferenceNoiseFrame frame, double elapsed)
    {
        if (Complete) return;
        double energy = mode == "Fast" ? frame.FastMeanSquare : frame.SlowMeanSquare;
        if (frame.Invalid || !double.IsFinite(frame.Seconds) || frame.Seconds is <= 0 or > .2 ||
            !double.IsFinite(elapsed) || elapsed < 0 || !double.IsFinite(frame.ClippedRatio) ||
            frame.ClippedRatio is < 0 or > 1 || !double.IsFinite(frame.RawPeak) || frame.RawPeak is < 0 or > 1 ||
            !double.IsFinite(energy) || energy < 0)
        { _invalid = true; return; }
        _allSeconds += frame.Seconds;
        if (_allSeconds > elapsed + .3) _invalid = true;
        if (!frame.Settled) return;
        if (_levels.Count == 0) { _firstWall = elapsed; _firstSeconds = frame.Seconds; }
        if (_levels.Count >= 128 || elapsed < _lastWall || energy == 0)
        { _invalid = true; return; }
        double seconds = Math.Min(frame.Seconds, MeasurementSeconds - _sampled);
        _sampled += seconds; _energy += energy * seconds; _clipped += frame.ClippedRatio * seconds;
        double level = NoiseMeter.Dbfs(Math.Sqrt(energy))!.Value;
        if (level <= NoiseMeter.NumericalFloorDbfs) _invalid = true;
        _levels.Add(level); _lastWall = elapsed;
        if (_sampled > _lastWall - _firstWall + _firstSeconds + .3) _invalid = true;
    }

    public NoiseReferenceSample Finish(double elapsed, bool interrupted)
    {
        double wall = _levels.Count > 0 ? _lastWall - _firstWall + _firstSeconds : 0;
        double coverage = wall > 0 ? Math.Clamp(_sampled / wall, 0, 1) : 0;
        double clipped = _sampled > 0 ? 100 * _clipped / _sampled : 0;
        string quality = interrupted ? "Interrupted" : _invalid ? "Invalid" : clipped > .1 ? "Clipping" :
            !double.IsFinite(elapsed) || elapsed < _lastWall || elapsed > MaximumCaptureSeconds + .3 ||
            !Complete || coverage < .9 || wall < 9.7 ? "Incomplete" : "Good";
        return new(Guid.NewGuid(), role, _sampled, coverage,
            _sampled > 0 ? NoiseMeter.Dbfs(Math.Sqrt(_energy / _sampled)) : null,
            _levels.Count > 0 ? _levels.Min() : null, _levels.Count > 0 ? _levels.Max() : null,
            clipped, quality, startedAt.AddSeconds(Math.Max(0, _firstWall - _firstSeconds)),
            startedAt.AddSeconds(Math.Max(0, _lastWall)));
    }

    public static NoiseReferenceEvaluation Evaluate(NoiseReferenceDraft? draft)
    {
        static NoiseReferenceEvaluation No(string reason) => new(false, null, null, reason);
        if (draft is null) return No("请先新建外部参考草稿；此流程与快速阈值校准分开。");
        if (draft.TimeWeighting is not ("Fast" or "Slow") || !NoiseReferenceContract.Limit(draft.StabilityDb) ||
            !NoiseReferenceContract.Limit(draft.CheckToleranceDb) || draft.Samples is null || draft.Samples.Count > 2 ||
            draft.Samples.Any(s => s is null || s.Role is not ("Primary" or "Check")) ||
            draft.Samples.Select(s => s.Role).Distinct().Count() != draft.Samples.Count)
            return No("参考草稿格式不完整，不能计算偏移。");
        var primary = draft.Samples.SingleOrDefault(s => s.Role == "Primary");
        var check = draft.Samples.SingleOrDefault(s => s.Role == "Check");
        if (primary is null || check is null) return No("需要第一电平和另一稳定电平的复核采样。");
        foreach (var s in new[] { primary, check })
        {
            if (draft.MeterDevicePath is not null && !NoiseReferenceContract.MeterEvidenceValid(s.MeterEvidence))
                return No("USB 同期读数覆盖或时效不足，请重采；未使用缓存参考。");
            if (s.Quality != "Good" || s.DigitalDbfs is not { } digital || !double.IsFinite(digital) ||
                s.MinimumDbfs is not { } minimum || !double.IsFinite(minimum) || minimum <= NoiseMeter.NumericalFloorDbfs ||
                s.MaximumDbfs is not { } maximumLevel || !double.IsFinite(maximumLevel) ||
                digital < minimum - 1e-8 || digital > maximumLevel + 1e-8 ||
                !double.IsFinite(s.Seconds) || s.Seconds is < 9.999 or > 10.01 ||
                !double.IsFinite(s.Coverage) || s.Coverage is < .9 or > 1 ||
                !double.IsFinite(s.ClippedPercent) || s.ClippedPercent is < 0 or > .1)
                return No("采样质量不足，请重新采样；无效、未预热、削波和不足时长不能用于参考。");
            if (!s.SameWindowConfirmed || !NoiseReferenceContract.ReferenceRange(s.ReferenceDbA, s.ReferenceMinimumDbA, s.ReferenceMaximumDbA))
                return No("请为两段采样填写同一时段的 A 加权参考读数，并确认仪表时间模式一致、非 MAX 且读数稳定。");
            if (s.MaximumDbfs - s.MinimumDbfs > draft.StabilityDb ||
                s.ReferenceMaximumDbA is { } maximum && maximum - s.ReferenceMinimumDbA!.Value > draft.StabilityDb)
                return No("采样或参考读数的波动超过管理员填写的允许值，请换稳定声场重采。");
        }
        if (draft.MeterDevicePath is not null && primary.MeterEvidence!.Range != check.MeterEvidence!.Range)
            return No("两段仪表量程不同，请保持仪表设置一致并重新采样。");
        double referenceChange = check.ReferenceDbA!.Value - primary.ReferenceDbA!.Value;
        double digitalChange = check.DigitalDbfs!.Value - primary.DigitalDbfs!.Value;
        if (Math.Abs(referenceChange) < MinimumReferenceSeparationDb || referenceChange * digitalChange <= 0)
            return No("复核需使用明显不同的电平：参考差至少 6 dB，且麦克风变化方向一致；6 dB 是工程检查间隔，不是精度承诺。");
        double offset = primary.ReferenceDbA.Value - primary.DigitalDbfs.Value;
        double error = check.DigitalDbfs.Value + offset - check.ReferenceDbA.Value;
        if (Math.Abs(error) > draft.CheckToleranceDb)
            return new(false, offset, error, "第二电平复核未通过；请检查声场、输入增益、AGC／增强和仪表读数口径。");
        return new(true, offset, error, "两段统计满足当前填写的检查门槛，可以保存本机参考；这是估算配置，不证明计量精度，也尚未应用到正式监测。");
    }
}

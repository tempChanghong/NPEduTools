using NPEduTools.Contracts;

namespace NPEduTools.Core;

/// <summary>Bounded settled F/S energy statistics, never PCM or fabricated acoustic readings.</summary>
public sealed class NoiseReferenceCalibration(string role, string mode, DateTimeOffset startedAt, string workflow = "Stable")
{
    public const double MeasurementSeconds = 10;
    public const double MaximumCaptureSeconds = 20;
    public const double MinimumReferenceSeparationDb = 6; // Engineering check separation; not an accuracy claim.
    public const string Statistic = "mean-energy-of-100ms-time-weighted-endpoints-v1";
    public const string FieldStatistic = "mean-energy-of-100ms-time-weighted-endpoints-30s-field-v1";
    public static double Duration(string workflow) => workflow == "Field" ? 30 : MeasurementSeconds;
    public static double CaptureLimit(string workflow) => workflow == "Field" ? 40 : MaximumCaptureSeconds;
    public double MaximumSeconds => CaptureLimit(workflow);
    private readonly List<double> _levels = new(512);
    private double _allSeconds, _sampled, _energy, _clipped, _firstWall, _lastWall, _firstSeconds;
    private bool _invalid;
    public double MeasuredSeconds => _sampled;
    public bool Complete => _sampled >= Duration(workflow) - 1e-8;

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
        if (_levels.Count >= (workflow == "Field" ? 512 : 128) || elapsed < _lastWall || energy == 0)
        { _invalid = true; return; }
        double seconds = Math.Min(frame.Seconds, Duration(workflow) - _sampled);
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
            !double.IsFinite(elapsed) || elapsed < _lastWall || elapsed > MaximumSeconds + .3 ||
            !Complete || coverage < .9 || wall < Duration(workflow) - .3 ? "Incomplete" : "Good";
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
        bool field = draft.Workflow == "Field";
        if (!NoiseReferenceContract.WorkflowValid(draft.Workflow,draft.MeterDevicePath) || draft.TimeWeighting is not ("Fast" or "Slow") || !NoiseReferenceContract.StabilityLimit(draft.StabilityDb,draft.Workflow) ||
            !NoiseReferenceContract.CheckLimit(draft.CheckToleranceDb,draft.Workflow) || draft.Samples is null || draft.Samples.Count > 2 ||
            draft.Samples.Any(s => s is null || s.Role is not ("Primary" or "Check")) ||
            draft.Samples.Select(s => s.Role).Distinct().Count() != draft.Samples.Count)
            return No("参考草稿格式不完整，不能计算偏移。");
        var primary = draft.Samples.SingleOrDefault(s => s.Role == "Primary");
        var check = draft.Samples.SingleOrDefault(s => s.Role == "Check");
        if (primary is null || check is null) return No(field ? "需要第一段与独立的第二段现场采样；无需人为制造不同音量。" : "需要第一电平和另一稳定电平的复核采样。");
        if (field && check.WindowStartedAt < primary.WindowEndedAt) return No("现场复核必须来自第一段结束后的独立第二段；不能复用旧段或重叠时段。");
        foreach (var s in new[] { primary, check })
        {
            if (field && (s.ReferenceMinimumDbA is null || s.ReferenceMaximumDbA is null ||
                s.WindowEndedAt<=s.WindowStartedAt || Math.Abs(s.Coverage-Math.Clamp(s.Seconds/(s.WindowEndedAt-s.WindowStartedAt).TotalSeconds,0,1))>.01))
                return No("现场统计缺少同期读数范围，或覆盖率与窗口不一致，请重采。");
            if (draft.MeterDevicePath is not null && !NoiseReferenceContract.MeterEvidenceValid(s.MeterEvidence,draft.Workflow))
                return No("USB 同期读数覆盖或时效不足，请重采；未使用缓存参考。");
            if (s.Quality != "Good" || s.DigitalDbfs is not { } digital || !double.IsFinite(digital) ||
                s.MinimumDbfs is not { } minimum || !double.IsFinite(minimum) || minimum <= NoiseMeter.NumericalFloorDbfs ||
                s.MaximumDbfs is not { } maximumLevel || !double.IsFinite(maximumLevel) ||
                digital < minimum - 1e-8 || digital > maximumLevel + 1e-8 ||
                !double.IsFinite(s.Seconds) || s.Seconds < Duration(draft.Workflow) - .001 || s.Seconds > Duration(draft.Workflow) + .01 ||
                s.WindowEndedAt <= s.WindowStartedAt || (s.WindowEndedAt - s.WindowStartedAt).TotalSeconds < Duration(draft.Workflow) - .3 ||
                (s.WindowEndedAt - s.WindowStartedAt).TotalSeconds > CaptureLimit(draft.Workflow) + 1 ||
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
        if ((!field || Math.Abs(referenceChange) >= MinimumReferenceSeparationDb) &&
            (Math.Abs(referenceChange) < MinimumReferenceSeparationDb || referenceChange * digitalChange <= 0))
            return No("复核需使用明显不同的电平：参考差至少 6 dB，且麦克风变化方向一致；6 dB 是工程检查间隔，不是精度承诺。");
        double offset = primary.ReferenceDbA.Value - primary.DigitalDbfs.Value;
        double error = check.DigitalDbfs.Value + offset - check.ReferenceDbA.Value;
        if (Math.Abs(error) > (field ? 5 : draft.CheckToleranceDb) + 1e-8)
            return new(false, offset, error, "第二电平复核未通过；请检查声场、输入增益、AGC／增强和仪表读数口径。");
        if (field)
        {
            bool warning = Math.Abs(error) > draft.CheckToleranceDb + 1e-8;
            string scope = Math.Abs(referenceChange) < MinimumReferenceSeparationDb ? "NearbyLevel" : "TwoLevels";
            var assessment = new NoiseReferenceAssessment(warning, scope, Math.Min(primary.ReferenceDbA.Value, check.ReferenceDbA.Value),
                Math.Max(primary.ReferenceDbA.Value, check.ReferenceDbA.Value));
            return new(true, offset, error, (warning ? "两段偏移复核差超过自动通过门槛但不超过 5 dB；须明确确认后才能保存。" : "两段现场偏移复核通过，可以保存估算参考。") +
                (scope == "NearbyLevel" ? " 两段参考音量接近，仅验证参考均值附近，未验证全量程。" : " 已复核两个不同参考均值，不代表所有音量和频谱。") + " 仍为估算 dB(A)／Beta，不是精度保证。", assessment);
        }
        return new(true, offset, error, "两段统计满足当前填写的检查门槛，可以保存本机参考；这是估算配置，不证明计量精度，也尚未应用到正式监测。");
    }
    public static string? Describe(NoiseReferenceProfile? profile)
    {
        if (profile is not { Workflow: "Field" }) return null;
        var result = Evaluate(new(Guid.NewGuid(),profile.DeviceId,profile.Instrument,profile.TimeWeighting,profile.StabilityDb,
            profile.CheckToleranceDb,profile.Signature,profile.Samples,profile.MeterDevicePath,profile.Workflow));
        if (result.Assessment is not { } a) return "USB 现场估算参考（Beta），验证范围待确认。";
        return $"USB 现场校准（Beta）· 偏移复核差 {Math.Abs(result.CheckErrorDb!.Value):F1} dB · 参考均值 {a.ReferenceMinimumDbA:F1}–{a.ReferenceMaximumDbA:F1} dB(A)" +
            (a.Scope == "NearbyLevel" ? " 附近验证，未验证全量程" : " 两电平复核，未验证全量程") + (a.WarningRequired ? "；已明确接受复核差警告" : "");
    }
}

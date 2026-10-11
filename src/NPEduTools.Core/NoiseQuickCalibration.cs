using NPEduTools.Contracts;

namespace NPEduTools.Core;

/// <summary>Bounded statistics from energy frames. No PCM, assumed dBA or mandatory silent scene.</summary>
public sealed class NoiseQuickCalibration(string scene, string label)
{
    public const int DurationSeconds = 15;
    private readonly List<double> _levels = [];
    private double _seconds, _clipped;
    private bool _invalid;
    public void Receive(NoiseFrame frame, double? elapsed = null)
    {
        if (_seconds >= DurationSeconds + 1) return;
        if (frame.Invalid || !double.IsFinite(frame.Seconds) || frame.Seconds is <= 0 or > 1 ||
            !double.IsFinite(frame.MeanSquare) || frame.MeanSquare <= 0 ||
            !double.IsFinite(frame.Peak) || frame.Peak < 0 ||
            !double.IsFinite(frame.ClippedRatio) || frame.ClippedRatio is < 0 or > 1)
        { _invalid = true; return; }
        _seconds += frame.Seconds; _clipped += frame.Seconds * frame.ClippedRatio;
        // Buffered or fabricated frame bursts cannot count as seconds of natural observation.
        if (elapsed is { } wall && (!double.IsFinite(wall) || wall < 0 || _seconds > wall + .3)) _invalid = true;
        if (_levels.Count < 200) _levels.Add(NoiseMeter.Dbfs(Math.Sqrt(frame.MeanSquare))!.Value);
        else _invalid = true;
    }
    public NoiseCalibrationSample Finish(double elapsed, bool interrupted = false)
    {
        _levels.Sort();
        double coverage = elapsed > 0 ? Math.Clamp(_seconds / elapsed, 0, 1) : 0;
        double clipped = _seconds > 0 ? _clipped / _seconds * 100 : 0;
        string quality = interrupted ? "Interrupted" : _invalid ? "Invalid" : clipped > .1 ? "Clipping" :
            elapsed < DurationSeconds - .2 || coverage < .9 || _seconds < DurationSeconds * .9 || _seconds > elapsed + .3 ? "Incomplete" : "Good";
        double? Q(double p) => _levels.Count == 0 ? null : _levels[(int)Math.Round((_levels.Count - 1) * p)];
        return new(Guid.NewGuid(), scene, label, _seconds, coverage, Q(.1), Q(.5), Q(.9), clipped, quality);
    }
    public static NoiseCalibrationSuggestion Suggest(IReadOnlyList<NoiseCalibrationSample> samples)
    {
        var good = samples.Where(s => s.Quality == "Good").ToArray();
        var accepted = good.Where(s => s.Label == "Acceptable").ToArray();
        var high = good.Where(s => s.Label == "Discipline").ToArray();
        var visual = good.Where(s => s.Label == "Visual").ToArray();
        if (accepted.Length == 0 || high.Length == 0)
            return new(false, null, null, "至少补充一段可接受场景和一段希望纪律提醒的场景；不必采齐全部场景。");
        double lowEdge = accepted.Max(s => s.P90Dbfs!.Value), highEdge = high.Min(s => s.P10Dbfs!.Value);
        if (visual.Length == 0)
        {
            if (highEdge - lowEdge < 6) return Overlap();
            double gap = highEdge - lowEdge;
            return Candidate(Math.Round(lowEdge + gap / 3, 1), Math.Round(lowEdge + gap * 2 / 3, 1),
                "根据可接受与纪律场景之间的间隔建议两条线；视觉场景尚未采样，请试运行核对。");
        }
        double visualLow = visual.Min(s => s.P10Dbfs!.Value), visualHigh = visual.Max(s => s.P90Dbfs!.Value);
        if (visualLow - lowEdge < 3 || highEdge - visualHigh < 3) return Overlap();
        return Candidate(Math.Round((lowEdge + visualLow) / 2, 1), Math.Round((visualHigh + highEdge) / 2, 1),
            "已按管理员标记建议双阈值；确认前可以调整数值，之后观察实际提醒效果。");
    }
    private static NoiseCalibrationSuggestion Overlap() => new(false, null, null,
        "场景电平重叠或间隔不足，不能可靠自动建议。请补采、重新标记，或由管理员手动确认两条线。");
    private static NoiseCalibrationSuggestion Candidate(double medium, double high, string message) =>
        NoiseCalibrationContract.Thresholds(medium, high) ? new(true, medium, high, message) :
            new(false, null, null, "建议值超出可用阈值范围，请检查输入音量与采样场景；未填入无效值。");
}

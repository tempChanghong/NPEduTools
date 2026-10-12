using NPEduTools.Contracts;

namespace NPEduTools.Host;

/// <summary>Local end-of-study presentation. Never starts/stops capture or waits for upload.</summary>
public sealed class StudyReportService : IAsyncDisposable
{
    public const int DefaultDurationSeconds = 300;
    private readonly object _sync = new();
    private readonly Func<(string? Scope, bool Selected, StudyHostState State)> _sample;
    private readonly Func<StudySessionReport?> _statistics;
    private readonly Action<string, string, Guid, StudyNoise>? _observePolicy;
    private readonly TimeProvider _time;
    private readonly Func<bool>? _projection;
    private string _mode = "Daily";
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _loop;
    private readonly Dictionary<Guid, (double Offset, StudySessionReport Report)> _sessions = [];
    private string? _scope, _version;
    private SchoolNoiseWindow? _window;
    private Guid _instance = Guid.NewGuid();
    private long? _ended;
    private double _endDelay;
    private long? _statisticsAt;
    private bool _expired;
    private StudyReportData? _frozen;
    public StudyReportService(Func<(string? Scope, bool Selected, StudyHostState State)> sample,
        Func<StudySessionReport?> statistics, TimeProvider? time = null, bool background = true,
        Action<string, string, Guid, StudyNoise>? observePolicy = null, Func<bool>? projection = null)
    {
        _projection = projection;
        _sample = sample; _statistics = statistics; _time = time ?? TimeProvider.System; _observePolicy = observePolicy;
        _loop = background ? Task.Run(LoopAsync) : Task.CompletedTask;
    }
    private void Clear()
    {
        _window = null; _ended = null; _frozen = null; _expired = false; _statisticsAt = null;
        _sessions.Clear(); _instance = Guid.NewGuid();
    }
    public StudyReportState Snapshot()
    {
        lock (_sync)
        {
            var (scope, selected, s) = _sample();
            if (_scope != scope || _version != s.Version || _mode != s.Mode) { Clear(); _scope = scope; _version = s.Version; _mode = s.Mode; }
            if (!selected || scope is null || s.Version is null || s.PolicyState != "APPLIED" || !s.ClockReady || s.SchoolNow is not { } now ||
                s.Phase is not ("Active" or "Outside" or "Ended"))
            { Clear(); return State(false, "Unavailable", 0); }
            if (s.Phase == "Active" && s.Window is { } active)
            {
                if (_window != active) { Clear(); _window = active; }
                // Threshold statistics must also work with the App closed and photography disabled.
                if (s.Settings?.Noise is { Enabled: true } noise &&
                    s.Execution is { Owner: "Schedule", State: "Active", SignalFresh: true, SessionId: not null } execution)
                    _observePolicy?.Invoke(scope, s.Version, execution.SessionId.Value, noise);
                Observe(s, now); return State(false, "Monitoring", 0);
            }
            if (_expired) return State(false, "Expired", 0);
            if (_window is not { } window || now < window.End) return State(false, "Unavailable", 0);
            // Only a window witnessed active can finish. Late reconnects never create an old report.
            if (_ended is null)
            {
                if (now >= window.End.AddSeconds(5)) { Clear(); return State(false, "Unavailable", 0); }
                _ended = _time.GetTimestamp(); _endDelay = Math.Max(0, 5 - (now - window.End).TotalSeconds);
            }
            Observe(s, now);
            double elapsed = _time.GetElapsedTime(_ended.Value).TotalSeconds;
            double remaining = Math.Max(0, DefaultDurationSeconds + _endDelay - elapsed);
            if (remaining <= 0) { _expired = true; return State(false, "Expired", 0); }
            // Freeze only after terminal tails have been flushed. Until then show honest provisional statistics.
            if (_frozen is null && _sessions.Count > 0 && _sessions.Values.All(x => x.Report.Complete)) _frozen = Build();
            bool available = elapsed >= _endDelay;
            return State(available, available ? "Report" : "EndReminder", available ? remaining : DefaultDurationSeconds);
        }
    }
    private StudyReportState State(bool available, string state, double remaining) =>
        new(_instance, _version, available && _projection?.Invoke() != true, state, remaining, _window is null || state is "Monitoring" or "Unavailable" or "Expired" ? null : _frozen ?? Build());
    private void Observe(StudyHostState s, DateTimeOffset now)
    {
        if (_frozen is not null) return;
        if (s.Phase == "Active" && _statisticsAt is { } at && _time.GetElapsedTime(at).TotalSeconds < 1 &&
            s.Execution?.SessionId is { } current && _sessions.ContainsKey(current)) return;
        _statisticsAt = _time.GetTimestamp();
        if (_statistics() is not { } report) return;
        if (_sessions.TryGetValue(report.SessionId, out var old)) _sessions[report.SessionId] = (old.Offset, report);
        else if (_window is { } window && s.Phase == "Active" &&
            s.Execution is { Owner: "Schedule", State: "Starting" or "Active", SessionId: not null } execution && execution.SessionId == report.SessionId && _sessions.Count < 16)
            _sessions[report.SessionId] = (Math.Max(0, (now - window.Start).TotalSeconds - report.ElapsedSeconds), report);
    }
    private StudyReportData Build()
    {
        var window = _window!;
        double expected = (window.End - window.Start).TotalSeconds;
        var sessions = _sessions.Values.ToArray();
        var raw = Merge("dBFS", sessions.Select(x => (x.Offset, x.Report.Raw)), expected);
        var estimated = sessions.Any(x => x.Report.Estimated is not null)
            ? Merge("estimated-dB(A)", sessions.Where(x => x.Report.Estimated is not null).Select(x => (x.Offset, x.Report.Estimated!)), expected) : null;
        var notes = new List<string>();
        if (raw.SampledSeconds == 0) notes.Add("未取得有效采样；没有数据不代表安静。");
        if (raw.SampledSeconds > 0 && raw.EnergyMean is null or <= -100) notes.Add("输入为数字静音或接近静音，请检查静音、输入增益与麦克风；不据此判断教室安静。");
        if (raw.SampledSeconds < expected * .95) notes.Add("采集覆盖不足；空白时段未记作安静。");
        if (sessions.Any(x => x.Report.EstimateReason is not null)) notes.Add("参考已失效或不可用；估算数据仅包含有效参考期间。");
        notes.AddRange(sessions.Select(x=>x.Report.ReferenceNote).OfType<string>().Distinct());
        notes.Add("超限时长按采样时生效的阈值统计；中线以上包含高线以上。未判定时段不记作未超限。");
        return new(window, expected, raw.SampledSeconds, Math.Clamp(raw.SampledSeconds / expected, 0, 1),
            raw.SampledSeconds > 0 ? 100 * sessions.Sum(x => x.Report.ClippedSeconds) / raw.SampledSeconds : 0,
            sessions.Sum(x => x.Report.Frames), sessions.Sum(x => x.Report.DisciplineReminders), raw, estimated,
            sessions.All(x => x.Report.Complete), notes, _mode, _mode == "Exam" ? "Windows" : "ClassIsland");
    }
    private static StudyReportChannel Merge(string unit, IEnumerable<(double Offset, StudyReportChannel Data)> source, double expected)
    {
        var all = source.ToArray(); double sampled = all.Sum(x => x.Data.SampledSeconds);
        double energy = all.Sum(x => x.Data.EnergyMean is { } mean ? Math.Pow(10, mean / 10) * x.Data.SampledSeconds : 0);
        var maxima = all.Select(x => x.Data.Maximum).OfType<double>().ToArray();
        var points = all.SelectMany(x => x.Data.Trend.Select(p => p with { Seconds = Math.Min(expected, p.Seconds + x.Offset) }))
            .GroupBy(p => Math.Min(179, (int)(p.Seconds / 60))).OrderBy(g => g.Key).Select(g =>
            {
                double duration = g.Sum(p => p.SampledSeconds);
                double total = g.Sum(p => p.Mean is { } mean ? Math.Pow(10, mean / 10) * p.SampledSeconds : 0);
                return new StudyReportPoint(g.Key * 60, duration,
                    total > 0 ? 10 * Math.Log10(total / duration) : null, g.Max(p => p.Maximum));
            }).ToArray();
        return new(unit, sampled, sampled > 0 && energy > 0 ? 10 * Math.Log10(energy / sampled) : null,
            maxima.Length > 0 ? maxima.Max() : null, all.Sum(x => x.Data.ThresholdSampledSeconds),
            all.Sum(x => x.Data.MediumSeconds), all.Sum(x => x.Data.HighSeconds), points, all.Sum(x => x.Data.MediumThresholdSampledSeconds));
    }
    private async Task LoopAsync()
    {
        try { while (!_stop.IsCancellationRequested) { Snapshot(); await Task.Delay(TimeSpan.FromMilliseconds(200), _time, _stop.Token); } }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
    }
    public async ValueTask DisposeAsync() { _stop.Cancel(); await _loop; _stop.Dispose(); }
}

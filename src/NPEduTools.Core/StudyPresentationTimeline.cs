using NPEduTools.Contracts;

namespace NPEduTools.Core;

/// <summary>Consumes school timestamps only. Monotonic time expires observations and animations, never advances school time.</summary>
public sealed class StudyPresentationTimeline(TimeProvider? time = null)
{
    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private long? _received, _noticeUntil;
    private StudyHostState? _state;
    private Guid? _instance;
    private string? _notice, _cue;
    private readonly HashSet<string> _seen = [];
    private readonly Queue<string> _order = new();
    public StudyHostState? State => _state;
    public bool Fresh => _state is { PolicyState: "APPLIED", ClockReady: true, SchoolNow: not null } &&
        _received is { } at && _time.GetElapsedTime(at) < TimeSpan.FromSeconds(1.5);
    public string? Notice => Fresh && (_notice == "Preparing" && _state?.Phase == "Preparing" ||
        _noticeUntil is { } until && _time.GetTimestamp() < until) ? _notice : null;
    public double Progress => Fresh && _state?.Window is { } w && _state.SchoolNow is { } now
        ? Math.Clamp((now - w.Start).TotalSeconds / (w.End - w.Start).TotalSeconds, 0, 1) : 0;
    public void Apply(StudyHostState state, TimeSpan transportAge = default)
    {
        if (_notice == "Discipline" && (_state?.Execution?.SessionId != state.Execution?.SessionId || _state?.Version != state.Version)) ClearNotice();
        if (_instance != state.Display?.InstanceId)
        { _instance = state.Display?.InstanceId; _seen.Clear(); _order.Clear(); ClearNotice(); }
        _state = state;
        _received = transportAge < TimeSpan.Zero || transportAge >= TimeSpan.FromSeconds(1.5) ? null :
            _time.GetTimestamp() - (long)(transportAge.TotalSeconds * _time.TimestampFrequency);
        if (!Fresh || state.Window is not { } window || state.Phase is not ("Preparing" or "Active" or "Ended"))
        { ClearNotice(); return; }
        if (_notice != state.Phase && !(_notice == "Discipline" && state.Phase == "Active" &&
            state.Execution is { Owner: "Schedule", State: "Active", SignalFresh: true })) ClearNotice();
        string key = $"{window.Start:O}|{window.End:O}|{state.Phase}";
        if (!_seen.Add(key))
        {
            // Restore the persistent preparation visual after a priority window or brief disconnect,
            // while retaining the once-per-window audio fence.
            if (state.Phase == "Preparing") _notice = "Preparing";
            return;
        }
        _order.Enqueue(key); if (_order.Count > 128) _seen.Remove(_order.Dequeue());
        double duration = state.Phase switch
        {
            "Preparing" => (window.Start - state.SchoolNow!.Value).TotalSeconds,
            "Active" => 5 - (state.SchoolNow!.Value - window.Start).TotalSeconds - transportAge.TotalSeconds,
            _ => 5 - (state.SchoolNow!.Value - window.End).TotalSeconds - transportAge.TotalSeconds
        };
        // A refreshed App halfway through a study session does not announce a new start.
        if (duration <= 0) return;
        _notice = _cue = state.Phase;
        _noticeUntil = state.Phase == "Preparing" ? null : _time.GetTimestamp() + (long)(Math.Min(5, duration) * _time.TimestampFrequency);
    }
    public string? TakeCue() { var cue = _cue; _cue = null; return cue; }
    public bool BeginDiscipline()
    {
        // Stage transitions take priority; discipline never queues behind a start/end notice.
        if (!Fresh || Notice is not null || _state is not { Phase: "Active", Execution: { Owner: "Schedule", State: "Active", SignalFresh: true } }) return false;
        _notice = "Discipline"; _noticeUntil = _time.GetTimestamp() + 5 * _time.TimestampFrequency;
        return true;
    }
    public void CancelDiscipline() { if (_notice == "Discipline") ClearNotice(); }
    public void Suspend() { _received = null; ClearNotice(); }
    private void ClearNotice() { _notice = _cue = null; _noticeUntil = null; }
}

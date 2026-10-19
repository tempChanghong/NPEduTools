using NPEduTools.Contracts;

namespace NPEduTools.Core;

// A read failure may retain an existing window, never authorize a new one or advance school time.
public sealed class StudyDisplayContinuation(TimeProvider? time = null)
{
    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private long? _confirmed;
    private TimeSpan _remaining;
    public bool CanKeep => _confirmed is { } at && _time.GetElapsedTime(at) is var elapsed &&
        elapsed >= TimeSpan.Zero && elapsed < _remaining;

    public void Observe(StudyHostState state, TimeSpan transportAge = default)
    {
        _confirmed = null;
        if (transportAge < TimeSpan.Zero || transportAge >= TimeSpan.FromSeconds(1.5) ||
            state is not { PolicyState: "APPLIED", ClockReady: true, SchoolNow: { } now, Window: { } window,
                Display: { Selected: true, Available: true }, ProjectionPriority: false, Phase: "Preparing" or "Active" or "Ended" } ||
            string.IsNullOrEmpty(state.Version) || window.End <= window.Start ||
            (window.Start - now).TotalSeconds > StudyPresentationTiming.ScheduledDisplayLeadSeconds) return;
        // A preparation observation cannot stand in for confirmation that a session actually started.
        var end = state.Phase == "Preparing" ? window.Start : window.End;
        _remaining = end - now + TimeSpan.FromSeconds(StudyPresentationTiming.StageNoticeSeconds) - transportAge;
        if (_remaining <= TimeSpan.Zero) return;
        _confirmed = _time.GetTimestamp();
    }
}

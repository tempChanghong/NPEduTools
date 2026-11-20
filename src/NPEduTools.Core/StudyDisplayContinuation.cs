using NPEduTools.Contracts;

namespace NPEduTools.Core;

// A read failure may retain an existing window, never authorize a new one or advance school time.
public sealed class StudyDisplayContinuation(TimeProvider? time = null)
{
    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private long? _confirmed;
    private TimeSpan _remaining;
    private TimeSpan _syncWaitLimit;
    private string? _activeVersion, _activeMode;
    public bool CanKeep => _confirmed is { } at && _time.GetElapsedTime(at) is var elapsed &&
        elapsed >= TimeSpan.Zero && elapsed < _remaining;

    public void Observe(StudyHostState state, TimeSpan transportAge = default)
    {
        _confirmed = null;
        _activeVersion = null;
        if (transportAge < TimeSpan.Zero || transportAge >= TimeSpan.FromSeconds(1.5) ||
            state is not { PolicyState: "APPLIED", ClockReady: true, SchoolNow: { } now, Window: { } window,
                Display: { Selected: true, Available: true }, ProjectionPriority: false, Phase: "Preparing" or "Active" or "Ended" } ||
            string.IsNullOrEmpty(state.Version) || window.End <= window.Start ||
            (window.Start - now).TotalSeconds > (state.Manual is { } manual
                ? Math.Clamp(manual.DisplayLeadSeconds, 0, 1800) : StudyPresentationTiming.ScheduledDisplayLeadSeconds)) return;
        // A preparation observation cannot stand in for confirmation that a session actually started.
        var end = state.Phase == "Preparing" ? window.Start : window.End;
        _remaining = end - now + TimeSpan.FromSeconds(StudyPresentationTiming.StageNoticeSeconds) - transportAge;
        if (_remaining <= TimeSpan.Zero) return;
        _confirmed = _time.GetTimestamp();
        if (state is { Phase: "Active", Manual: null })
        {
            _activeVersion = state.Version;
            _activeMode = state.Mode;
            // Allow two three-second school sync cycles. Never wait past the known end of class.
            _syncWaitLimit = TimeSpan.FromSeconds(8);
            if (end - now < _syncWaitLimit) _syncWaitLimit = end - now;
            _syncWaitLimit -= transportAge;
        }
    }

    public bool TryWaitForSchoolSync(StudyHostState state)
    {
        if (_activeVersion is null || state.Mode != _activeMode ||
            state is not { Phase: "Blocked", ClockReady: false, Window: null, Manual: null, ProjectionPriority: false,
                Display: { Selected: true, Available: false, HiddenRemainingSeconds: 0 } }) return false;
        bool transient = state.PolicyState switch
        {
            "UNAVAILABLE" => state.Version is null && state.Reason is "STUDY_OFFLINE" or "TEMPORARILY_UNAVAILABLE" or "RATE_LIMITED" or "CONFIGURATION_STALE",
            "APPLIED" => state.Version == _activeVersion && state.Reason == "SCHOOL_CLOCK_UNAVAILABLE",
            _ => false
        };
        if (!transient) return false;
        // Repeated failures can shorten the display lease, never refresh its original timestamp.
        if (_syncWaitLimit < _remaining) _remaining = _syncWaitLimit;
        return CanKeep;
    }
}

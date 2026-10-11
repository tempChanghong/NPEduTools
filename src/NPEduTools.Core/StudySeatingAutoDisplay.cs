using NPEduTools.Contracts;
namespace NPEduTools.Core;

// One automatic visit per study window. Manual viewing is never part of homework rotation.
public sealed class StudySeatingAutoDisplay
{
    private DateTimeOffset? _start;
    private string? _mode;
    private bool _dismissed, _expired;
    public bool WantsImage(StudyHostState state)
    {
        if (state.Window is { } w && (_start != w.Start || _mode != state.Mode)) {
            _start = w.Start; _mode = state.Mode; _dismissed = _expired = false;
        }
        if (state.ClockReady && state.Window is { } current && state.SchoolNow is { } timestamp &&
            (timestamp >= current.Start.AddSeconds(StudyPresentationTiming.SeatingDismissAfterStartSeconds) || timestamp >= current.End)) _expired = true;
        return !_dismissed && !_expired && state.Manual is null && state.PolicyState == "APPLIED" && state.ClockReady &&
            state.Phase is "Preparing" or "Active" && state.Window is { } window && state.SchoolNow is { } now &&
            now >= window.Start.AddSeconds(-StudyPresentationTiming.ScheduledDisplayLeadSeconds) &&
            now < window.Start.AddSeconds(StudyPresentationTiming.SeatingDismissAfterStartSeconds) && now < window.End;
    }
    public void ManualChoice() => _dismissed = true;
}

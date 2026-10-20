using NPEduTools.Contracts;

namespace NPEduTools.App;

// Uses the existing status poll; a return action never opens a microphone or starts a session.
internal sealed class StudyDisplayReturn(TimeProvider? time = null)
{
    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private StudyHostState? _state;
    private long _at;
    public void Observe(StudyHostState? state) { _state = state; _at = _time.GetTimestamp(); }
    public bool Available => _state is { PolicyState: "APPLIED", ClockReady: true, ProjectionPriority: false,
        Phase: "Preparing" or "Active", Window: not null, Display: { Selected: true, HiddenRemainingSeconds: > 0 } } &&
        _time.GetElapsedTime(_at).TotalSeconds is >= 0 and < 3.5;
    public HostRequest? Request(bool manual)
    {
        if (!Available) return null;
        var state = _state!;
        return new(1, Guid.NewGuid(), manual ? "manual.study.show" : "study.show",
            StudyHide: new(state.Display!.InstanceId, state.Version!, state.Window!));
    }
}

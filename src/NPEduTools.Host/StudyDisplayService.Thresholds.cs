using NPEduTools.Contracts;

namespace NPEduTools.Host;

public sealed partial class StudyDisplayService
{
    private Guid? _disciplineOffer;
    private long _disciplineOfferedAt;
    private (string? Scope, string? Version, Guid Session, StudyNoise Policy)? _thresholdContext;
    private StudyThresholdState? ObserveThreshold(StudyHostState state)
    {
        // Polling checkpoints elapsed progress, never school/Windows wall time. Binding hashes remain independent.
        bool cooled = _scope is not null && state.Settings is { } settings && _cooldown.CanRemind(_scope, settings.Noise.DisciplineCooldownSeconds);
        bool eligible = (state.Execution?.Owner != "Manual" || state.Manual is not null) && state.PolicyState == "APPLIED" && state.ClockReady &&
            state.Phase == "Active" && state.Window is not null && state.Settings?.Noise is { Enabled: true } &&
            state.Execution is { Owner: "Schedule" or "Manual", State: "Active", SignalFresh: true, SessionId: not null };
        if (!eligible)
        { thresholds?.Invoke(null, null); _thresholdContext = null; _disciplineOffer = null; return null; }
        var effectivePolicy = calibratedThresholds?.Invoke(_scope, state.Version, state.Settings!.Noise) ?? state.Settings!.Noise;
        if (effectivePolicy.High is null)
        { thresholds?.Invoke(null, null); _thresholdContext = null; _disciplineOffer = null; return null; }
        var context = (_scope, state.Version, state.Execution!.SessionId!.Value, effectivePolicy);
        if (_thresholdContext != context) { _thresholdContext = context; _disciplineOffer = null; }
        var observed = thresholds?.Invoke(context.Item3, context.Item4) ?? ("None", false);
        bool ready = observed.Qualified && state.Display is { Available: true, Selected: true } && state.SchoolNow >= state.Window!.Start.AddSeconds(5) &&
            cooled;
        if (!ready) _disciplineOffer = null;
        else if (_disciplineOffer is null || _time.GetElapsedTime(_disciplineOfferedAt).TotalSeconds >= 5)
        { _disciplineOffer = Guid.NewGuid(); _disciplineOfferedAt = _time.GetTimestamp(); }
        return new(context.Item3, observed.Band, _disciplineOffer, _cooldown.Error);
    }
    private HostResponse ClaimDiscipline(HostRequest request, StudyHostState state)
    {
        var reminder = request.StudyReminder!;
        if (reminder.InstanceId != _instance || reminder.Version != state.Version || reminder.Window != state.Window ||
            reminder.DisciplineId is null || reminder.DisciplineId != _disciplineOffer ||
            state.Threshold is not { Band: "High", DisciplineId: not null } threshold || reminder.SessionId != threshold.SessionId)
            return new(1, request.RequestId, "Rejected", "STUDY_STATE_CHANGED", "超限状态已变化，不播放旧提醒。");
        _disciplineOffer = null;
        if (_scope is null || !_cooldown.Claim(_scope))
            return new(1, request.RequestId, "Rejected", _cooldown.Error ?? "STUDY_REMINDER_STORE_UNAVAILABLE", "纪律提醒记录暂不可用；未授权播报，监测继续。", Study: Snapshot());
        disciplineClaimed?.Invoke(threshold.SessionId);
        return new(1, request.RequestId, "Succeeded", null, "允许当前超限提醒一次；不代表摄像头捕获。", Study: state);
    }
}

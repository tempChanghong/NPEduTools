using NPEduTools.Contracts;
using NPEduTools.Core;

namespace NPEduTools.Host;

/// <summary>Presentation only. A monotonic Host deadline survives App restarts, but never resumes capture.</summary>
public sealed partial class StudyDisplayService(Func<(string? Scope, bool Selected, StudyHostState State)> sample, TimeProvider? time = null,
    Func<Guid?, StudyNoise?, (string Band, bool Qualified)>? thresholds = null, string? directory = null,
    Func<string?, string?, StudyNoise, StudyNoise>? calibratedThresholds = null, Action<Guid>? disciplineClaimed = null, Func<bool>? projection = null)
{
    private readonly object _sync = new();
    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly StudyReminderStore _cooldown = new(directory, time);
    private readonly StudyStageReminderStore _stageReminders = new(directory);
    private Guid _instance = Guid.NewGuid();
    private string? _scope, _version;
    private string _mode = "Daily";
    private SchoolNoiseWindow? _lastWindow, _hiddenWindow;
    private SchoolNoiseWindow? _presentationWindow;
    private long? _hiddenUntil;
    private bool _manualHidden;
    private readonly HashSet<Guid> _requests = [];
    private readonly Queue<Guid> _requestOrder = new();
    private double Remaining => _hiddenUntil is { } until ? Math.Max(0, (until - _time.GetTimestamp()) / (double)_time.TimestampFrequency) : 0;
    public void ResumeAfterExitWarning(StudyHostState expected)
    {
        lock(_sync){var current=Snapshot();if(current.PolicyState=="APPLIED" && current.Phase=="Active" && current.ClockReady && current.Version==expected.Version && current.Window==expected.Window){_hiddenUntil=null;_hiddenWindow=null;}}
    }

    public StudyHostState Snapshot()
    {
        lock (_sync)
        {
            var current = sample(); var state = current.State;
            if (_scope != current.Scope || !current.Selected)
            {
                if (_scope != current.Scope) _instance = Guid.NewGuid();
                _scope = current.Scope; _lastWindow = _hiddenWindow = null; _hiddenUntil = null;_manualHidden=false;
                _requests.Clear(); _requestOrder.Clear();
                _presentations.Clear();
                _presentationWindow = null;
            }
            if (_version != state.Version || _mode != state.Mode) { _mode = state.Mode; _lastWindow = null; _version = state.Version; _instance = Guid.NewGuid(); _presentations.Clear(); }
            // The end overlay is displayed only after observing this exact active window.
            // Reconnection, disabled policy, exam or invalid time cannot manufacture an end event.
            if (state.PolicyState != "APPLIED" || !state.ClockReady) _lastWindow = null;
            else if (state.Phase == "Active") _lastWindow = state.Window;
            if (state.PolicyState == "APPLIED" && state.ClockReady && state.Phase == "Outside" &&
                _lastWindow is { } ended && state.SchoolNow is { } now && now >= ended.End && now < ended.End.AddSeconds(5))
                state = state with { Phase = "Ended", Window = ended, Reason = "WINDOW_ENDED" };
            else if (state.Phase is not ("Active" or "Preparing" or "Ended")) _lastWindow = null;
            if (state.Window is { } presentationWindow && presentationWindow != _presentationWindow)
            {
                _presentationWindow = presentationWindow; _instance = Guid.NewGuid(); _presentations.Clear();
            }
            double hidden = state.Window == _hiddenWindow ? _manualHidden?10801:Remaining : 0;
            bool projectionPriority = projection?.Invoke() == true;
            if (projectionPriority && current.Selected && _scope is not null && state.Window is { } mutedWindow && state.SchoolNow is { } mutedNow &&
                (state.Phase is "Preparing" or "Active" or "Ended" && StudyPresentationTiming.NoticeRemaining(state) > 0))
                _stageReminders.Claim(_scope + (_mode == "Exam" ? ":Exam" : ""), mutedWindow, state.Phase);
            state = state with { ProjectionPriority = projectionPriority };
            bool available = !projectionPriority && current.Selected && state.PolicyState == "APPLIED" && state.ClockReady &&
                state.Window is not null && state.Phase is "Preparing" or "Active" or "Ended" && hidden <= 0;
            state = state with { Display = new(_instance, current.Selected, available, hidden, _stageReminders.Error) };
            return state with { Threshold = ObserveThreshold(state) };
        }
    }

    public HostResponse Handle(HostRequest request)
    {
        lock (_sync)
        {
            var state = Snapshot();
            if (request.Capability == "study.status") return new(1, request.RequestId, "Succeeded", null, "学校自习状态。", Study: state);
            if (request.Capability == "study.presentation") return ReceivePresentation(request, state);
            if (request.Capability == "study.reminder")
            {
                var reminder = request.StudyReminder;
                if (reminder?.Phase == "Discipline") return ClaimDiscipline(request, state);
                if (_scope is null || reminder is null || reminder.InstanceId != _instance || reminder.Version != state.Version || reminder.Window != state.Window ||
                    reminder.Phase != state.Phase || state.Display is not { Available: true } || state.SchoolNow is not { } now ||
                    state.Phase == "Preparing" && StudyPresentationTiming.NoticeRemaining(state) <= 0 ||
                    state.Phase == "Ended" && (now < reminder.Window.End || now >= reminder.Window.End.AddSeconds(5)) ||
                    state.Phase == "Active" && (now < reminder.Window.Start || now >= reminder.Window.Start.AddSeconds(5)))
                    return new(1, request.RequestId, "Rejected", "STUDY_STATE_CHANGED", "阶段已变化，不播放旧提示音。");
                var claimed = _stageReminders.Claim(_scope + (_mode == "Exam" ? ":Exam" : ""), reminder.Window, reminder.Phase);
                if (claimed == StudyStageClaim.Duplicate) return new(1, request.RequestId, "Suppressed", null, "本阶段已申请提示音，不重复播放。");
                if (claimed == StudyStageClaim.Unavailable)
                    return new(1, request.RequestId, "Rejected", _stageReminders.Error, "阶段提示音记录暂不可用；监测和展示继续。", Study: Snapshot());
                return new(1, request.RequestId, "Succeeded", null, "仅允许当前阶段一次提示音。");
            }
            var command = request.StudyHide;
            if (command is null || command.InstanceId != _instance || command.Version != state.Version ||
                command.Window != state.Window || state.PolicyState != "APPLIED" || !state.ClockReady || state.Phase != "Active")
                return new(1, request.RequestId, "Rejected", "STUDY_STATE_CHANGED", "自习状态已变化，请重新查看。", Study: state);
            if (_requests.Contains(request.RequestId)) return new(1, request.RequestId, "Succeeded", null, "沿用原返回期限。", Study: state);
            _requests.Add(request.RequestId); _requestOrder.Enqueue(request.RequestId);
            if (_requestOrder.Count > 128) _requests.Remove(_requestOrder.Dequeue());
            if (state.Display!.HiddenRemainingSeconds <= 0)
            {
                int seconds = Math.Clamp(state.Manual?.HideSeconds??state.Settings!.Presentation.TemporaryHideSeconds, 1, state.Manual is null?180:10800);
                _manualHidden=state.Manual is {HideSeconds:null};
                _hiddenWindow = state.Window;
                _hiddenUntil = _time.GetTimestamp() + (long)(seconds * (double)_time.TimestampFrequency);
            }
            return new(1, request.RequestId, "Succeeded", null, "临时收起展示，监测按原规则运行。", Study: Snapshot());
        }
    }
}

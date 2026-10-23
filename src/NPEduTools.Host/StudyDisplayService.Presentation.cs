using System.Globalization;
using System.Text.Json.Nodes;
using NPEduTools.Contracts;

namespace NPEduTools.Host;

public sealed partial class StudyDisplayService
{
    private sealed record Seen(StudyPresentationPulse Pulse, long At);
    private readonly Dictionary<Guid, Seen> _presentations = [];
    private Guid _presentationEpoch = Guid.NewGuid();
    private Guid _presentationEpochInstance;
    private bool _presentationEpochRequired;
    private const int PresentationLifetimeMs = 5000;
    private void ResetPresentations()
    {
        _presentations.Clear();
        _presentationEpoch = Guid.NewGuid();
        // A cache reset alone must not admit legacy packets after tombstones were retired.
        if (_presentationEpochInstance != _instance) _presentationEpochRequired = false;
        _presentationEpochInstance = _instance;
    }
    private HostResponse PresentationEpoch(HostRequest request, StudyHostState state) =>
        Protocol.Validate(request) is not null || state.PolicyState != "APPLIED" || !state.ClockReady ||
        state.Display?.Selected != true || state.Window is null || state.Phase is not ("Preparing" or "Active" or "Ended")
        ? new(1, request.RequestId, "Rejected", "STUDY_STATE_CHANGED", "展示状态已变化。")
        : new(1, request.RequestId, "Succeeded", null, "展示回执世代。",
            PresentationEpoch: new(_instance, _presentationEpoch));
    private int PulseRemaining(Seen seen) => Math.Max(0, PresentationLifetimeMs - (int)Math.Min(int.MaxValue,
        _time.GetElapsedTime(seen.At).TotalMilliseconds) - (seen.Pulse.RenderAgeMs ?? 0));
    private HostResponse ReceivePresentation(HostRequest request, StudyHostState state)
    {
        var pulse = request.StudyPresentation;
        if (Protocol.Validate(request) is not null || pulse is null || pulse.InstanceId != _instance ||
            pulse.Version != state.Version || pulse.Window != state.Window || pulse.Phase != state.Phase ||
            state.PolicyState != "APPLIED" || !state.ClockReady || !state.Display!.Selected ||
            pulse.State == "Visible" && !state.Display.Available)
            return new(1, request.RequestId, "Rejected", "STUDY_STATE_CHANGED", "展示回执不属于当前时段。");
        bool registered = _presentations.TryGetValue(pulse.AppInstanceId, out var old);
        // A still-registered survivor may keep its original epoch. Once removed, that
        // identity cannot return with a retired epoch; adopting the new epoch is one-way.
        bool registeredEpoch = registered && pulse.ObservationEpoch == old!.Pulse.ObservationEpoch;
        if (pulse.ObservationEpoch is { } epoch ? epoch != _presentationEpoch && !registeredEpoch : _presentationEpochRequired && !registeredEpoch)
            return new(1, request.RequestId, "Rejected", "STUDY_OBSERVATION_CHANGED", "请重新读取展示回执世代。");
        if (registered && pulse.Sequence <= old!.Pulse.Sequence)
            return pulse == old.Pulse ? new(1, request.RequestId, "Suppressed", null, "重复回执不延长有效期。")
                : new(1, request.RequestId, "Rejected", "STUDY_SEQUENCE_CONFLICT", "展示回执序号已失效。");
        // Multiple App processes cannot overwrite another process's fresh visible observation.
        // Retire bounded tombstones only behind a negotiated, separate observation fence.
        // Replaying the triggering packet cannot rotate this fence again or refresh visibility.
        if (!registered && _presentations.Count >= 16)
        {
            if (pulse.ObservationEpoch == _presentationEpoch && _presentations.Values.Any(p => PulseRemaining(p) == 0))
            {
                foreach (var retired in _presentations.Where(p => PulseRemaining(p.Value) == 0).Select(p => p.Key).ToArray())
                    _presentations.Remove(retired);
                _presentationEpoch = Guid.NewGuid();
                _presentationEpochRequired = true;
                return new(1, request.RequestId, "Rejected", "STUDY_OBSERVATION_CHANGED", "旧展示回执已过期，请读取新世代后重新采样。");
            }
            return new(1, request.RequestId, "Rejected", "STUDY_CLIENT_LIMIT", "展示客户端数量超过限制。");
        }
        _presentations[pulse.AppInstanceId] = new(pulse, _time.GetTimestamp());
        return new(1, request.RequestId, "Succeeded", null, "已收到界面回执；不改变采集状态。");
    }
    public JsonObject? ObservePresentation()
    {
        lock (_sync)
        {
            var state = Snapshot();
            if (state.PolicyState != "APPLIED" || state.Version is null) return null;
            string shown = "Unknown", reason = "WAITING_APP";
            Seen? seen = null; int remaining = PresentationLifetimeMs;
            if (!state.Display!.Selected || state.Phase is "Disabled" or "Outside")
            { shown = "NotRequired"; reason = state.Reason == "OFFLINE_CONTINUING" ? "OUTSIDE_WINDOW" : state.Reason; }
            else if (!state.ClockReady || state.Phase == "Blocked")
            { shown = "Blocked"; reason = state.Reason; }
            else
            {
                var matching = _presentations.Values.Where(p => p.Pulse.Window == state.Window && p.Pulse.Phase == state.Phase && PulseRemaining(p) > 0).ToArray();
                seen = matching.Where(p => p.Pulse.State == "Visible" && state.Display.Available).OrderByDescending(p => p.At).FirstOrDefault()
                    ?? matching.OrderByDescending(p => p.At).FirstOrDefault();
                if (seen is not null)
                {
                    remaining = PulseRemaining(seen);
                    shown = seen.Pulse.State == "Visible" && state.Display.Available ? "Visible" : seen.Pulse.State == "Hidden" ? "Hidden" : "Unknown";
                    reason = shown == "Visible" ? "FRAME_CURRENT" : shown == "Hidden" ? state.Display.HiddenRemainingSeconds > 0 ? "TEMPORARY_HIDE" : "APP_HIDDEN"
                        : seen.Pulse.State == "Visible" ? "WAITING_HIDE_CONFIRMATION" : "FRAME_STALE";
                }
                else if (_presentations.Count > 0) reason = "APP_STALE";
            }
            static string Stamp(DateTimeOffset value) => value.ToString("yyyy-MM-dd'T'HH:mm:ss.fff", CultureInfo.InvariantCulture);
            var result = new JsonObject { ["version"] = state.Version, ["status"] = new JsonObject {
                ["state"] = shown, ["reason"] = reason, ["phase"] = state.Phase, ["clockReady"] = state.ClockReady,
                ["schoolNow"] = state.SchoolNow is { } now ? Stamp(now) : null,
                ["window"] = state.Window is { } window ? new JsonObject { ["start"] = Stamp(window.Start), ["end"] = Stamp(window.End) } : null,
                ["hostInstanceId"] = _instance.ToString(), ["appInstanceId"] = seen?.Pulse.AppInstanceId.ToString(),
                ["pulse"] = seen?.Pulse.Sequence, ["remainingMs"] = remaining } };
            if (state.Mode == "Exam") result["status"]!["mode"] = state.Mode;
            if (state.ProjectionPriority) result["status"]!["reason"] = "EXAM_PRESENTATION_PRIORITY";
            return result;
        }
    }
}

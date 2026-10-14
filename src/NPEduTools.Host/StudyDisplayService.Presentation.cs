using System.Globalization;
using System.Text.Json.Nodes;
using NPEduTools.Contracts;

namespace NPEduTools.Host;

public sealed partial class StudyDisplayService
{
    private sealed record Seen(StudyPresentationPulse Pulse, long At);
    private readonly Dictionary<Guid, Seen> _presentations = [];
    private const int PresentationLifetimeMs = 5000;
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
        if (_presentations.TryGetValue(pulse.AppInstanceId, out var old) && pulse.Sequence <= old.Pulse.Sequence)
            return pulse == old.Pulse ? new(1, request.RequestId, "Suppressed", null, "重复回执不延长有效期。")
                : new(1, request.RequestId, "Rejected", "STUDY_SEQUENCE_CONFLICT", "展示回执序号已失效。");
        // Multiple App processes cannot overwrite another process's fresh visible observation.
        // Retain sequence tombstones for this display epoch; bound memory and reject overflow.
        if (!_presentations.ContainsKey(pulse.AppInstanceId) && _presentations.Count >= 16)
            return new(1, request.RequestId, "Rejected", "STUDY_CLIENT_LIMIT", "展示客户端数量超过限制。");
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

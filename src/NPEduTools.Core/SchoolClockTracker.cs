using NPEduTools.Contracts;

namespace NPEduTools.Core;

public sealed record SchoolClockReading(DateTimeOffset? Now, double AgeMs, bool Fresh, bool CanStart,
    string Message, bool DateNeedsReview = false);

/// <summary>Client-side monotonic freshness and continuity guard. Never extrapolates school calendar time.</summary>
public sealed class SchoolClockTracker
{
    private SchoolClockFrame? _frame;
    private TimeSpan _received, _stableSince, _lastAdvance;
    private int _stableSamples;
    private bool _healthy, _dateNeedsReview;
    private string _message = "等待 ClassIsland 学校时间；不会使用系统时间代替";
    public DaySchedule? Schedule => _frame?.Schedule;

    public void Accept(SchoolClockFrame frame, TimeSpan elapsed, double roundTripMs)
    {
        if (!double.IsFinite(frame.AgeMs) || frame.AgeMs < 0 || !double.IsFinite(roundTripMs) || roundTripMs < 0 ||
            frame.SchoolNow is { Offset.Ticks: not 0 })
        { Unavailable("收到无效的学校时间", elapsed); return; }
        frame = frame with { AgeMs = frame.AgeMs + roundTripMs };
        if (frame.SchoolNow is null || frame.State == "Unavailable")
        { Unavailable(frame.Message, elapsed); return; }
        var previous = _frame;
        bool advanced = previous?.SchoolNow is null || frame.SchoolNow > previous.SchoolNow;
        bool same = previous is not null && previous.ConnectionId == frame.ConnectionId && previous.BridgeInstanceId == frame.BridgeInstanceId;
        bool oldFresh = previous is not null && previous.AgeMs + (elapsed - _received).TotalMilliseconds < SchoolClockFrame.MaxAgeMs;
        if (same && frame.Sequence < previous!.Sequence)
        { Unavailable("学校时间样本顺序异常", elapsed); return; }
        if (same && frame.Sequence == previous!.Sequence)
        {
            // Re-reading a Host or plugin cache cannot renew its lease or count as advancing evidence.
            if (frame.SchoolNow != previous.SchoolNow || frame.Epoch != previous.Epoch)
            { Unavailable("学校时间样本内容异常", elapsed); return; }
            double age = Math.Max(frame.AgeMs, previous.AgeMs + (elapsed - _received).TotalMilliseconds);
            _frame = frame with { AgeMs = age }; _received = elapsed;
            if (age >= SchoolClockFrame.MaxAgeMs || frame.State != "Advancing") Reset("学校时间样本已陈旧或暂停");
            return;
        }
        if (previous?.SchoolNow is { } prior && frame.SchoolNow is { } current)
        {
            double delta = (current - prior).TotalMilliseconds;
            double expected = (elapsed - _received).TotalMilliseconds + previous.AgeMs - frame.AgeMs;
            bool discontinuous = delta < -1 || Math.Abs(delta - expected) > 2000 || (same && frame.Epoch != previous.Epoch);
            // A normal midnight transition is fine; a calendar jump needs explicit review.
            if (DateOnly.FromDateTime(prior.Date) != DateOnly.FromDateTime(current.Date) && (!same || discontinuous))
                _dateNeedsReview = true;
            if (discontinuous || !same || !oldFresh) Reset("学校时间重新校验中");
            if (delta > 0) _lastAdvance = elapsed;
            else if (elapsed - _lastAdvance >= TimeSpan.FromSeconds(2)) Reset("学校时间疑似冻结");
        }
        else { Reset("学校时间重新校验中"); _lastAdvance = elapsed; }
        _frame = frame; _received = elapsed;
        if (frame.AgeMs >= SchoolClockFrame.MaxAgeMs || frame.State != "Advancing")
        {
            Reset(frame.State switch
            {
                "FrozenSuspected" => "学校时间疑似冻结", "Discontinuous" => "学校时间发生跳变，重新校验中",
                "Stale" => "学校时间样本已陈旧", _ => "学校时间重新校验中"
            });
            return;
        }
        if (_stableSamples == 0) _stableSince = elapsed;
        if (advanced || _stableSamples == 0) _stableSamples++;
        _healthy = _stableSamples >= 3 && elapsed - _stableSince >= TimeSpan.FromMilliseconds(500) &&
            elapsed - _lastAdvance < TimeSpan.FromSeconds(2);
        _message = _healthy ? "使用 ClassIsland 学校时间" : "学校时间重新校验中";
    }

    public void Unavailable(string message, TimeSpan elapsed)
    {
        // Keep last calendar value for clearly labelled historical display, never as a fallback clock.
        if (_frame is not null) _frame = _frame with { AgeMs = SchoolClockFrame.MaxAgeMs, State = "Unavailable", Schedule = null };
        _received = elapsed; Reset(message);
    }
    public void ConfirmDate() => _dateNeedsReview = false;
    private void Reset(string message) { _healthy = false; _stableSamples = 0; _message = message; }
    public SchoolClockReading Read(TimeSpan elapsed)
    {
        double age = _frame is null ? SchoolClockFrame.MaxAgeMs : _frame.AgeMs + (elapsed - _received).TotalMilliseconds;
        bool fresh = _frame?.SchoolNow is not null && _frame.State != "Unavailable" && age < SchoolClockFrame.MaxAgeMs;
        return new(_frame?.SchoolNow, age, fresh, fresh && _healthy && !_dateNeedsReview,
            _dateNeedsReview ? "学校日期发生跳变，请核对课表后重新启动预演" : !fresh ? "学校时间已断开或陈旧，等待新鲜样本" : _message,
            _dateNeedsReview);
    }
}

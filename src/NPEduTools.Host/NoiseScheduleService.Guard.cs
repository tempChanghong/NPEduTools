using NPEduTools.Core;

namespace NPEduTools.Host;

public sealed partial class NoiseScheduleService
{
    /// <summary>No identity, credential, microphone configuration or capture lease is persisted here.
    /// Convert school wall fields using a fresh paired school/UTC observation, not ToUniversalTime().</summary>
    public GuardWakePlan GuardPlan(Guid generation, GuardProcess host)
    {
        lock (_sync)
        {
            var utc = _time.GetUtcNow();
            var reading = _tracker.Read(TimeSpan.FromMilliseconds(Elapsed));
            var hints = new List<GuardWakeWindow>();
            DateTimeOffset? suppress = null;
            // Exit suppression remains meaningful while capture is paused or already stopping.
            // It is not a wake authorization, so do not gate it on the current exam/capture state.
            if (reading.CanStart && reading.Now is { Year: >= 2000 and <= 9998 } observed)
            {
                var w = ScheduleWindows(observed);
                var current = w.Current ?? (w.Next?.Start.AddMinutes(-2) <= observed ? w.Next : null);
                if (current is not null) suppress = utc + (current.End - observed);
            }
            bool paused = _exam();
            if (_eligible && _scope is not null && _storeError is null && reading.CanStart &&
                LeaseRemaining(Elapsed) > 0 && reading.Now is { Year: >= 2000 and <= 9998 } schoolNow &&
                (_studySelected ? _studySettings is not null : _policy.Source is not ("None" or "Disabled")))
            {
                var cursor = schoolNow;
                for (int i = 0; i < 16; i++)
                {
                    var windows = ScheduleWindows(cursor);
                    var window = windows.Current ?? windows.Next;
                    if (window is null || window.End - schoolNow > TimeSpan.FromDays(399)) break;
                    var start = utc + (window.Start - schoolNow); var end = utc + (window.End - schoolNow);
                    if (window.Start.AddMinutes(-2) <= schoolNow && schoolNow < window.End) suppress = end;
                    bool skipped;
                    lock (_journalLock) skipped = _journal.Blocks.Any(b => b.Kind == "Skipped" && SchoolNoiseSchedule.Overlaps(b.Window, window));
                    // A current exam blocks current/preparing wake, but future slots remain launch hints.
                    if (!skipped && (!paused || window.Start.AddMinutes(-2) > schoolNow)) hints.Add(new(start, end));
                    cursor = window.End;
                }
            }
            return new(generation, host, utc, hints, suppress);
        }
    }
}

using System.Diagnostics;

namespace NPEduTools.Contracts;

/// <summary>Absolute Windows monotonic timestamps, shared by controller and worker; never wall-clock instants.</summary>
public sealed class RecorderDeadline
{
    private readonly object _gate = new();
    private RecorderControl? _control;
    public RecorderControl? Current { get { lock (_gate) return _control; } }
    public static long After(TimeSpan duration) => checked(Stopwatch.GetTimestamp() + (long)(duration.TotalSeconds * Stopwatch.Frequency));
    public static double SecondsUntil(long timestamp) => (timestamp - Stopwatch.GetTimestamp()) / (double)Stopwatch.Frequency;
    public bool Accept(RecorderControl value, bool start, long now)
    {
        lock (_gate)
        {
            if (value.Owner is not ("Manual" or "Automatic") || value.SessionId == Guid.Empty || value.OccurrenceId is null or { Length: > 256 } ||
                value.LeaseDeadline <= now || (value.LeaseDeadline - (double)now) / Stopwatch.Frequency > 12 ||
                (value.Owner == "Automatic" && (string.IsNullOrEmpty(value.OccurrenceId) || value.HardDeadline <= now ||
                    (value.HardDeadline - (double)now) / Stopwatch.Frequency > 86400)) ||
                value.Owner == "Manual" && value.HardDeadline != 0) return false;
            if (start) { if (_control is not null) return false; _control = value; return true; }
            if (_control is null || !_control.Matches(value)) return false;
            // An expired lease cannot be resurrected by a delayed heartbeat.
            if (Expired(now)) return false;
            _control = value with { HardDeadline = value.Owner == "Automatic" ? Math.Min(value.HardDeadline, _control.HardDeadline) : 0,
                LeaseDeadline = Math.Max(_control.LeaseDeadline, value.LeaseDeadline) };
            return true;
        }
    }
    public bool Expired(long now)
    {
        lock (_gate) return _control is { } c && (now >= c.LeaseDeadline || c.HardDeadline != 0 && now >= c.HardDeadline);
    }
}

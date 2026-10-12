using System.Diagnostics;
using System.Threading.Channels;

namespace NPEduTools.PowerPoint.Diagnostics;

/// <summary>Bounded input handoff. Window hit testing belongs to the consumer, never the global hook.</summary>
internal sealed class TouchInputBuffer
{
    private readonly record struct Observation(long TimeMs, string Kind, int X, int Y, ulong ExtraInfo, uint Flags, ShowTarget? Target);
    private readonly Channel<Observation> _events = Channel.CreateBounded<Observation>(2048);
    private bool _tracking;
    private long _dropped;
    public long Dropped => Interlocked.Read(ref _dropped);
    public void Discard() { while (_events.Reader.TryRead(out _)) { } }
    // MSLLHOOKSTRUCT.time wraps every 49.7 days; preserve the original event age even
    // when hook dispatch is delayed rather than timestamping an old tap as new.
    public static long EventTime(long nowMs, uint eventTime) => nowMs - unchecked((uint)nowMs - eventTime);

    public void Observe(nuint message, int x, int y, ulong extraInfo, uint flags, long timeMs, SnapshotFrame frame, long foreground)
    {
        string? kind = message switch { 0x201 => "Down", 0x202 => "Up", 0x200 when _tracking => "Move", 0x204 => "RightDown", _ => null };
        if (kind is null) return;
        ShowTarget? target = null;
        if (Stopwatch.GetElapsedTime(frame.ReceivedAt).TotalMilliseconds <= 750 && frame.Snapshot.Status == "Showing" && frame.Snapshot.Windows.Length == 1)
        {
            var show = frame.Snapshot.Windows[0];
            if (show.Hwnd == foreground && x >= show.Left && x < show.Right && y >= show.Top && y < show.Bottom) target = show;
        }
        bool wasTracking = _tracking;
        if (kind == "Down" && target is not null) _tracking = true;
        if (kind is "Up" or "RightDown" || target is null) _tracking = false;
        if (target is null && !wasTracking) return;
        // Do not retain coordinates/source information for input outside the show.
        var sample = new Observation(timeMs, target is null ? "Cancel" : kind, target is null ? 0 : x, target is null ? 0 : y,
            target is null ? 0 : extraInfo, target is null ? 0 : flags, target);
        if (!_events.Writer.TryWrite(sample)) Interlocked.Increment(ref _dropped);
    }

    public bool TryRead(long nowMs, Func<ShowTarget, int, int, bool> hitTest, out InputSample? input)
    {
        input = null;
        if (!_events.Reader.TryRead(out var observed)) return false;
        var target = observed.Target;
        long age = nowMs - observed.TimeMs;
        if (target is not null && (age is < 0 or > 500 || !hitTest(target, observed.X, observed.Y))) target = null;
        input = new(observed.TimeMs, target is null ? "Cancel" : observed.Kind, target is null ? 0 : observed.X,
            target is null ? 0 : observed.Y, target is null ? 0 : observed.ExtraInfo, target is null ? 0 : observed.Flags, target);
        return true;
    }
}

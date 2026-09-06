namespace NPEduTools.PowerPoint.Diagnostics;

public sealed record AssistTap(ShowTarget Before, int X, int Y, long ReleasedAt);

/// <summary>PTA-style click/edge/menu rules. No screen-wide input suppression.</summary>
public sealed class TouchAssistGesture
{
    private InputSample? _down;
    private bool _blocked;
    private int _skip;
    public bool AllowUnmarkedMouse { get; set; }

    public void Reset() { _down = null; _blocked = false; _skip = 0; }
    public static bool Ready(ShowTarget target) => target.FullScreen && target.State == 1 && target.PointerType is not (2 or 5);
    public static bool SameWindow(ShowTarget a, ShowTarget b) => a.Id == b.Id && a.Hwnd == b.Hwnd &&
        a.ProcessId == b.ProcessId && a.ProcessStartedUtcTicks == b.ProcessStartedUtcTicks;
    public static bool AlreadyChanged(ShowTarget a, ShowTarget b) => !SameWindow(a, b) || a.SlideId != b.SlideId || a.ClickIndex != b.ClickIndex;
    public static bool InContent(ShowTarget target, int x, int y)
    {
        double border = 95 * Math.Max(96, target.Dpi) / 96.0;
        return x >= target.Left + border && x < target.Right - border && y >= target.Top && y < target.Bottom - border;
    }

    private bool Allowed(InputSample input)
    {
        bool marked = (input.ExtraInfo & 0xffffff00UL) == 0xff515700UL;
        if (marked) return (input.ExtraInfo & 0x80) != 0; // Includes promoted touch marked as injected by some drivers.
        return AllowUnmarkedMouse && (input.Flags & 3) == 0;
    }

    public AssistTap? Accept(InputSample input)
    {
        if (input.Kind == "RightDown") { _down = null; _skip = 1; return null; }
        if (input.Target is not { } target || input.Kind == "Cancel") { _down = null; return null; }
        if (input.Kind == "Down")
        {
            _down = input;
            bool edge = !InContent(target, input.X, input.Y);
            if (edge) _skip = 2; // This edge tap plus the next tap used to dismiss/operate the menu.
            _blocked = edge || !Allowed(input) || !Ready(target);
            return null;
        }
        if (_down is not { Target: { } before } down) return null;
        double scale = 96.0 / Math.Max(96, before.Dpi);
        double dx = ((double)input.X - down.X) * scale, dy = ((double)input.Y - down.Y) * scale;
        if (dx * dx + dy * dy > 144 || !SameWindow(before, target) || !Allowed(input) || !Ready(target)) _blocked = true;
        if (input.Kind != "Up") return null;
        _down = null;
        if (_skip > 0) { _skip--; return null; }
        if (_blocked || input.TimeMs - down.TimeMs is < 0 or > 450 || !InContent(target, input.X, input.Y)) return null;
        return new(before, input.X, input.Y, input.TimeMs);
    }
}

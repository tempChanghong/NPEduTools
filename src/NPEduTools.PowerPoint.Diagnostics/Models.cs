namespace NPEduTools.PowerPoint.Diagnostics;

public sealed record ShowTarget(string Id, long Hwnd, int ProcessId, long ProcessStartedUtcTicks,
    int Left, int Top, int Right, int Bottom, uint Dpi, bool FullScreen,
    int SlideId, int SlideIndex, int ClickIndex, int ClickCount, int PointerType, int State,
    SlideFeatures? Features = null);

public sealed record SlideFeatures(DateTimeOffset At, string Coverage, int ShapeCount, int ActionShapes,
    int Hyperlinks, int InteractiveSequences, int MediaShapes, int GroupShapes, string? Error = null);

public sealed record ShowSnapshot(DateTimeOffset At, string Status, string? OfficeVersion = null,
    ShowTarget[]? Targets = null, string? Error = null, string? OfficeBuild = null)
{
    [System.Text.Json.Serialization.JsonIgnore]
    public ShowTarget[] Windows => Targets ?? [];
}

public sealed record InputSample(long TimeMs, string Kind, int X, int Y, ulong ExtraInfo, uint Flags,
    ShowTarget? Target);

public sealed record TapResult(string Outcome, string Source, string TargetId, long DurationMs,
    double MaxDistanceDip, int SlideId, int ClickIndex);

public static class InputSource
{
    // This is the Windows compatibility-mouse signature, not proof of the physical device type.
    public static string Classify(ulong extraInfo, uint flags)
    {
        if ((flags & 3) != 0) return "Injected";
        if ((extraInfo & 0xffffff00UL) != 0xff515700UL) return "MouseOrUnmarked";
        return (extraInfo & 0x80) != 0 ? "TouchMarked" : "PenMarked";
    }
}

/// <summary>Diagnostic candidates only. This class never authorizes a PowerPoint mutation.</summary>
public sealed class TapTracker
{
    private InputSample? _start;
    private double _maxDistance;
    private string? _rejected;

    public TapResult? Cancel(string reason)
    {
        if (_start is not { Target: { } target } start) return null;
        var result = new TapResult(reason, InputSource.Classify(start.ExtraInfo, start.Flags), target.Id,
            0, _maxDistance, target.SlideId, target.ClickIndex);
        _start = null;
        return result;
    }

    public TapResult? Accept(InputSample input)
    {
        if (input.Kind == "Cancel" || input.Target is null) return Cancel("TargetLost");
        if (input.Kind == "RightDown") return Cancel("OtherButton");
        if (input.Kind == "Down")
        {
            var interrupted = Cancel("OverlappingDown");
            _start = input;
            _maxDistance = 0;
            _rejected = null;
            return interrupted;
        }
        if (_start is not { Target: { } target } start) return null;
        string source = InputSource.Classify(start.ExtraInfo, start.Flags);
        if (input.Target.Id != target.Id || input.Target.Hwnd != target.Hwnd || input.Target.SlideId != target.SlideId ||
            input.Target.ProcessId != target.ProcessId || input.Target.ProcessStartedUtcTicks != target.ProcessStartedUtcTicks)
            _rejected = "TargetChanged";
        if (InputSource.Classify(input.ExtraInfo, input.Flags) != source) _rejected = "SourceChanged";
        double dx = ((double)input.X - start.X) * 96 / Math.Max(96, target.Dpi);
        double dy = ((double)input.Y - start.Y) * 96 / Math.Max(96, target.Dpi);
        _maxDistance = Math.Max(_maxDistance, Math.Sqrt(dx * dx + dy * dy));
        if (input.Kind != "Up") return null;
        long duration = input.TimeMs - start.TimeMs;
        string outcome = _rejected ?? (duration is < 0 or > 450 ? "LongOrInvalidDuration" :
            _maxDistance > 12 ? "Moved" : source != "TouchMarked" ? "NotTouchMarked" : "TouchTapCandidate");
        _start = null;
        return new(outcome, source, target.Id, duration, _maxDistance, target.SlideId, target.ClickIndex);
    }
}

using NPEduTools.PowerPoint.Diagnostics;

namespace NPEduTools.Tests;

public sealed class PowerPointInputTests
{
    private static readonly ShowTarget Target = new("show-1", 123, 42, 1, -1920, 0, 0, 1080, 144, true, 256, 1, 0, 2, 1, 1);
    private static InputSample Sample(string kind, long time, int x = -100, int y = 100, ulong extra = 0xff515780, uint flags = 0, ShowTarget? target = null)
        => new(time, kind, x, y, extra, flags, target ?? Target);

    [Theory]
    [InlineData(0UL, 0U, "MouseOrUnmarked")]
    [InlineData(0xff515780UL, 0U, "TouchMarked")]
    [InlineData(0xff515701UL, 0U, "PenMarked")]
    [InlineData(0xff515780UL, 1U, "Injected")]
    [InlineData(0UL, 2U, "Injected")]
    public void InputSignatureDoesNotConfusePenOrInjectedEventsWithTouch(ulong extra, uint flags, string expected)
        => Assert.Equal(expected, InputSource.Classify(extra, flags));

    [Fact]
    public void TouchJitterUsesMonitorDpiAndProducesOnlyOneCandidate()
    {
        var tracker = new TapTracker();
        tracker.Accept(Sample("Down", 10));
        tracker.Accept(Sample("Move", 50, -88));
        var result = tracker.Accept(Sample("Up", 110, -91));
        Assert.Equal("TouchTapCandidate", result?.Outcome);
        Assert.Equal(8, result!.MaxDistanceDip);
        Assert.Null(tracker.Accept(Sample("Up", 120)));
    }

    [Fact]
    public void DragReturningToStartIsStillRejected()
    {
        var tracker = new TapTracker();
        tracker.Accept(Sample("Down", 0));
        tracker.Accept(Sample("Move", 20, -50));
        Assert.Equal("Moved", tracker.Accept(Sample("Up", 100))?.Outcome);
    }

    [Theory]
    [InlineData(451)]
    [InlineData(-1)]
    public void LongPressOrInvalidTimeIsRejected(long elapsed)
    {
        var tracker = new TapTracker();
        tracker.Accept(Sample("Down", 0));
        Assert.Equal("LongOrInvalidDuration", tracker.Accept(Sample("Up", elapsed))?.Outcome);
    }

    [Fact]
    public void MouseClickIsNeverTouchCandidate()
    {
        var tracker = new TapTracker();
        tracker.Accept(Sample("Down", 0, extra: 0));
        Assert.Equal("NotTouchMarked", tracker.Accept(Sample("Up", 100, extra: 0))?.Outcome);
    }

    [Fact]
    public void TargetChangeCannotBeUndoneByReturningToOriginalTarget()
    {
        var tracker = new TapTracker();
        tracker.Accept(Sample("Down", 0));
        tracker.Accept(Sample("Move", 50, target: Target with { Id = "another-show" }));
        Assert.Equal("TargetChanged", tracker.Accept(Sample("Up", 100))?.Outcome);
    }

    [Fact]
    public void NativeSlideAdvanceDuringTouchIsReportedInsteadOfAnotherCandidate()
    {
        var tracker = new TapTracker();
        tracker.Accept(Sample("Down", 0));
        Assert.Equal("TargetChanged", tracker.Accept(Sample("Up", 100, target: Target with { SlideId = 257 }))?.Outcome);
    }

    [Fact]
    public void ReusedWindowWithDifferentProcessLifetimeIsRejected()
    {
        var tracker = new TapTracker();
        tracker.Accept(Sample("Down", 0));
        Assert.Equal("TargetChanged", tracker.Accept(Sample("Up", 100, target: Target with { ProcessStartedUtcTicks = 2 }))?.Outcome);
    }

    [Theory]
    [InlineData("InputGap")]
    [InlineData("SnapshotUnavailable")]
    [InlineData("SessionEnded")]
    public void CancellationDiscardsIncompleteGesture(string reason)
    {
        var tracker = new TapTracker();
        tracker.Accept(Sample("Down", 0));
        Assert.Equal(reason, tracker.Cancel(reason)?.Outcome);
        Assert.Null(tracker.Accept(Sample("Up", 100)));
    }

    [Fact]
    public void RightButtonAndSourceChangesRejectCandidate()
    {
        var tracker = new TapTracker();
        tracker.Accept(Sample("Down", 0));
        Assert.Equal("OtherButton", tracker.Accept(Sample("RightDown", 20))?.Outcome);
        Assert.Null(tracker.Accept(Sample("Up", 100)));
        tracker.Accept(Sample("Down", 200));
        Assert.Equal("SourceChanged", tracker.Accept(Sample("Up", 250, extra: 0xff515700))?.Outcome);
    }
}

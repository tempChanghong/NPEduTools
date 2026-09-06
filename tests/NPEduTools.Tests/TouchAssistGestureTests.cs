using NPEduTools.PowerPoint.Diagnostics;

namespace NPEduTools.Tests;

public sealed class TouchAssistGestureTests
{
    private static ShowTarget Target => new("show", 1, 2, 3, 0, 0, 1920, 1080, 96, true, 256, 1, 0, 2, 1, 1);
    private static InputSample Sample(string kind, long time = 0, int x = 500, int y = 500,
        ulong source = 0xff515780, uint flags = 0, ShowTarget? target = null) => new(time, kind, x, y, source, flags, target ?? Target);
    private static AssistTap? Tap(TouchAssistGesture gesture, int x = 500, ulong source = 0xff515780, uint flags = 0)
    { gesture.Accept(Sample("Down", x: x, source: source, flags: flags)); return gesture.Accept(Sample("Up", 100, x, source: source, flags: flags)); }

    [Fact] public void MarkedTouchIncludingPromotedInjectedTouchIsAccepted()
    { Assert.NotNull(Tap(new())); Assert.NotNull(Tap(new(), flags: 1)); }

    [Fact] public void MouseIsIgnoredUnlessCompatibilityIsExplicitlyEnabled()
    { Assert.Null(Tap(new(), source: 0)); Assert.NotNull(Tap(new() { AllowUnmarkedMouse = true }, source: 0)); }

    [Theory]
    [InlineData(0xff515700UL, 0U)]
    [InlineData(0UL, 1U)]
    [InlineData(0UL, 2U)]
    public void PenAndUnmarkedInjectionStayIgnored(ulong source, uint flags)
    { Assert.Null(Tap(new() { AllowUnmarkedMouse = true }, source: source, flags: flags)); }

    [Fact] public void EdgeTapAlsoSkipsTheNextMenuDismissalTap()
    { var g = new TouchAssistGesture(); Assert.Null(Tap(g, 30)); Assert.Null(Tap(g)); Assert.NotNull(Tap(g)); }

    [Fact] public void RightClickSkipsOneTap()
    { var g = new TouchAssistGesture(); g.Accept(Sample("RightDown")); Assert.Null(Tap(g)); Assert.NotNull(Tap(g)); }

    [Fact] public void DragReturningToStartIsStillRejected()
    {
        var g = new TouchAssistGesture(); g.Accept(Sample("Down"));
        g.Accept(Sample("Move", 40, 530)); Assert.Null(g.Accept(Sample("Up", 100)));
    }

    [Theory]
    [InlineData(451, 503, false)]
    [InlineData(100, 503, true)]
    [InlineData(-1, 500, false)]
    public void TapAllowsSmallJitterButRejectsHoldAndInvalidTime(long elapsed, int x, bool accepted)
    {
        var g = new TouchAssistGesture(); g.Accept(Sample("Down"));
        Assert.Equal(accepted, g.Accept(Sample("Up", elapsed, x)) is not null);
    }

    [Fact] public void WindowChangeOrPenCancelsTap()
    {
        foreach (var target in new[] { Target with { Hwnd = 9 }, Target with { PointerType = 2 }, Target with { State = 2 } })
        { var g = new TouchAssistGesture(); g.Accept(Sample("Down")); Assert.Null(g.Accept(Sample("Up", 100, target: target))); }
    }

    [Fact] public void ScaledNegativeMonitorCoordinatesUseTheShowBounds()
    {
        var target = Target with { Left = -3840, Right = 0, Bottom = 2160, Dpi = 192 };
        Assert.True(TouchAssistGesture.InContent(target, -2000, 500));
        Assert.False(TouchAssistGesture.InContent(target, -3800, 500));
        Assert.False(TouchAssistGesture.InContent(target, -2000, 2000));
    }

    [Fact] public void NativeAnimationOrSlideAdvanceCancelsSupplement()
    {
        Assert.False(TouchAssistGesture.AlreadyChanged(Target, Target));
        Assert.True(TouchAssistGesture.AlreadyChanged(Target, Target with { ClickIndex = 1 }));
        Assert.True(TouchAssistGesture.AlreadyChanged(Target, Target with { SlideId = 257 }));
        Assert.True(TouchAssistGesture.AlreadyChanged(Target, Target with { ProcessStartedUtcTicks = 4 }));
    }

    [Fact] public void GeneralAnimationsAndInteractiveSlidesAreNotExperimentalStepGated()
    { Assert.True(TouchAssistGesture.Ready(Target with { Features = new(DateTimeOffset.UtcNow, "TopLevelOnly", 1, 1, 1, 1, 1, 1) })); }
}

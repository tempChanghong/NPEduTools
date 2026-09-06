namespace NPEduTools.PowerPoint.Diagnostics;

public sealed record StepPlan(string Action, string Reason, int? Click = null, int? NextSlideId = null)
{
    public static StepPlan Decide(ShowTarget target, bool simpleSequence, bool standardShow,
        int slideCount, int? nextSlideId, bool nextSlideSupported)
    {
        if (!target.FullScreen || target.State != 1 || target.PointerType is not (1 or 4)) return new("Refuse", "ShowNotInArrowMode");
        if (!standardShow) return new("Refuse", "CustomShowUnsupported");
        if (target.Features is not { Coverage: "TopLevelOnly", ActionShapes: 0, Hyperlinks: 0,
            InteractiveSequences: 0, MediaShapes: 0, GroupShapes: 0 }) return new("Refuse", "InteractiveOrUnknownSlide");
        if (!simpleSequence || target.ClickIndex < 0 || target.ClickCount < 0 || target.ClickIndex > target.ClickCount)
            return new("Refuse", "AnimationSequenceUnsupported");
        if (target.ClickIndex < target.ClickCount) return new("Animation", "NextClick", target.ClickIndex + 1);
        if (target.SlideIndex == slideCount) return new("None", "EndOfShow");
        if (target.SlideIndex < 1 || target.SlideIndex > slideCount || nextSlideId is null || !nextSlideSupported)
            return new("Refuse", "NextSlideUnsupported");
        return new("Slide", "NextSlide", NextSlideId: nextSlideId);
    }

    public static bool SameState(ShowTarget expected, ShowTarget current) => expected.Id == current.Id &&
        expected.Hwnd == current.Hwnd && expected.ProcessId == current.ProcessId &&
        expected.ProcessStartedUtcTicks == current.ProcessStartedUtcTicks && expected.SlideId == current.SlideId &&
        expected.ClickIndex == current.ClickIndex && expected.ClickCount == current.ClickCount &&
        expected.State == current.State && expected.PointerType == current.PointerType && expected.FullScreen == current.FullScreen;

    public bool MatchesResult(ShowTarget before, ShowTarget after) => before.Id == after.Id && before.Hwnd == after.Hwnd &&
        before.ProcessId == after.ProcessId && before.ProcessStartedUtcTicks == after.ProcessStartedUtcTicks &&
        (Action == "Animation" ? after.SlideId == before.SlideId && after.ClickIndex == Click :
            Action == "Slide" && after.SlideId == NextSlideId && after.ClickIndex == 0);
}

public sealed record StepResult(Guid RequestId, DateTimeOffset At, string Outcome, string Reason,
    StepPlan? Plan = null, ShowTarget? Before = null, ShowTarget? After = null);

using NPEduTools.PowerPoint.Diagnostics;

namespace NPEduTools.Tests;

public sealed class PowerPointStepTests
{
    private static ShowTarget Target => new("show", 1, 2, 3, 0, 0, 1920, 1080, 96, true,
        256, 1, 0, 2, 4, 1, new(DateTimeOffset.UtcNow, "TopLevelOnly", 2, 0, 0, 0, 0, 0));
    private static StepPlan Plan(ShowTarget target) => StepPlan.Decide(target, true, true, 5, 257, true);

    [Fact]
    public void AnimationsAreConsumedBeforeChangingSlide()
    {
        Assert.Equal(new StepPlan("Animation", "NextClick", 1), Plan(Target));
        Assert.Equal(new StepPlan("Animation", "NextClick", 2), Plan(Target with { ClickIndex = 1 }));
        Assert.Equal(new StepPlan("Slide", "NextSlide", NextSlideId: 257), Plan(Target with { ClickIndex = 2 }));
    }

    [Fact]
    public void LastSlideDoesNotExitShow()
    {
        var plan = StepPlan.Decide(Target with { SlideIndex = 5, ClickIndex = 2 }, true, true, 5, null, false);
        Assert.Equal("None", plan.Action);
        Assert.Equal("EndOfShow", plan.Reason);
    }

    [Theory]
    [InlineData("pen")]
    [InlineData("paused")]
    [InlineData("windowed")]
    [InlineData("links")]
    [InlineData("trigger")]
    [InlineData("unknown")]
    public void UnsupportedOrInteractiveStateIsRefused(string mode)
    {
        var target = mode switch
        {
            "pen" => Target with { PointerType = 2 },
            "paused" => Target with { State = 2 },
            "windowed" => Target with { FullScreen = false },
            "links" => Target with { Features = Target.Features! with { Hyperlinks = 1 } },
            "trigger" => Target with { Features = Target.Features! with { InteractiveSequences = 1 } },
            _ => Target with { Features = null }
        };
        Assert.Equal("Refuse", Plan(target).Action);
    }

    [Fact]
    public void ComplexAnimationAndCustomShowAreRefused()
    {
        Assert.Equal("Refuse", StepPlan.Decide(Target, false, true, 5, 257, true).Action);
        Assert.Equal("Refuse", StepPlan.Decide(Target, true, false, 5, 257, true).Action);
        Assert.Equal("Refuse", Plan(Target with { ClickIndex = -1 }).Action);
        Assert.Equal("Refuse", Plan(Target with { ClickIndex = 3 }).Action);
    }

    [Fact]
    public void StateComparisonIncludesProcessLifetimeAndClickPosition()
    {
        Assert.True(StepPlan.SameState(Target, Target));
        Assert.False(StepPlan.SameState(Target, Target with { ClickIndex = 1 }));
        Assert.False(StepPlan.SameState(Target, Target with { ProcessStartedUtcTicks = 4 }));
        Assert.False(StepPlan.SameState(Target, Target with { Id = "reopened" }));
    }

    [Fact]
    public void VerificationRequiresExactlyExpectedAnimationOrSlide()
    {
        var animation = Plan(Target);
        Assert.True(animation.MatchesResult(Target, Target with { ClickIndex = 1 }));
        Assert.False(animation.MatchesResult(Target, Target with { ClickIndex = 2 }));
        var before = Target with { ClickIndex = 2 };
        var slide = Plan(before);
        Assert.True(slide.MatchesResult(before, Target with { SlideId = 257, SlideIndex = 2 }));
        Assert.False(slide.MatchesResult(before, Target with { SlideId = 258 }));
    }

    [Fact]
    public void UnsupportedNextSlideDoesNotPreventRemainingAnimation()
    {
        Assert.Equal("Animation", StepPlan.Decide(Target, true, true, 5, 257, false).Action);
        Assert.Equal("Refuse", StepPlan.Decide(Target with { ClickIndex = 2 }, true, true, 5, 257, false).Action);
    }
}

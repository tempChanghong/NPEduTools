using NPEduTools.App;
using NPEduTools.Contracts;

namespace NPEduTools.Tests;

public sealed class StudySchedulePresentationTests
{
    private static readonly SchoolNoiseWindow Current = new(new(2026, 10, 12, 19, 0, 0, TimeSpan.Zero), new(2026, 10, 12, 21, 30, 0, TimeSpan.Zero));
    private static readonly SchoolNoiseWindow NextWindow = new(Current.Start.AddDays(1), Current.End.AddDays(1));
    private static StudyHostState State => new("v", "APPLIED", "NOT_INTEGRATED", "MANUAL", "Active", "WINDOW_ACTIVE", true,
        Current.Start, Current, NextWindow, null);

    [Fact]
    public void Current_or_manual_capture_does_not_hide_next_school_window()
    {
        var text = StudySchedulePresentation.Next(State);
        Assert.Contains("2026-10-13 19:00–21:30", text);
        Assert.Contains("ClassIsland", text);
        Assert.DoesNotContain("2026-10-12", text);
        Assert.Contains("Windows", StudySchedulePresentation.Next(State with { Mode = "Exam", ClockSource = "Windows" }));
    }

    [Fact]
    public void Unknown_stale_disabled_and_empty_mode_do_not_reuse_a_window()
    {
        Assert.DoesNotContain("2026", StudySchedulePresentation.Next(null));
        Assert.DoesNotContain("2026", StudySchedulePresentation.Next(State with { PolicyState = "UNAVAILABLE" }));
        Assert.DoesNotContain("2026", StudySchedulePresentation.Next(State with { ClockReady = false }));
        Assert.Contains("未启用", StudySchedulePresentation.Next(State with { Phase = "Disabled" }));
        Assert.Contains("考试模式没有后续时段", StudySchedulePresentation.Next(State with { Next = null, Mode = "Exam" }));
        Assert.Contains("当前暂停", StudySchedulePresentation.Next(State with { Reason = "EXAM_PAUSED" }));
    }

    [Fact]
    public void Overnight_window_includes_end_date_without_timezone_conversion()
    {
        Assert.Equal("2026-10-12 23:00–2026-10-13 01:00", StudySchedulePresentation.Span(new(Current.Start.AddHours(4), Current.Start.AddHours(6))));
    }
}

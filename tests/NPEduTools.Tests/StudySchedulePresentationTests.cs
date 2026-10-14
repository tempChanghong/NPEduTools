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

    [Fact]
    public void Offline_running_window_is_explained_without_advertising_cached_next_arrangements()
    {
        var offline = State with { Reason = "OFFLINE_CONTINUING", Next = null };
        Assert.Contains("本次自习继续", StudySchedulePresentation.Next(offline));
        Assert.Contains("沿用本次已确认安排", StudySchedulePresentation.Configuration(offline));
        Assert.DoesNotContain("尚未", StudySchedulePresentation.Configuration(offline));
        Assert.DoesNotContain("2026-10-13", StudySchedulePresentation.Next(offline));
    }

    [Theory]
    [InlineData("STUDY_OFFLINE", "自动重试")]
    [InlineData("CONFIGURATION_STALE", "已过期")]
    [InlineData("NOT_ELIGIBLE", "互联")]
    [InlineData("AUTH_INVALID", "授权")]
    [InlineData("POLICY_CHANGED", "已变更")]
    public void Missing_confirmation_explains_reason_without_reusing_old_times(string reason, string expected)
    {
        var unavailable = State with { PolicyState = "UNAVAILABLE", Reason = reason };
        Assert.Contains(expected, StudySchedulePresentation.Next(unavailable));
        Assert.Contains(expected, StudySchedulePresentation.Configuration(unavailable));
        Assert.DoesNotContain("2026", StudySchedulePresentation.Next(unavailable));
        Assert.DoesNotContain("2026", StudySchedulePresentation.Configuration(unavailable));
    }

    [Fact]
    public void Received_configuration_is_visible_even_when_school_time_is_unavailable()
    {
        var settings = new StudySettings(new("Asia/Shanghai", "2026-10-12", null,
            [new([1, 3, 5], "19:15", "21:30")], []), new("StatusTrend", 180), new(false, "OwnClass", [], 60),
            new(true, null, null, 600, 3), new(false, 3, 30, 30, true, false));
        var received = State with { Settings = settings, ClockReady = false, SchoolNow = null };
        Assert.Contains("安排已收到", StudySchedulePresentation.Next(received));
        string summary = StudySchedulePresentation.Configuration(received);
        Assert.Contains("周一、三、五 19:15–21:30", summary);
        Assert.Contains("2026-10-12", summary);
        Assert.Contains("考试模式未设置时段", StudySchedulePresentation.Configuration(received with { Mode = "Exam" }));
        Assert.Contains("学校尚未配置本班", StudySchedulePresentation.Next(received with { Phase = "Disabled", Reason = "NOT_CONFIGURED", Settings = null }));
    }
}

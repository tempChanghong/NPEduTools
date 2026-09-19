using NPEduTools.Contracts;
using NPEduTools.Core;

namespace NPEduTools.Tests;

public sealed class SchoolPreviewDeadlineTests
{
    private static readonly DateTimeOffset Start = new(2031, 4, 7, 10, 0, 0, TimeSpan.Zero);
    private static readonly Guid Profile = Guid.NewGuid();
    private static DaySchedule Day => new(Profile, Guid.NewGuid(), Guid.NewGuid(), "学校星期一", new(2031, 4, 7), "r1",
        Start, true, true, "学校时间", [new(1, Guid.NewGuid(), "数学", Start, Start.AddMinutes(40), true)]);
    private static SchoolClockReading Clock(DateTimeOffset? now, bool healthy = true, bool fresh = true, double age = 0) =>
        new(now, age, fresh, healthy, "学校时间");
    private static RecordingPreview Running(out DaySchedule day, double age = 0)
    {
        day = Day; var preview = new RecordingPreview(); preview.SetEnabled(true, Start);
        preview.Tick(day, RecordingPlanner.Build(day, preview.State.Rules), Clock(Start, age: age), TimeSpan.Zero);
        Assert.NotNull(preview.State.Active); return preview;
    }
    [Fact] public void ForwardJumpPastBusinessEndStopsImmediatelyEvenDuringWarmup()
    {
        var preview = Running(out var day);
        preview.Tick(day, [], Clock(Start.AddMinutes(46), healthy: false), TimeSpan.FromSeconds(1));
        Assert.Null(preview.State.Active); Assert.Equal("应停止", preview.State.Events.Last().Action);
    }
    [Fact] public void FreezeOrRollbackCannotRenewRemainingTime()
    {
        var preview = Running(out var day);
        for (int minute = 1; minute <= 45; minute++)
            preview.Tick(day, [], Clock(Start.AddHours(-1), healthy: false), TimeSpan.FromMinutes(minute));
        Assert.Null(preview.State.Active);
    }
    [Fact] public void NewEarlierEndTightensDeadlineAndLaterEditCannotUndoIt()
    {
        var preview = Running(out var day);
        var shorter = day with { Lessons = [day.Lessons[0] with { End = Start.AddMinutes(30) }] };
        preview.Tick(shorter, RecordingPlanner.Build(shorter, new()), Clock(Start.AddMinutes(1)), TimeSpan.FromMinutes(1));
        Assert.Equal(Start.AddMinutes(35), preview.State.Active!.Plan.End);
        preview.Tick(day, RecordingPlanner.Build(day, new()), Clock(Start.AddMinutes(2)), TimeSpan.FromMinutes(2));
        preview.Tick(null, [], Clock(null, false, false), TimeSpan.FromMinutes(35));
        Assert.Null(preview.State.Active); Assert.Null(preview.State.Events.Last().At);
    }
    [Fact] public void MeasuredSampleAgeIsDeductedFromHardDeadline()
    {
        var preview = Running(out _, 2000);
        preview.Tick(null, [], Clock(null, false, false), TimeSpan.FromSeconds(2698));
        Assert.Null(preview.State.Active);
    }
    [Fact] public void UncertainSampleAlreadyPastFormalEndCannotStartTailOnly()
    {
        var preview = new RecordingPreview(); preview.SetEnabled(true, Start);
        preview.Tick(Day, RecordingPlanner.Build(Day, new()), Clock(Start.AddMinutes(40).AddSeconds(-1), age: 2000), TimeSpan.Zero);
        Assert.Null(preview.State.Active);
    }
    [Fact] public void TodayAndWeekdayUseSchoolCalendarAndNaturallyExpireAtNextDay()
    {
        var day = Day; var preview = new RecordingPreview(); preview.Configure(new(Weekdays: 1 << (int)DayOfWeek.Monday));
        preview.SetEnabled(true, Start); preview.SkipToday(Start);
        preview.Tick(day, RecordingPlanner.Build(day, preview.State.Rules), Clock(Start), TimeSpan.Zero);
        Assert.Null(preview.State.Active); Assert.Equal(new DateOnly(2031, 4, 7), preview.State.SkipDate);
        var tomorrow = day with { Date = day.Date.AddDays(1), Lessons = [day.Lessons[0] with { Start = Start.AddDays(1), End = Start.AddDays(1).AddMinutes(40) }] };
        preview.Configure(new(Weekdays: 1 << (int)DayOfWeek.Tuesday));
        preview.Tick(tomorrow, RecordingPlanner.Build(tomorrow, preview.State.Rules), Clock(Start.AddDays(1)), TimeSpan.FromDays(1));
        Assert.NotNull(preview.State.Active);
    }
    [Fact] public void DateReviewDisarmsIdlePreviewAndCannotStartUntilConfirmed()
    {
        var preview = new RecordingPreview(); preview.SetEnabled(true, Start);
        preview.Tick(Day, RecordingPlanner.Build(Day, new()), Clock(Start, healthy: false) with { DateNeedsReview = true }, TimeSpan.Zero);
        Assert.False(preview.Enabled); Assert.Null(preview.State.Active);
    }
    [Fact] public void CrashRecoveryDoesNotInventCalendarTimestampOrRepeatCourse()
    {
        var preview = Running(out var day); var restored = new RecordingPreview(preview.State);
        Assert.Null(restored.State.Events.Last().At); Assert.Null(restored.State.Active);
        restored.SetEnabled(true, Start.AddMinutes(1));
        restored.Tick(day, RecordingPlanner.Build(day, new()), Clock(Start.AddMinutes(1)), TimeSpan.FromMinutes(1));
        Assert.Null(restored.State.Active); Assert.Single(restored.State.Events, e => e.Action == "应开始");
    }
}

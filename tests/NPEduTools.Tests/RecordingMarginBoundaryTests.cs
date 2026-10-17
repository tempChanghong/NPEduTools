using System.Text.Json;
using NPEduTools.Contracts;
using NPEduTools.Core;
using NPEduTools.Host;

namespace NPEduTools.Tests;

public sealed class RecordingMarginBoundaryTests
{
    private static readonly DateOnly Date = new(2031, 4, 7);
    private static readonly DateTimeOffset Start = new(Date.ToDateTime(new(8, 0)), TimeSpan.Zero);
    private static readonly Guid Profile = Guid.NewGuid(), Subject = Guid.NewGuid();
    private static DaySchedule Day(int breakMinutes) => new(Profile, Guid.NewGuid(), Guid.NewGuid(), "相邻课时", Date,
        "fixture", Start, true, true, "学校时间", [new(1, Subject, "数学", Start, Start.AddMinutes(45), true),
            new(2, Subject, "数学", Start.AddMinutes(45 + breakMinutes), Start.AddMinutes(90 + breakMinutes), true)]);
    private static RecordingPlanBook Book() => RecordingPlanBook.Create() with
    { Recurring = [new(Guid.NewGuid(), "本机安排", true, new(10, 10))] };

    [Theory]
    [InlineData(0)] [InlineData(5)] [InlineData(10)] [InlineData(15)] [InlineData(20)] [InlineData(25)]
    public void TenMinuteMarginsYieldBeforeTouchingEitherFormalLesson(int breakMinutes)
    {
        var plans = CalendarRecordingPlanner.Build(Book(), Date, Day(breakMinutes));
        Assert.All(plans, p => { Assert.True(p.Selected); Assert.False(p.Conflict); });
        Assert.Equal(Start.AddMinutes(-10), plans[0].Start);
        Assert.Equal(Start.AddMinutes(Math.Max(45, 35 + breakMinutes)), plans[1].Start);
        Assert.Equal(Start.AddMinutes(Math.Min(55, Math.Max(45, 35 + breakMinutes))), plans[0].End);
        Assert.True(plans[0].End <= plans[1].Start);
        Assert.True(plans[0].End >= plans[0].Lesson.End);
        Assert.True(plans[1].Start <= plans[1].Lesson.Start);
        Assert.Equal(plans[1].Lesson.End.AddMinutes(10), plans[1].End);
        var saving = new RecordingState("Saving", "上一节收尾", Control: new("Automatic", Guid.NewGuid(), plans[0].Key, 0, 0));
        Assert.True(RecordingService.CanWaitForAutomaticHandoff(saving, plans[0], plans[1], plans[1].Start, Profile, Profile));
    }

    [Fact]
    public void ExcludedNeighborStillProtectsItsFormalTimeWithTenMinuteMargins()
    {
        var day = Day(5);
        var rows = CalendarRecordingPlanner.Build(Book(), Date, day with { Lessons = [day.Lessons[0], day.Lessons[1] with { Subject = "周测" }] });
        Assert.False(rows[1].Selected); Assert.Equal(rows[1].Lesson.Start, rows[0].End);
        rows = CalendarRecordingPlanner.Build(Book(), Date, day with { Lessons = [day.Lessons[0] with { Subject = "周测" }, day.Lessons[1]] });
        Assert.False(rows[0].Selected); Assert.Equal(rows[0].Lesson.End, rows[1].Start); Assert.False(rows[1].Conflict);
    }

    [Fact]
    public void RealOverlapAndFixedTimeOverlapKeepTheLaterTaskAndExposeTheirResolution()
    {
        var day = Day(-1);
        var courses = CalendarRecordingPlanner.Build(Book(), Date, day);
        Assert.All(courses, p => Assert.False(p.Conflict)); Assert.Equal(courses[1].Start, courses[0].End);
        Assert.Contains("优先", courses[0].OverlapNotice);
        day = Day(5);
        var book = Book() with { Dated = [new(Guid.NewGuid(), "固定时段", Date, new(7, 55), new(8, 5))] };
        var rows = CalendarRecordingPlanner.Build(book, Date, day);
        var fixedRow = rows.Single(p => p.Fixed); var course = rows.Single(p => !p.Fixed && p.Lesson.Number == 1);
        Assert.False(fixedRow.Selected); Assert.Contains("本段不录", fixedRow.OverlapNotice);
        Assert.True(course.Selected); Assert.False(course.Conflict);
    }

    [Fact]
    public void EqualFormalStartStillNeedsReviewInsteadOfAnArbitraryWinner()
    {
        Assert.All(CalendarRecordingPlanner.Build(Book(), Date, Day(-45)), p =>
        { Assert.True(p.Conflict); Assert.Contains("同一开始时间", p.OverlapNotice); });
    }

    [Fact]
    public void OverlappingKnownLessonsHaveSeparateLedgerEntriesAndRehearsalSessions()
    {
        var source = Day(-5); var plans = CalendarRecordingPlanner.Build(Book(), Date, source);
        string directory = Directory.CreateTempSubdirectory("NPEduTools-overlap-ledger-").FullName;
        try
        {
            var ledger = new RecordingExecutionLedger(Path.Combine(directory, "ledger.json"));
            ledger.Add(Profile, Date, plans[0], "Recorded", "第一节完成", plans);
            Assert.True(ledger.Contains(Profile, plans[1])); // Unknown timetable keeps the conservative deduplication guard.
            Assert.False(ledger.Contains(Profile, plans[1], plans));
            ledger.Add(Profile, Date, plans[1], "Recorded", "第二节完成", plans);
            ledger.Load(); Assert.Equal(2, ledger.Document.Entries.Length);
            Assert.True(ledger.Contains(Profile, plans[1], plans));
            var moved = plans[1] with { Key = "moved", Lesson = plans[1].Lesson with { Start = plans[1].Lesson.Start.AddSeconds(1) } };
            Assert.True(ledger.Contains(Profile, moved, [moved]));
            var preview = new RecordingPreview(); preview.SetEnabled(true, Start);
            void Tick(DateTimeOffset at) => preview.Tick(source with { SampledAt = at }, plans,
                new(at, 0, true, true, "学校时间"), at - Start);
            Tick(Start); Assert.Equal(plans[0].Key, preview.State.Active!.Plan.Key);
            Tick(plans[1].Start); Assert.Equal(plans[1].Key, preview.State.Active!.Plan.Key);
            Tick(plans[1].Start.AddMinutes(1)); Assert.Equal(plans[1].End, preview.State.Active!.Plan.End);
            Assert.Equal(2, preview.State.Events.Count(e => e.Action == "应开始"));
            var late = new RecordingPreview(); late.SetEnabled(true, plans[1].Start);
            late.Tick(source with { SampledAt = plans[1].Start }, plans, new(plans[1].Start, 0, true, true, "学校时间"), TimeSpan.Zero);
            Assert.Equal(plans[1].Key, late.State.Active!.Plan.Key);
            var skip = new RecordingPreview(); skip.SetEnabled(true, Start);
            skip.Tick(source, plans, new(Start, 0, true, true, "学校时间"), TimeSpan.Zero);
            skip.SkipPlan(Profile, plans[1], Start, plans);
            Assert.Equal(plans[0].Key, skip.State.Active!.Plan.Key);
            Assert.True(skip.IsMarked(Profile, plans[1], plans));
            Assert.Equal(plans[1].Key, RecordingPlanner.FindReplacement(plans, plans[1])!.Key);
            var saving = new RecordingState("Saving", "第一节收尾", Control: new("Automatic", Guid.NewGuid(), plans[0].Key, 0, 0));
            Assert.True(RecordingService.CanWaitForAutomaticHandoff(saving, plans[0], plans[1], plans[1].Start, Profile, Profile));
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public void DateOverrideTenMinuteMarginsPersistWithoutChangingOtherPreferences()
    {
        string directory = Directory.CreateTempSubdirectory("NPEduTools-margins-").FullName;
        try
        {
            var original = Book() with { Overrides = [new(Guid.NewGuid(), Profile, Date, 1, Start, Start.AddMinutes(45), "数学", "Include", 10, 10)] };
            var store = new RecordingPlanBookStore(Path.Combine(directory, "plans.json"));
            store.Save(original);
            var saved = store.Read();
            Assert.Equal(JsonSerializer.Serialize(original), JsonSerializer.Serialize(saved));
            var rows = CalendarRecordingPlanner.Build(saved, Date, Day(5));
            Assert.Equal(rows[0].End, rows[1].Start); Assert.All(rows, p => Assert.False(p.Conflict));
            Assert.NotNull(CalendarRecordingPlanner.Validate(saved with { Overrides = [saved.Overrides[0] with { AfterMinutes = 11 }] }));
            Assert.NotNull(CalendarRecordingPlanner.Validate(saved with { Overrides = [saved.Overrides[0] with { BeforeMinutes = -1 }] }));
        }
        finally { Directory.Delete(directory, true); }
    }
}

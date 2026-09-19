using NPEduTools.Contracts;
using NPEduTools.Core;

namespace NPEduTools.Tests;

public sealed class CalendarRecordingPlannerTests
{
    private static readonly DateOnly Date = new(2031, 4, 7);
    private static readonly Guid Profile = Guid.NewGuid(), Subject = Guid.NewGuid();
    private static readonly DateTimeOffset Start = new(Date.ToDateTime(new(10, 0)), TimeSpan.Zero);
    private static DaySchedule Day(string name = "数学") => new(Profile, Guid.NewGuid(), Guid.NewGuid(), "周一", Date, "r1", Start,
        true, true, "学校时间", [new(1, Subject, name, Start, Start.AddMinutes(40), true)]);
    private static LessonDateOverride Override(string mode = "Include", int after = 5) => new(Guid.NewGuid(), Profile, Date, 1, Start, Start.AddMinutes(40), "数学", mode, 2, after);
    public static TheoryData<string> Excluded => new(RecordingPlanBook.DefaultExcluded);

    [Theory] [MemberData(nameof(Excluded))]
    public void EveryDefaultExcludedSubjectIsFiltered(string subject)
    {
        var plan = Assert.Single(CalendarRecordingPlanner.Build(RecordingPlanBook.Create(), Date, Day(subject)));
        Assert.False(plan.Selected); Assert.Contains("排除", plan.Reason);
    }
    [Theory] [InlineData("  体育 （ 室内 ） ")] [InlineData("物理（实验）")] [InlineData("化学 ( 实验 )")]
    public void ParenthesesAndSurroundingSpaceAreNormalized(string subject) =>
        Assert.False(Assert.Single(CalendarRecordingPlanner.Build(RecordingPlanBook.Create(), Date, Day(subject))).Selected);
    [Theory] [InlineData("物理")] [InlineData("音乐鉴赏拓展")] [InlineData("体育理论拓展")]
    public void NamesAreExactNotSubstringMatches(string subject) =>
        Assert.True(Assert.Single(CalendarRecordingPlanner.Build(RecordingPlanBook.Create(), Date, Day(subject))).Selected);
    [Fact] public void ExplicitSubjectAllowOverridesDefaultButDoesNotCrossProfiles()
    {
        var book = RecordingPlanBook.Create() with { Subjects = [new(Profile, Subject, true)] };
        Assert.True(Assert.Single(CalendarRecordingPlanner.Build(book, Date, Day("体育"))).Selected);
        Assert.False(Assert.Single(CalendarRecordingPlanner.Build(book, Date, Day("体育") with { ProfileId = Guid.NewGuid() })).Selected);
    }
    [Fact] public void DefaultBindingAndExplicitExclusionSurviveRename()
    {
        var book = CalendarRecordingPlanner.BindDefaultSubjects(RecordingPlanBook.Create(), Day("体育"));
        Assert.False(Assert.Single(CalendarRecordingPlanner.Build(book, Date, Day("改名后的科目"))).Selected);
        Assert.Same(book, CalendarRecordingPlanner.BindDefaultSubjects(book, Day("体育")));
    }
    [Fact] public void DateIncludeOverridesDefaultsAndDisabledRuleButCannotEnableDisabledSource()
    {
        var book = RecordingPlanBook.Create() with { Recurring = [], Overrides = [Override()] };
        Assert.True(Assert.Single(CalendarRecordingPlanner.Build(book, Date, Day("体育"))).Selected);
        Assert.False(Assert.Single(CalendarRecordingPlanner.Build(book, Date, Day("体育") with { Enabled = false })).Selected);
    }
    [Fact] public void ExclusionWinsOverMultipleRecurringRules()
    {
        var book = RecordingPlanBook.Create(); book = book with { Recurring = [book.Recurring[0], book.Recurring[0] with { Id = Guid.NewGuid(), Name = "第二规则" }], Overrides = [Override("Exclude")] };
        var row = Assert.Single(CalendarRecordingPlanner.Build(book, Date, Day()));
        Assert.False(row.Selected); Assert.Equal("单日明确不录", row.Reason);
    }
    [Fact] public void SameLessonAggregatesRulesAndDifferentMarginsNeedReviewUnlessDateOverrides()
    {
        var book = RecordingPlanBook.Create(); var second = book.Recurring[0] with { Id = Guid.NewGuid(), Name = "第二规则" };
        book = book with { Recurring = [book.Recurring[0], second] };
        var plan = Assert.Single(CalendarRecordingPlanner.Build(book, Date, Day()));
        Assert.True(plan.Selected); Assert.False(plan.Conflict); Assert.Contains("第二规则", plan.Sources);
        book = book with { Recurring = [book.Recurring[0], second with { Filter = new(AfterMinutes: 3) }] };
        Assert.True(Assert.Single(CalendarRecordingPlanner.Build(book, Date, Day())).Conflict);
        book = book with { Overrides = [Override(after: 1)] };
        plan = Assert.Single(CalendarRecordingPlanner.Build(book, Date, Day())); Assert.False(plan.Conflict); Assert.Equal(Start.AddMinutes(41), plan.End);
    }
    [Fact] public void MovedOrMissingCourseDoesNotSilentlyRetargetDateOverride()
    {
        var book = RecordingPlanBook.Create() with { Overrides = [Override()] };
        var day = Day(); day = day with { Lessons = [day.Lessons[0] with { Start = Start.AddHours(1), End = Start.AddHours(2) }] };
        var row = Assert.Single(CalendarRecordingPlanner.Build(book, Date, day)); Assert.True(row.Conflict); Assert.False(row.Selected);
        row = Assert.Single(CalendarRecordingPlanner.Build(book, Date, day with { Lessons = [] })); Assert.Contains("不存在", row.Reason);
    }
    [Fact] public void OnlyExplicitSuppressesRecurringButKeepsDateIncludesAndDateFixedWindows()
    {
        var book = RecordingPlanBook.Create() with { Days = [new(Date, Profile, true)] };
        Assert.False(Assert.Single(CalendarRecordingPlanner.Build(book, Date, Day())).Selected);
        book = book with { Overrides = [Override()], Dated = [new(Guid.NewGuid(), "单次", Date, new(12, 0), new(12, 30))] };
        Assert.All(CalendarRecordingPlanner.Build(book, Date, Day()), p => Assert.True(p.Selected));
    }
    [Fact] public void DateRangeAndWeekdayAreIntersected()
    {
        var book = RecordingPlanBook.Create(); var rule = book.Recurring[0];
        book = book with { Recurring = [rule with { From = Date.AddDays(1) }] };
        Assert.False(Assert.Single(CalendarRecordingPlanner.Build(book, Date, Day())).Selected);
        book = book with { Recurring = [rule with { Until = Date.AddDays(-1) }] };
        Assert.False(Assert.Single(CalendarRecordingPlanner.Build(book, Date, Day())).Selected);
        book = book with { Recurring = [rule with { Filter = new(Weekdays: 1 << (int)DayOfWeek.Tuesday) }] };
        Assert.False(Assert.Single(CalendarRecordingPlanner.Build(book, Date, Day())).Selected);
    }
    [Fact] public void FixedWindowsHaveNoAddedMarginsOrSubjectFiltersAndWorkWithoutTimetable()
    {
        var book = RecordingPlanBook.Create() with { Recurring = [new(Guid.NewGuid(), "体育", true, new(), FixedStart: new(10, 0), FixedEnd: new(10, 40))] };
        var plan = Assert.Single(CalendarRecordingPlanner.Build(book, Date, null));
        Assert.True(plan.Selected); Assert.True(plan.Fixed); Assert.Equal(Start, plan.Start); Assert.Equal(Start.AddMinutes(40), plan.End);
        var preview = new RecordingPreview(); preview.SetEnabled(true, Start);
        preview.Tick(null, [plan], new(Start, 0, true, true, "学校时间"), TimeSpan.Zero);
        Assert.NotNull(preview.State.Active);
        preview.Tick(null, [], new(null, 3000, false, false, "断线"), TimeSpan.FromMinutes(40)); Assert.Null(preview.State.Active);
        var restored = new RecordingPreview(preview.State); restored.SetEnabled(true, Start);
        restored.Tick(null, [plan], new(Start, 0, true, true, "学校时间"), TimeSpan.Zero); Assert.Null(restored.State.Active);
    }
    [Fact] public void FixedCourseOrFixedFixedOverlapIsConflictInsteadOfParallelAdmission()
    {
        var book = RecordingPlanBook.Create() with { Dated = [new(Guid.NewGuid(), "单次", Date, new(10, 15), new(10, 30))] };
        Assert.All(CalendarRecordingPlanner.Build(book, Date, Day()), p => Assert.True(p.Conflict));
        book = book with { Dated = [.. book.Dated, new(Guid.NewGuid(), "另一次", Date, new(10, 20), new(10, 40))] };
        Assert.All(CalendarRecordingPlanner.Build(book, Date, null), p => Assert.True(p.Conflict));
    }
    [Fact] public void AdjacentCoursesShortenTailWithoutTruncatingFormalTime()
    {
        var day = Day(); day = day with { Lessons = [day.Lessons[0], day.Lessons[0] with { Number = 2, Start = Start.AddMinutes(45), End = Start.AddMinutes(85) }] };
        var plans = CalendarRecordingPlanner.Build(RecordingPlanBook.Create(), Date, day);
        Assert.Equal(Start.AddMinutes(43), plans[0].End); Assert.False(plans[1].Conflict);
    }
    [Fact] public void PauseThroughIsInclusiveAndCoversDateIncludesAndFixedTasks()
    {
        var book = RecordingPlanBook.Create() with { PauseThrough = Date, Overrides = [Override()], Dated = [new(Guid.NewGuid(), "单次", Date, new(12, 0), new(13, 0))] };
        Assert.All(CalendarRecordingPlanner.Build(book, Date, Day()), p => Assert.False(p.Selected));
        var tomorrow = book with { Dated = [book.Dated[0] with { Date = Date.AddDays(1) }] };
        Assert.True(Assert.Single(CalendarRecordingPlanner.Build(tomorrow, Date.AddDays(1), null)).Selected);
    }
    [Fact] public void FixedRuleIdentitySurvivesTimeEditsWithinSameDay()
    {
        var book = RecordingPlanBook.Create() with { Dated = [new(Guid.NewGuid(), "固定", Date, new(10, 0), new(10, 40))] };
        var p = Assert.Single(CalendarRecordingPlanner.Build(book, Date, null));
        var preview = new RecordingPreview(); preview.SkipPlan(Guid.Empty, p, Start);
        var shifted = book with { Dated = [book.Dated[0] with { Start = new(11, 0), End = new(11, 40) }] };
        var next = Assert.Single(CalendarRecordingPlanner.Build(shifted, Date, null)); Assert.True(preview.IsMarked(Guid.Empty, next));
    }
    [Fact] public void OvernightAndDuplicateRulesAreRejected()
    {
        var book = RecordingPlanBook.Create();
        Assert.NotNull(CalendarRecordingPlanner.Validate(book with { Recurring = [book.Recurring[0], book.Recurring[0]] }));
        Assert.NotNull(CalendarRecordingPlanner.Validate(book with { Dated = [new(Guid.NewGuid(), "跨夜", Date, new(23, 0), new(1, 0))] }));
    }
    [Fact] public void StoreMigratesLegacyAsDisabledDraftAndPreservesInvalidFile()
    {
        string directory = Path.Combine(Path.GetTempPath(), "NPEduTools-book-" + Guid.NewGuid()); Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "plans.json");
        try
        {
            var store = new RecordingPlanBookStore(path); var book = store.Read(new(2, new(LessonNumbers: [2]), [], []));
            Assert.True(store.Migrated); Assert.False(book.Recurring[0].Enabled); Assert.False(book.Recurring[0].UseDefaultExclusions);
            Assert.Equal(2, Assert.Single(book.Recurring[0].Filter.LessonNumbers!));
            store.Save(book); Assert.Equal(book.Recurring[0], store.Read().Recurring[0] with { Filter = book.Recurring[0].Filter });
            File.WriteAllText(path, "{\"Version\":999}"); Assert.Throws<InvalidDataException>(() => store.Read());
            Assert.Equal("{\"Version\":999}", File.ReadAllText(path));
        }
        finally { Directory.Delete(directory, true); }
    }
}

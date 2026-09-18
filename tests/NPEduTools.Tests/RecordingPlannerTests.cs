using NPEduTools.Contracts;
using NPEduTools.Core;

namespace NPEduTools.Tests;

public sealed class RecordingPlannerTests
{
    private static readonly DateTimeOffset Start = new(2026, 9, 18, 10, 0, 0, TimeSpan.FromHours(8));
    private static readonly Guid Profile = Guid.Parse("34d14b59-8bc0-457b-bbf8-258bed28b71c");
    private static readonly Guid Math = Guid.Parse("8a8a91f4-875c-47a9-92d8-e9b8c9cc30f1");
    private static DaySchedule Day(params LessonSlot[] slots) => new(Profile, Guid.NewGuid(), Guid.NewGuid(), "测试课表",
        DateOnly.FromDateTime(Start.Date), "r1", Start, true, true, "已校时", slots.Length == 0 ? [new(1, Math, "数学", Start, Start.AddMinutes(40), true)] : slots);
    private static void Tick(RecordingPreview engine, DaySchedule day, DateTimeOffset now) =>
        engine.Tick(day with { SampledAt = now }, RecordingPlanner.Build(day, engine.State.Rules), now);

    [Fact]
    public void DefaultWindowStartsTwoMinutesBeforeAndEndsFiveMinutesAfter()
    {
        var plan = Assert.Single(RecordingPlanner.Build(Day(), new()));
        Assert.Equal(Start.AddMinutes(-2), plan.Start);
        Assert.Equal(Start.AddMinutes(45), plan.End);
        Assert.True(plan.Selected); Assert.False(plan.Conflict);
    }
    [Theory]
    [InlineData(-1, 5)] [InlineData(2, 6)] [InlineData(6, 0)]
    public void RejectsUnboundedMargins(int before, int after) => Assert.NotNull(RecordingPlanner.Validate(new(before, after)));
    [Fact]
    public void WeekdaySubjectAndOrdinalFiltersMustAllMatch()
    {
        var day = Day();
        Assert.False(Assert.Single(RecordingPlanner.Build(day, new(Weekdays: 1 << (int)DayOfWeek.Monday))).Selected);
        Assert.False(Assert.Single(RecordingPlanner.Build(day, new(AllSubjects: false, SubjectIds: []))).Selected);
        Assert.False(Assert.Single(RecordingPlanner.Build(day, new(LessonNumbers: [2]))).Selected);
        Assert.True(Assert.Single(RecordingPlanner.Build(day, new(AllSubjects: false, SubjectIds: [Math], LessonNumbers: [1]))).Selected);
    }
    [Fact]
    public void ShortBreakReducesTailInsteadOfStartingTwoRecorders()
    {
        var day = Day(new(1, Math, "数学", Start, Start.AddMinutes(40), true), new(2, Math, "数学", Start.AddMinutes(45), Start.AddMinutes(85), true));
        var plans = RecordingPlanner.Build(day, new());
        Assert.Equal(Start.AddMinutes(43), plans[0].End);
        Assert.Equal(plans[0].End, plans[1].Start);
        Assert.DoesNotContain(plans, p => p.Conflict);
    }
    [Fact]
    public void OverlappingFormalLessonsOrLeadInPreviousClassAreFlagged()
    {
        var day = Day(new(1, Math, "一", Start, Start.AddMinutes(40), true), new(2, Math, "二", Start.AddMinutes(39), Start.AddMinutes(79), true));
        Assert.All(RecordingPlanner.Build(day, new()), p => Assert.True(p.Conflict));
        day = day with { Lessons = [day.Lessons[0], day.Lessons[1] with { Start = Start.AddMinutes(41) }] };
        Assert.True(RecordingPlanner.Build(day, new())[1].Conflict);
    }
    [Fact]
    public void DisabledLessonsAreNeverSelected()
    {
        var day = Day();
        Assert.False(Assert.Single(RecordingPlanner.Build(day with { Enabled = false }, new())).Selected);
        Assert.False(Assert.Single(RecordingPlanner.Build(day with { Lessons = [day.Lessons[0] with { Enabled = false }] }, new())).Selected);
    }
    [Fact]
    public void PreviewRunsThroughBellAndStopsAtHardEndEvenDisconnected()
    {
        var day = Day(); var engine = new RecordingPreview(); engine.SetEnabled(true, Start.AddMinutes(-3));
        Tick(engine, day, Start.AddMinutes(-3)); Assert.Null(engine.State.Active);
        Tick(engine, day, Start.AddMinutes(-2)); Assert.NotNull(engine.State.Active);
        Tick(engine, day, Start.AddMinutes(40)); Assert.NotNull(engine.State.Active);
        engine.Tick(null, [], Start.AddMinutes(44)); Assert.NotNull(engine.State.Active);
        engine.Tick(null, [], Start.AddMinutes(45)); Assert.Null(engine.State.Active);
        Assert.Equal(new[] { "应开始", "应停止" }, engine.State.Events.Select(e => e.Action));
    }
    [Fact]
    public void ManualStopSurvivesReconnectionRestartAndTemporaryPlanIdentityChange()
    {
        var day = Day(); var engine = new RecordingPreview(); engine.SetEnabled(true, Start);
        Tick(engine, day, Start);
        engine.Skip(day.ProfileId, day.Lessons[0], Start.AddMinutes(1));
        var restored = new RecordingPreview(engine.State); restored.SetEnabled(true, Start.AddMinutes(2));
        var changed = day with { PlanId = Guid.NewGuid(), LayoutId = Guid.NewGuid(), Revision = "changed", Lessons = [day.Lessons[0] with { SubjectId = Guid.NewGuid(), Subject = "英语", End = Start.AddMinutes(41) }] };
        Tick(restored, changed, Start.AddMinutes(2));
        Assert.Null(restored.State.Active);
        Assert.Single(restored.State.Events, e => e.Action == "应开始");
    }
    [Fact]
    public void StaleOrUnverifiedClockDoesNotStartAndTailOnlyDoesNotCatchUp()
    {
        var day = Day(); var engine = new RecordingPreview(); engine.SetEnabled(true, Start);
        engine.Tick(day with { SampledAt = Start.AddSeconds(-16) }, RecordingPlanner.Build(day, new()), Start);
        Assert.Null(engine.State.Active);
        Tick(engine, day with { ClockVerified = false }, Start); Assert.Null(engine.State.Active);
        Tick(engine, day, Start.AddMinutes(41)); Assert.Null(engine.State.Active);
    }
    [Fact]
    public void SameSubjectInDifferentPeriodsGetsSeparateTasks()
    {
        var day = Day(new(1, Math, "数学", Start, Start.AddMinutes(40), true), new(2, Math, "数学", Start.AddMinutes(50), Start.AddMinutes(90), true));
        var engine = new RecordingPreview(); engine.SetEnabled(true, Start);
        Tick(engine, day, Start); Tick(engine, day, Start.AddMinutes(45)); Tick(engine, day, Start.AddMinutes(48));
        Assert.Equal(2, engine.State.Events.Count(e => e.Action == "应开始"));
    }
    [Fact]
    public void RuleChangeStopsActivePreviewAndTimetableChangesNeverExtendItsDeadline()
    {
        var day = Day(); var engine = new RecordingPreview(); engine.SetEnabled(true, Start); Tick(engine, day, Start);
        Tick(engine, day with { Lessons = [day.Lessons[0] with { End = Start.AddMinutes(50) }] }, Start.AddMinutes(1));
        Assert.Equal(Start.AddMinutes(45), engine.State.Active!.Plan.End);
        engine.Configure(new(AllSubjects: false, SubjectIds: [])); Tick(engine, day, Start.AddMinutes(2));
        Assert.Null(engine.State.Active);
    }
    [Fact]
    public void ClockRollbackDisarmsPreview()
    {
        var day = Day(); var engine = new RecordingPreview(); engine.SetEnabled(true, Start); Tick(engine, day, Start);
        Tick(engine, day, Start.AddMinutes(-1)); Assert.False(engine.Enabled); Assert.Null(engine.State.Active);
    }
    [Fact]
    public void SkipTodayIsBoundedToDateAndRecordsArePersisted()
    {
        string directory = Path.Combine(Path.GetTempPath(), "NPEduTools-preview-" + Guid.NewGuid().ToString("N"));
        string path = Path.Combine(directory, "preview.json");
        try
        {
            var engine = new RecordingPreview(); engine.Configure(new(AfterMinutes: 3)); engine.SetEnabled(true, Start);
            Tick(engine, Day(), Start); engine.SkipToday(Start);
            var store = new RecordingPreviewStore(path); store.Save(engine.State);
            var restored = new RecordingPreview(store.Read()); restored.SetEnabled(true, Start); Tick(restored, Day(), Start);
            Assert.Null(restored.State.Active); Assert.Equal(3, restored.State.Rules.AfterMinutes);
            Assert.Equal(DateOnly.FromDateTime(Start.Date), restored.State.SkipDate);
            File.WriteAllText(path, "{ invalid");
            Assert.Throws<System.Text.Json.JsonException>(() => store.Read());
            Assert.Equal("{ invalid", File.ReadAllText(path));
        }
        finally { if (File.Exists(path)) File.Delete(path); if (Directory.Exists(directory)) Directory.Delete(directory); }
    }
}

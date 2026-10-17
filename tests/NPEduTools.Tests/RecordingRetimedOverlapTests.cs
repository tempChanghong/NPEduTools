using NPEduTools.Contracts;
using NPEduTools.Core;

namespace NPEduTools.Tests;

public sealed class RecordingRetimedOverlapTests
{
    private static readonly DateOnly Date = new(2031, 4, 7);
    private static readonly DateTimeOffset Start = new(Date.ToDateTime(new(8, 0)), TimeSpan.Zero);
    private static readonly Guid Profile = Guid.NewGuid();
    private static DaySchedule Day() => new(Profile, Guid.NewGuid(), Guid.NewGuid(), "重叠课程", Date, "fixture", Start,
        true, true, "隔离学校时间", [new(1, Guid.NewGuid(), "上一节", Start, Start.AddHours(1), true),
            new(2, Guid.NewGuid(), "下一节", Start.AddMinutes(40), Start.AddMinutes(80), true)]);
    private static DaySchedule RetimeEnd(DaySchedule day, int minutes) => day with
    { Lessons = [day.Lessons[0] with { End = day.Lessons[0].End.AddMinutes(minutes) }, day.Lessons[1]] };
    private static PlannedRecording[] Plans(DaySchedule day) => RecordingPlanner.Build(day, new(0, 0));

    [Theory]
    [InlineData(-5)] [InlineData(5)]
    public void UnchangedUniqueStartIdentifiesAnEndEditedCourseAmongOverlappingNeighbors(int minutes)
    {
        var day = Day(); var original = Plans(day)[0]; var current = Plans(RetimeEnd(day, minutes));
        Assert.NotEqual(original.Key, current[0].Key);
        Assert.Equal(current[0], RecordingPlanner.FindReplacement(current, original));
        Assert.Null(RecordingPlanner.FindReplacement(current, original, sameProfile: false));
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void EndEditedButWithdrawnCourseCannotBecomeItsSelectedNeighbor(bool conflict)
    {
        var day = Day(); var original = Plans(day)[0]; var current = Plans(RetimeEnd(day, -5));
        current[0] = conflict ? current[0] with { Conflict = true } : current[0] with { Selected = false };
        Assert.True(current[1].Selected); Assert.False(current[1].Conflict);
        Assert.Null(RecordingPlanner.FindReplacement(current, original));
    }

    [Fact]
    public void AmbiguousSameStartsAndDisappearedSourcesKeepConservativeMatching()
    {
        var day = Day(); var original = Plans(day)[0]; var current = Plans(RetimeEnd(day, -5));
        var duplicate = current[0] with { Key = "ambiguous", Selected = false, Lesson = current[0].Lesson with { Number = 3 } };
        Assert.Null(RecordingPlanner.FindReplacement([current[0], duplicate, current[1]], original));
        Assert.False(RecordingPlanner.IsDifferentKnownLesson([current[0], duplicate, current[1]], current[1], original.Lesson.Start, original.Lesson.End));
        Assert.Equal(current[1], RecordingPlanner.FindReplacement([current[1]], original));
    }

    [Theory]
    [InlineData("Recorded")] [InlineData("Skipped")] [InlineData("Failed")]
    public void EndEditedPreviousCourseDoesNotSuppressTheDistinctNextCourseInTheLedger(string phase)
    {
        var day = Day(); var original = Plans(day); var current = Plans(RetimeEnd(day, -5));
        string directory = Directory.CreateTempSubdirectory("NPEduTools-retimed-ledger-").FullName;
        try
        {
            var ledger = new RecordingExecutionLedger(Path.Combine(directory, "executions.json"));
            ledger.Add(Profile, Date, original[0], phase, "隔离旧课程标记", original);
            Assert.True(ledger.Contains(Profile, current[0], current)); // The old occurrence itself must not reopen.
            Assert.True(ledger.Contains(Profile, current[1], [current[1]])); // Missing source identity remains conservative.
            Assert.False(ledger.Contains(Profile, current[1], current));
            ledger.Add(Profile, Date, current[1], "Recorded", "下一节独立完成", current);
            ledger.Load();
            Assert.Equal(2, ledger.Document.Entries.Length);
            Assert.True(ledger.Contains(Profile, current[0], current));
            Assert.True(ledger.Contains(Profile, current[1], current));
        }
        finally { Directory.Delete(directory, true); }
    }

    [Theory]
    [InlineData(-5)] [InlineData(5)]
    public void RehearsalKeepsTheActiveDeadlineThenHandsOffToTheDistinctNextCourse(int minutes)
    {
        var day = Day(); var original = Plans(day); var changed = RetimeEnd(day, minutes); var current = Plans(changed);
        var preview = new RecordingPreview(); preview.SetEnabled(true, Start);
        void Tick(DaySchedule source, PlannedRecording[] plans, DateTimeOffset now) =>
            preview.Tick(source, plans, new(now, 0, true, true, "隔离学校时间"), now - Start);
        Tick(day, original, Start);
        Tick(changed, current, Start.AddMinutes(15));
        Assert.NotNull(preview.State.Active);
        Assert.Equal(original[0].Key, preview.State.Active!.Plan.Key);
        Assert.Equal(original[0].End, preview.State.Active.Plan.End);
        Tick(changed, current, current[1].Start);
        Assert.Equal(current[1].Key, preview.State.Active!.Plan.Key);
        Assert.Equal(2, preview.State.Events.Count(e => e.Action == "应开始"));
    }

    [Fact]
    public void SkippingTheNextCourseAfterAnEndEditDoesNotStopTheCurrentRehearsal()
    {
        var day = Day(); var original = Plans(day); var changed = RetimeEnd(day, -5); var current = Plans(changed);
        var preview = new RecordingPreview(); preview.SetEnabled(true, Start);
        preview.Tick(day, original, new(Start, 0, true, true, "学校时间"), TimeSpan.Zero);
        preview.SkipPlan(Profile, current[1], Start.AddMinutes(15), current);
        Assert.Equal(original[0].Key, preview.State.Active!.Plan.Key);
        Assert.True(preview.IsMarked(Profile, current[1], current));
        Assert.Equal(2, preview.State.Marks.Length);
        preview.SkipPlan(Profile, current[0], Start.AddMinutes(15), current);
        Assert.Null(preview.State.Active);
    }
}

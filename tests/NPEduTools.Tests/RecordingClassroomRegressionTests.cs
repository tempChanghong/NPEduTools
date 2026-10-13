using System.Text.Json;
using NPEduTools.Contracts;
using NPEduTools.Core;
using NPEduTools.Host;

namespace NPEduTools.Tests;

public sealed class RecordingClassroomRegressionTests
{
    private static readonly DateOnly Date = new(2026, 10, 13);
    private static readonly DateTimeOffset Morning = new(2026, 10, 13, 7, 30, 0, TimeSpan.Zero);
    private static readonly Guid Profile = Guid.NewGuid(), MorningSubject = Guid.NewGuid(), Math = Guid.NewGuid();
    private static DaySchedule Day(string subject = "周测") => new(Profile, Guid.NewGuid(), Guid.NewGuid(), "相邻课程",
        Date, "r1", Morning, true, true, "学校时间",
        [new(1, MorningSubject, subject, Morning, Morning.AddMinutes(30), true),
         new(2, Math, "数学", Morning.AddMinutes(30), Morning.AddMinutes(75), true)]);

    [Theory]
    [InlineData("周测")]
    [InlineData("早读")]
    [InlineData("晨测")]
    public void ExcludedMorningSlotDoesNotBlockEightOClockLesson(string subject)
    {
        var rows = CalendarRecordingPlanner.Build(RecordingPlanBook.Create(), Date, Day(subject));
        Assert.False(rows[0].Selected);
        Assert.True(rows[1].Selected);
        Assert.False(rows[1].Conflict);
        Assert.Equal(Morning.AddMinutes(30), rows[1].Start);
        Assert.Equal(Morning.AddMinutes(80), rows[1].End);
        var preview = new RecordingPreview(); preview.SetEnabled(true, Morning);
        var at = Morning.AddMinutes(30);
        preview.Tick(Day(subject) with { SampledAt = at }, rows, new(at, 0, true, true, "学校时间"), TimeSpan.FromMinutes(30));
        Assert.Equal(rows[1].Key, preview.State.Active!.Plan.Key);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void ExplicitMorningExclusionOrInclusionStillKeepsNextLesson(bool include)
    {
        var book = RecordingPlanBook.Create() with { Overrides = [new(Guid.NewGuid(), Profile, Date, 1,
            Morning, Morning.AddMinutes(30), "周测", include ? "Include" : "Exclude")] };
        var rows = CalendarRecordingPlanner.Build(book, Date, Day());
        Assert.Equal(include, rows[0].Selected);
        Assert.False(rows[1].Conflict);
        Assert.Equal(Morning.AddMinutes(30), rows[1].Start);
        if (include) Assert.Equal(rows[0].End, rows[1].Start);
    }

    [Fact]
    public void FormalOverlapStillRequiresCorrection()
    {
        var day = Day("语文");
        day = day with { Lessons = [day.Lessons[0] with { End = Morning.AddMinutes(31) }, day.Lessons[1]] };
        Assert.All(CalendarRecordingPlanner.Build(RecordingPlanBook.Create(), Date, day), p => Assert.True(p.Conflict));
    }

    [Fact]
    public void WeeklyTestExplicitAllowIsPreserved()
    {
        var book = RecordingPlanBook.Create() with { Subjects = [new(Profile, MorningSubject, true)] };
        Assert.True(CalendarRecordingPlanner.Build(book, Date, Day())[0].Selected);
    }

    [Fact]
    public void OnlyUntouchedOldDefaultsAreUpgradedAndUserRemovalIsNotReintroduced()
    {
        string directory = Directory.CreateTempSubdirectory("NPEduTools-defaults-").FullName;
        string path = Path.Combine(directory, "plans.json");
        try
        {
            var old = RecordingPlanBook.Create() with { DefaultExclusionsRevision = 1,
                ExcludedNames = RecordingPlanBook.DefaultExcluded.Where(n => n != "周测").ToArray() };
            var legacy = System.Text.Json.Nodes.JsonNode.Parse(JsonSerializer.Serialize(old))!.AsObject();
            legacy.Remove("DefaultExclusionsRevision");
            File.WriteAllText(path, legacy.ToJsonString());
            var store = new RecordingPlanBookStore(path);
            var upgraded = store.Read();
            Assert.True(store.DefaultExclusionsUpdated);
            Assert.Contains("周测", upgraded.ExcludedNames);
            Assert.DoesNotContain("周测", JsonSerializer.Deserialize<RecordingPlanBook>(File.ReadAllText(path))!.ExcludedNames);
            store.Save(upgraded with { ExcludedNames = old.ExcludedNames });
            Assert.DoesNotContain("周测", store.Read().ExcludedNames);
            Assert.False(store.DefaultExclusionsUpdated);
            store.Save(old with { ExcludedNames = ["体育"] });
            Assert.Equal(new[] { "体育" }, store.Read().ExcludedNames);
            Assert.False(store.DefaultExclusionsUpdated);
        }
        finally { Directory.Delete(directory, true); }
    }

    [Theory]
    [InlineData("Automatic", true)] [InlineData("Manual", false)]
    public void OnlyPriorAutomaticLessonMayDelayTheNextWithoutSkipping(string owner, bool expected)
    {
        var rows = CalendarRecordingPlanner.Build(RecordingPlanBook.Create(), Date, Day("语文"));
        var state = new RecordingState("Saving", "收尾", Control: new(owner, Guid.NewGuid(), rows[0].Key, 0, 0));
        Assert.Equal(expected, RecordingService.CanWaitForAutomaticHandoff(state, rows[0], rows[1], rows[1].Start, Profile, Profile));
        Assert.False(RecordingService.CanWaitForAutomaticHandoff(state, rows[0], rows[1], rows[1].Lesson.End, Profile, Profile));
        Assert.False(RecordingService.CanWaitForAutomaticHandoff(state, rows[0], rows[1], rows[1].Start, Profile, Guid.NewGuid()));
        Assert.False(RecordingService.CanWaitForAutomaticHandoff(state, rows[0], rows[1], Morning, Profile, Profile));
    }
}

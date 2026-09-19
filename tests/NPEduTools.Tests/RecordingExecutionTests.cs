using System.Diagnostics;
using NPEduTools.Contracts;
using NPEduTools.Core;

namespace NPEduTools.Tests;

public sealed class RecordingExecutionTests
{
    [Fact]
    public void IndependentDeadlineCannotBeExtendedAndExpiredLeaseCannotBeRenewed()
    {
        long now = Stopwatch.GetTimestamp(), second = Stopwatch.Frequency;
        var guard = new RecorderDeadline();
        var c = new RecorderControl("Automatic", Guid.NewGuid(), "fixed/test/2031-04-07", now + 60 * second, now + 8 * second);
        Assert.True(guard.Accept(c, true, now));
        Assert.True(guard.Accept(c with { HardDeadline = now + 80 * second, LeaseDeadline = now + 10 * second }, false, now + 2 * second));
        Assert.Equal(c.HardDeadline, guard.Current!.HardDeadline);
        Assert.False(guard.Accept(c with { SessionId = Guid.NewGuid() }, false, now + 3 * second));
        Assert.True(guard.Accept(c with { HardDeadline = now + 5 * second }, false, now + 3 * second));
        Assert.True(guard.Expired(now + 5 * second));
        Assert.False(guard.Accept(c with { LeaseDeadline = now + 12 * second }, false, now + 5 * second));
    }
    [Fact]
    public void ManualLeaseAndInvalidDeadlinesAreBounded()
    {
        long now = Stopwatch.GetTimestamp(), second = Stopwatch.Frequency;
        var guard = new RecorderDeadline();
        Assert.False(guard.Accept(new("Manual", Guid.NewGuid(), "", 0, now + 8 * second), false, now));
        Assert.False(guard.Accept(new("Automatic", Guid.NewGuid(), "x", now - 1, now + 8 * second), true, now));
        Assert.True(guard.Accept(new("Manual", Guid.NewGuid(), "", 0, now + 8 * second), true, now));
        Assert.False(guard.Expired(now + 7 * second)); Assert.True(guard.Expired(now + 8 * second));
    }
    [Fact]
    public void StartingIntentSurvivesRestartAndDoesNotBecomeRecorded()
    {
        string directory = Path.Combine(Path.GetTempPath(), "NPEduTools-ledger-" + Guid.NewGuid().ToString("N"));
        string path = Path.Combine(directory, "executions.json");
        try
        {
            var ledger = new RecordingExecutionLedger(path); ledger.Load();
            var date = new DateOnly(2031, 4, 7);
            var book = RecordingPlanBook.Create() with { Recurring = [], Dated = [new(Guid.NewGuid(), "固定测试", date, new(10, 0), new(10, 1))] };
            var plan = Assert.Single(CalendarRecordingPlanner.Build(book, date, null));
            var entry = ledger.Add(Guid.Empty, date, plan, "Starting", "已提交启动意图");
            Assert.True(File.Exists(path));
            var restarted = new RecordingExecutionLedger(path); restarted.Load();
            Assert.Equal("Interrupted", Assert.Single(restarted.Document.Entries).Phase);
            Assert.True(restarted.Contains(Guid.Empty, plan));
            Assert.Throws<InvalidDataException>(() => restarted.Add(Guid.Empty, date, plan, "Starting", "重放"));
            restarted.SkipDate(date); restarted.Update(entry.SessionId, "Recorded", state: new("Saved", "已保存", OutputFile: "verified.mp4"));
            var again = new RecordingExecutionLedger(path); again.Load();
            Assert.Equal(date, again.Document.SkipDate); Assert.Equal("verified.mp4", Assert.Single(again.Document.Entries).OutputFile);
            File.WriteAllText(path, "{\"Version\":999}");
            Assert.Throws<InvalidDataException>(() => new RecordingExecutionLedger(path).Load());
            Assert.Equal("{\"Version\":999}", File.ReadAllText(path));
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }
    [Fact]
    public void PreviewMarksCannotSuppressRealIntentsAndCourseRenameCannotReplay()
    {
        var date = new DateOnly(2031, 4, 7); var time = new DateTimeOffset(date.ToDateTime(new(10, 0)), TimeSpan.Zero);
        Guid profile = Guid.NewGuid(); var lesson = new LessonSlot(1, Guid.NewGuid(), "数学", time, time.AddMinutes(40), true);
        var source = new DaySchedule(profile, Guid.NewGuid(), Guid.NewGuid(), "今日", date, "r", time, true, true, "学校", [lesson]);
        var p = Assert.Single(RecordingPlanner.Build(source, new()));
        var preview = new RecordingPreview(); preview.Skip(profile, lesson, time);
        string root = Path.Combine(Path.GetTempPath(), "NPEduTools-ledger-" + Guid.NewGuid());
        try
        {
            var ledger = new RecordingExecutionLedger(Path.Combine(root, "journal.json")); ledger.Load();
            Assert.False(ledger.Contains(profile, p));
            ledger.Add(profile, date, p, "Skipped", "手动占用");
            Assert.True(ledger.Contains(profile, p with { Key = "changed", Lesson = lesson with { Subject = "临时英语", Start = time.AddMinutes(2) } }));
            Assert.False(ledger.Contains(Guid.NewGuid(), p));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
    [Fact]
    public void StopRequiresExpectedOwnerAndSession()
    {
        var request = new HostRequest(1, Guid.NewGuid(), "recording.command", Recording: new("stop", ClientId: Guid.NewGuid()));
        Assert.NotNull(Protocol.Validate(request));
        Assert.Null(Protocol.Validate(request with { Recording = request.Recording! with { Control = new("Manual", Guid.NewGuid(), "", 0, 0) } }));
        Assert.NotNull(Protocol.Validate(request with { Capability = "host.ping" }));
    }
}

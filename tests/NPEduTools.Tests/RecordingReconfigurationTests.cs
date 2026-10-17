using System.Diagnostics;
using System.Reflection;
using NPEduTools.Contracts;
using NPEduTools.Core;
using NPEduTools.Host;

namespace NPEduTools.Tests;

public sealed class RecordingReconfigurationTests
{
    private static readonly DateOnly Date = new(2031, 4, 7);
    private static readonly DateTimeOffset Now = new(Date.ToDateTime(new(12, 15)), TimeSpan.Zero);

    [Theory]
    [InlineData(10, false)] [InlineData(10, true)]
    [InlineData(0, false)] [InlineData(0, true)]
    [InlineData(-5, false)] [InlineData(-5, true)]
    public async Task RemovedActiveTaskDoesNotSkipTheDueReplacementWhileFinalizing(int nextStartMinute, bool alreadyEnded)
    {
        await using var f = await Fixture.Open();
        var oldBook = RecordingPlanBook.Create() with { Recurring = [], Dated = [new(Guid.NewGuid(), "旧任务", Date, new(12, 0), new(12, 30))] };
        var oldPlan = Assert.Single(CalendarRecordingPlanner.Build(oldBook, Date, null));
        if (alreadyEnded) oldPlan = oldPlan with { End = Now.AddMinutes(-1) };
        var entry = f.Ledger.Add(Guid.Empty, Date, oldPlan, "Recording", "隔离会话");
        var newBook = oldBook with { Dated = [new(Guid.NewGuid(), "替换任务", Date, new TimeOnly(12, 0).AddMinutes(nextStartMinute), new(12, 45))] };
        new RecordingPlanBookStore(f.BookPath).Save(newBook);
        f.Set("_activePlan", oldPlan); f.Set("_activeProfile", Guid.Empty);
        f.Set("_last", new RecordingState("Saving", "隔离收尾", Control: new("Automatic", entry.SessionId, entry.Key, 0, 0)));
        f.Set("_enabled", true); f.Set("_appLease", RecorderDeadline.After(TimeSpan.FromMinutes(1)));
        f.Fresh(Now);
        await f.Tick();
        Assert.Single(f.Ledger.Document.Entries);
        Assert.Equal("Finalizing", f.Ledger.Document.Entries[0].Phase);
        Assert.True(f.Field<PlannedRecording>("_activePlan").End <= Now.AddSeconds(1));
        if (alreadyEnded) Assert.Equal(oldPlan.End, f.Field<PlannedRecording>("_activePlan").End);
        Assert.Null(f.Field<object?>("_worker"));
        await f.Tick();
        Assert.Single(f.Ledger.Document.Entries);
    }

    [Fact]
    public async Task AManualRecordingStillKeepsItsOwnershipDuringScheduleChanges()
    {
        await using var f = await Fixture.Open();
        var book = RecordingPlanBook.Create() with { Recurring = [], Dated = [new(Guid.NewGuid(), "自动任务", Date, new(12, 10), new(12, 45))] };
        new RecordingPlanBookStore(f.BookPath).Save(book);
        var manual = new RecordingState("Recording", "隔离手动录制", Control: new("Manual", Guid.NewGuid(), "", 0, 0));
        f.Set("_last", manual); f.Set("_enabled", true); f.Set("_appLease", RecorderDeadline.After(TimeSpan.FromMinutes(1)));
        f.Fresh(Now); await f.Tick();
        Assert.Equal("Skipped", Assert.Single(f.Ledger.Document.Entries).Phase);
        Assert.Equal(manual, f.Field<RecordingState>("_last"));
        Assert.Null(f.Field<object?>("_worker"));
    }

    [Fact]
    public async Task RecordingConfirmationRetainsOverlapExplanationWithoutRepeatingIt()
    {
        await using var f = await Fixture.Open();
        var book = RecordingPlanBook.Create() with { Recurring = [], Dated = [new(Guid.NewGuid(), "任务", Date, new(12, 0), new(12, 30))] };
        var plan = Assert.Single(CalendarRecordingPlanner.Build(book, Date, null));
        const string explanation = "启动意图已保存；录制范围重叠，下一节优先";
        var entry = f.Ledger.Add(Guid.Empty, Date, plan, "Starting", explanation);
        f.Set("_activePlan", plan);
        f.Set("_last", new RecordingState("Recording", "隔离回执", Control: new("Automatic", entry.SessionId, entry.Key, 0, 0)));
        await f.Tick();
        string reason = f.Ledger.Document.Entries[0].Reason;
        Assert.Contains(explanation, reason);
        Assert.Contains("录制器已确认开始", reason);
        await f.Tick();
        Assert.Equal(reason, f.Ledger.Document.Entries[0].Reason);
        Assert.Null(f.Field<object?>("_worker"));
    }

    // Execute the real Host scheduler with synthetic state and a private plan
    // store. The background loop is cancelled; no recorder process is created.
    private sealed class Fixture : IAsyncDisposable
    {
        private const BindingFlags Flags = BindingFlags.Instance | BindingFlags.NonPublic;
        private readonly string _directory = Directory.CreateTempSubdirectory("NPEduTools-reconfiguration-").FullName;
        private SchoolClockFrame _frame = SchoolClockFrame.Unavailable("隔离时钟");
        private readonly RecordingService _service;
        private Fixture() => _service = new("NPEduTools.Test.reconfigure." + Guid.NewGuid().ToString("N"), _directory, () => _frame);
        public T Field<T>(string name) => (T)typeof(RecordingService).GetField(name, Flags)!.GetValue(_service)!;
        public void Set(string name, object value) => typeof(RecordingService).GetField(name, Flags)!.SetValue(_service, value);
        public string BookPath => Field<string>("_bookPath");
        public RecordingExecutionLedger Ledger => Field<RecordingExecutionLedger>("_ledger");
        public static async Task<Fixture> Open()
        {
            var f = new Fixture(); f.Field<CancellationTokenSource>("_lifetime").Cancel();
            await f.Field<Task>("_loop"); return f;
        }
        public void Fresh(DateTimeOffset now)
        {
            var clock = Field<SchoolClockTracker>("_clock");
            Guid connection = Guid.NewGuid(), bridge = Guid.NewGuid();
            var at = Stopwatch.GetElapsedTime(0);
            for (int i = 0; i < 3; i++)
            {
                _frame = new(connection, bridge, i + 1, 0, now.AddSeconds(i * .5), 0, "Advancing", "隔离时钟");
                clock.Accept(_frame, at - TimeSpan.FromSeconds(1 - i * .5), 0);
            }
            Assert.True(clock.Read(Stopwatch.GetElapsedTime(0)).CanStart);
        }
        public Task Tick() => (Task)typeof(RecordingService).GetMethod("TickAsync", Flags)!.Invoke(_service, null)!;
        public async ValueTask DisposeAsync()
        {
            Set("_last", new RecordingState("Idle", "隔离测试结束"));
            await _service.DisposeAsync();
            if (File.Exists(BookPath)) File.Delete(BookPath);
            Directory.Delete(_directory, true);
        }
    }
}

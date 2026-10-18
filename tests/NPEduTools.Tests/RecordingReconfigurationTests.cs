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

    [Fact]
    public async Task ExcludingTheCurrentOverlappingCourseDoesNotRetargetItsSessionToTheNextCourse()
    {
        await using var f = await Fixture.Open();
        var start = new DateTimeOffset(Date.ToDateTime(new(12, 0)), TimeSpan.Zero);
        var source = new DaySchedule(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "重叠课程", Date, "fixture", Now,
            true, true, "隔离课表", [new(1, Guid.NewGuid(), "第一节", start, start.AddMinutes(30), true),
                new(2, Guid.NewGuid(), "下一节", start.AddMinutes(10), start.AddMinutes(45), true)]);
        var oldBook = RecordingPlanBook.Create();
        oldBook = oldBook with { Recurring = [oldBook.Recurring[0] with { Filter = new(LessonNumbers: [1]) }] };
        var old = CalendarRecordingPlanner.Build(oldBook, Date, source).Single(p => p.Selected);
        var entry = f.Ledger.Add(source.ProfileId, Date, old, "Recording", "隔离自动会话");
        var nextBook = oldBook with { Recurring = [oldBook.Recurring[0] with { Filter = new(LessonNumbers: [2]) }] };
        new RecordingPlanBookStore(f.BookPath).Save(nextBook);
        f.Set("_activePlan", old); f.Set("_activeProfile", source.ProfileId);
        f.Set("_last", new RecordingState("Recording", "隔离自动会话", Control: new("Automatic", entry.SessionId, entry.Key, 0, 0)));
        f.Set("_enabled", true); f.Set("_appLease", RecorderDeadline.After(TimeSpan.FromMinutes(1)));
        f.Fresh(Now, source); await f.Tick();
        Assert.Single(f.Ledger.Document.Entries);
        Assert.False(f.Field<PlannedRecording>("_activePlan").Selected);
        Assert.True(f.Field<PlannedRecording>("_activePlan").End <= Now.AddSeconds(1));
        Assert.Null(f.Field<object?>("_worker"));
    }

    [Theory]
    [InlineData("Saved", "Recorded")] [InlineData("Failed", "Failed")]
    public async Task RetiringAnAutomaticWorkerCommitsItsFinalResultBeforeAnotherSessionCanReplaceIt(string workerPhase, string expected)
    {
        await using var f = await Fixture.Open();
        var book = RecordingPlanBook.Create() with { Recurring = [], Dated = [new(Guid.NewGuid(), "刚结束的任务", Date, new(12, 0), new(12, 15))] };
        var plan = Assert.Single(CalendarRecordingPlanner.Build(book, Date, null));
        var entry = f.Ledger.Add(Guid.Empty, Date, plan, "Recording", "重叠已处理，下一节优先");
        var final = new RecordingState(workerPhase, "隔离最终回执", OutputFile: workerPhase == "Saved" ? "fixture.mp4" : null,
            Error: workerPhase == "Failed" ? "隔离失败" : null, Control: new("Automatic", entry.SessionId, entry.Key, 0, 0));
        f.Set("_activePlan", plan); f.Worker(final);
        await f.Retire();
        var saved = Assert.Single(f.Ledger.Document.Entries);
        Assert.Equal(expected, saved.Phase); Assert.Equal(final.OutputFile, saved.OutputFile);
        if (workerPhase == "Saved") Assert.Contains("下一节优先", saved.Reason);
        else Assert.Equal("隔离失败", saved.Reason);
        Assert.Null(f.Field<object?>("_activePlan")); Assert.Null(f.Field<object?>("_worker"));
        string reason = saved.Reason;
        await f.Tick(); Assert.Equal(reason, f.Ledger.Document.Entries[0].Reason);
        f.Ledger.Load(); Assert.Equal(expected, f.Ledger.Document.Entries[0].Phase);
    }

    [Fact]
    public async Task AFinalReceiptRemainsRetryableWhenTheLedgerIsTemporarilyLocked()
    {
        await using var f = await Fixture.Open();
        var book = RecordingPlanBook.Create() with { Recurring = [], Dated = [new(Guid.NewGuid(), "完成的任务", Date, new(12, 0), new(12, 15))] };
        var plan = Assert.Single(CalendarRecordingPlanner.Build(book, Date, null));
        var entry = f.Ledger.Add(Guid.Empty, Date, plan, "Recording", "隔离会话");
        f.Set("_activePlan", plan);
        f.Worker(new("Saved", "隔离完成回执", OutputFile: "fixture.mp4", Control: new("Automatic", entry.SessionId, entry.Key, 0, 0)));
        byte[] original = File.ReadAllBytes(f.LedgerPath);
        using (var locked = new FileStream(f.LedgerPath, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            var error = await Record.ExceptionAsync(f.Retire);
            Assert.True(error is IOException or UnauthorizedAccessException, error?.ToString() ?? "The locked ledger unexpectedly accepted a write.");
            Assert.Equal(original, File.ReadAllBytes(f.LedgerPath));
            Assert.NotNull(f.Field<object?>("_worker"));
            Assert.NotNull(f.Field<object?>("_activePlan"));
        }
        await f.Retire();
        Assert.Equal("Recorded", Assert.Single(f.Ledger.Document.Entries).Phase);
        Assert.Equal("fixture.mp4", f.Ledger.Document.Entries[0].OutputFile);
        Assert.Null(f.Field<object?>("_worker"));
        Assert.Null(f.Field<object?>("_activePlan"));
    }

    [Fact]
    public async Task RetiringAManualWorkerCannotCertifyAnOldAutomaticExecution()
    {
        await using var f = await Fixture.Open();
        var book = RecordingPlanBook.Create() with { Recurring = [], Dated = [new(Guid.NewGuid(), "自动任务", Date, new(12, 0), new(12, 15))] };
        var plan = Assert.Single(CalendarRecordingPlanner.Build(book, Date, null));
        f.Ledger.Add(Guid.Empty, Date, plan, "Recording", "旧自动会话");
        f.Set("_activePlan", plan);
        f.Worker(new("Saved", "手动最终回执", OutputFile: "manual-fixture.mp4", Control: new("Manual", Guid.NewGuid(), "", 0, 0)));
        await f.Retire();
        var entry = Assert.Single(f.Ledger.Document.Entries);
        Assert.Equal("Recording", entry.Phase); Assert.Null(entry.OutputFile);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task StoppingKeepsOverlapExplanationAndDoesNotAppendOnEveryRetry(bool userStop)
    {
        await using var f = await Fixture.Open();
        var book = RecordingPlanBook.Create() with { Recurring = [], Dated = [new(Guid.NewGuid(), "任务", Date, new(12, 0), new(12, 30))] };
        var plan = Assert.Single(CalendarRecordingPlanner.Build(book, Date, null));
        const string explanation = "录制范围重叠，下一节优先";
        var entry = f.Ledger.Add(Guid.Empty, Date, plan, "Recording", explanation);
        var control = new RecorderControl("Automatic", entry.SessionId, entry.Key, 0, 0);
        f.Set("_activePlan", plan); f.Worker(new("Recording", "隔离会话", Control: control));
        async Task Stop()
        {
            if (userStop)
            {
                var response = await f.Send(new(Protocol.Version, Guid.NewGuid(), "recording.command",
                    Recording: new("stop", Control: control, ClientId: Guid.NewGuid())));
                Assert.Equal("Rejected", response.Outcome); // No real worker process exists.
            }
            else await Assert.ThrowsAsync<IOException>(() => f.Stop("到达计划截止时间"));
        }
        try
        {
            await Stop();
            string reason = f.Ledger.Document.Entries[0].Reason;
            Assert.Contains(explanation, reason);
            Assert.Contains(userStop ? "用户提前结束" : "到达计划截止时间", reason);
            Assert.Equal("Finalizing", f.Ledger.Document.Entries[0].Phase);
            await Stop(); Assert.Equal(reason, f.Ledger.Document.Entries[0].Reason);
        }
        finally { f.Field<RecorderProcess>("_worker").Fail("隔离测试清理；未启动进程"); }
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
        public string LedgerPath => Path.Combine(_directory, "recording-executions.json");
        public RecordingExecutionLedger Ledger => Field<RecordingExecutionLedger>("_ledger");
        public static async Task<Fixture> Open()
        {
            var f = new Fixture(); f.Field<CancellationTokenSource>("_lifetime").Cancel();
            await f.Field<Task>("_loop"); return f;
        }
        public void Fresh(DateTimeOffset now, DaySchedule? source = null)
        {
            var clock = Field<SchoolClockTracker>("_clock");
            Guid connection = Guid.NewGuid(), bridge = Guid.NewGuid();
            var at = Stopwatch.GetElapsedTime(0);
            for (int i = 0; i < 3; i++)
            {
                _frame = new(connection, bridge, i + 1, 0, now.AddSeconds(i * .5), 0, "Advancing", "隔离时钟", source);
                clock.Accept(_frame, at - TimeSpan.FromSeconds(1 - i * .5), 0);
            }
            Assert.True(clock.Read(Stopwatch.GetElapsedTime(0)).CanStart);
        }
        public Task Tick() => (Task)typeof(RecordingService).GetMethod("TickAsync", Flags)!.Invoke(_service, null)!;
        public Task Retire() => (Task)typeof(RecordingService).GetMethod("RetireAsync", Flags)!.Invoke(_service, null)!;
        public Task Stop(string reason) => (Task)typeof(RecordingService).GetMethod("StopAutomaticAsync", Flags)!.Invoke(_service, [reason])!;
        public Task<HostResponse> Send(HostRequest request) => _service.HandleAsync(request);
        public void Worker(RecordingState state)
        {
            var worker = new RecorderProcess("isolated-worker-never-started");
            typeof(RecorderProcess).GetField("_state", Flags)!.SetValue(worker, state);
            typeof(RecorderProcess).GetField("_control", Flags)!.SetValue(worker, state.Control);
            Set("_worker", worker);
        }
        public async ValueTask DisposeAsync()
        {
            Set("_last", new RecordingState("Idle", "隔离测试结束"));
            await _service.DisposeAsync();
            if (File.Exists(BookPath)) File.Delete(BookPath);
            Directory.Delete(_directory, true);
        }
    }
}

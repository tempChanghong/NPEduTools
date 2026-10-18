using System.Diagnostics;
using System.Reflection;
using NPEduTools.Contracts;
using NPEduTools.Core;
using NPEduTools.Host;

namespace NPEduTools.Tests;

[Collection(ProcessIntegrationCollection.Name)]
public sealed class RecordingHandoffLifecycleTests
{
    private static readonly DateOnly Date = new(2031, 4, 7);
    private static readonly DateTimeOffset Now = new(Date.ToDateTime(new(12, 15)), TimeSpan.Zero);

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task WorkerCleanupTimeoutKeepsTheSchedulerEnabledAndTheNextCourseUnclaimed(bool failed)
    {
        await using var f = await Fixture.Open();
        var plans = f.Plans(new(12, 45));
        await f.FinishedWorker(plans[0], failed);
        f.Enable(); f.Fresh(Now);
        await f.Tick();
        Assert.True(f.Field<bool>("_enabled"));
        Assert.True(f.Worker!.Alive);
        Assert.Equal(failed ? "Failed" : "Recorded", Assert.Single(f.Ledger.Document.Entries).Phase);
        Assert.Contains("交接", f.Service.Automatic.Message);
        await f.ReleaseWorker();
        f.Fresh(Now.AddSeconds(2)); await f.Tick();
        Assert.Equal(2, f.Ledger.Document.Entries.Length);
        Assert.Equal(plans[1].Key, f.Worker!.State.Control!.OccurrenceId);
        Assert.DoesNotContain(f.Ledger.Document.Entries, e => e.Phase == "Skipped");
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task CleanupTimeCannotExtendOrReopenTheNextCourseWindow(bool nextAlreadyEnded)
    {
        await using var f = await Fixture.Open();
        var plans = f.Plans(nextAlreadyEnded ? new(12, 15, 2) : new(12, 45));
        await f.FinishedWorker(plans[0]);
        f.Enable(); f.Fresh(Now);
        long expectedEnd = RecorderDeadline.After(plans[1].End - Now.AddSeconds(1));
        var release = Task.Run(async () => { await Task.Delay(1600); File.WriteAllText(f.ReleasePath, "done"); });
        await f.Tick(); await release;
        var next = f.Ledger.Document.Entries.Single(e => e.Key == plans[1].Key);
        if (nextAlreadyEnded)
        {
            Assert.Equal("Missed", next.Phase);
            Assert.Null(f.Worker);
        }
        else
        {
            Assert.Equal("Starting", next.Phase);
            Assert.True(f.Worker!.State.Control!.HardDeadline <= expectedEnd + Stopwatch.Frequency / 4,
                "Waiting for the old worker must consume, rather than extend, the next course's deadline.");
        }
    }

    [Fact]
    public async Task AppLeaseExpirationStillDisablesStartsWhileOldWorkerCleanupIsPending()
    {
        await using var f = await Fixture.Open();
        var plans = f.Plans(new(12, 45));
        await f.FinishedWorker(plans[0]);
        f.Enable(); f.Set("_appLease", RecorderDeadline.After(TimeSpan.FromSeconds(1))); f.Fresh(Now);
        await f.Tick();
        Assert.False(f.Field<bool>("_enabled"));
        Assert.Single(f.Ledger.Document.Entries);
        await f.ReleaseWorker();
        f.Fresh(Now.AddSeconds(2)); await f.Tick();
        Assert.Single(f.Ledger.Document.Entries);
        Assert.Null(f.Worker);
    }

    [Fact]
    public async Task ManualStartCannotReplaceAWorkerStillCleaningUpOrDisableAutomaticHandoff()
    {
        await using var f = await Fixture.Open();
        var plans = f.Plans(new(12, 45));
        await f.FinishedWorker(plans[0]);
        var oldWorker = f.Worker;
        f.Enable(); f.Fresh(Now);
        var response = await f.Service.HandleAsync(new(Protocol.Version, Guid.NewGuid(), "recording.command",
            Recording: new("start", new("synthetic", f.DirectoryPath), ClientId: Guid.NewGuid())));
        Assert.Equal("Rejected", response.Outcome);
        Assert.Contains("收尾", response.Message);
        Assert.Same(oldWorker, f.Worker);
        Assert.True(f.Field<bool>("_enabled"));
        Assert.Single(f.Ledger.Document.Entries);
        await f.ReleaseWorker();
        f.Fresh(Now.AddSeconds(2)); await f.Tick();
        Assert.Equal("Recorded", f.Ledger.Document.Entries[0].Phase);
        Assert.Equal(plans[1].Key, f.Worker!.State.Control!.OccurrenceId);
    }

    [Fact]
    public async Task LongSavingKeepsAutomaticHandoffWaitingWithoutFloodingTheWorkerPipe()
    {
        await using var f = await Fixture.Open();
        var plans = f.Plans(new(12, 45));
        await f.FinishedWorker(plans[0], saving: true);
        f.Enable();
        for (int i = 0; i < 100; i++)
        {
            f.Fresh(Now.AddMilliseconds(i * 250)); await f.Tick();
            Assert.True(f.Field<bool>("_enabled"));
            Assert.Equal("Finalizing", Assert.Single(f.Ledger.Document.Entries).Phase);
        }
        File.WriteAllText(Path.Combine(f.DirectoryPath, "release-saving"), "done");
        using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5)))
            while (f.Worker!.State.Phase != "Saved") await Task.Delay(20, timeout.Token);
        f.Fresh(Now.AddSeconds(30)); await f.Tick();
        Assert.Equal("Recorded", f.Ledger.Document.Entries[0].Phase);
        Assert.Equal(plans[1].Key, f.Worker!.State.Control!.OccurrenceId);
        Assert.Single(File.ReadAllLines(Path.Combine(f.DirectoryPath, "stop-commands.txt")));
        Assert.DoesNotContain(f.Ledger.Document.Entries, e => e.Phase == "Skipped");
    }

    [Theory]
    [InlineData(-10)] [InlineData(10)]
    public async Task EndEditedOverlappingCourseKeepsItsIdentityAndHandsOffToTheNextCourse(int minutes)
    {
        await using var f = await Fixture.Open();
        var start = Now.AddMinutes(-15);
        var source = new DaySchedule(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "重叠课程", Date, "fixture", Now,
            true, true, "隔离课表", [new(1, Guid.NewGuid(), "上一节", start, start.AddMinutes(40), true),
                new(2, Guid.NewGuid(), "下一节", start.AddMinutes(20), start.AddMinutes(60), true)]);
        var book = RecordingPlanBook.Create(); new RecordingPlanBookStore(f.BookPath).Save(book);
        var original = CalendarRecordingPlanner.Build(book, Date, source);
        await f.StartWorker(original[0], "recorder-slow-saving", source.ProfileId);
        var changed = source with { Lessons = [source.Lessons[0] with { End = source.Lessons[0].End.AddMinutes(minutes) }, source.Lessons[1]] };
        var current = CalendarRecordingPlanner.Build(book, Date, changed);
        f.Enable(); f.Fresh(Now, changed); await f.Tick();
        Assert.True(f.Field<PlannedRecording>("_activePlan").Selected);
        Assert.Equal(original[0].Key, f.Field<PlannedRecording>("_activePlan").Key);
        Assert.Equal(original[0].End, f.Field<PlannedRecording>("_activePlan").End);
        Assert.Equal("Recording", f.Worker!.State.Phase);
        Assert.Equal("下一节", f.Service.Automatic.Next?.Subject);
        f.Fresh(Now.AddMinutes(5), changed); await f.Tick();
        using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5)))
            while (f.Worker.State.Phase != "Saving") await Task.Delay(20, timeout.Token);
        Assert.Single(f.Ledger.Document.Entries);
        File.WriteAllText(Path.Combine(f.DirectoryPath, "release-saving"), "done");
        using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5)))
            while (f.Worker!.State.Phase != "Saved") await Task.Delay(20, timeout.Token);
        f.Fresh(Now.AddMinutes(5).AddSeconds(1), changed); await f.Tick();
        Assert.Equal(2, f.Ledger.Document.Entries.Length);
        Assert.Equal("Recorded", f.Ledger.Document.Entries[0].Phase);
        Assert.Equal(current[1].Key, f.Worker!.State.Control!.OccurrenceId);
        Assert.DoesNotContain(f.Ledger.Document.Entries, e => e.Phase == "Skipped");
    }

    [Theory]
    [InlineData("skip-upcoming")] [InlineData("skip-next")]
    public async Task SkipNextIgnoresACourseWhoseFormalWindowAlreadyEnded(string action)
    {
        await using var f = await Fixture.Open();
        var start = Now.AddMinutes(-30);
        var source = new DaySchedule(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "课间", Date, "fixture", Now,
            true, true, "隔离时钟", [new(1, Guid.NewGuid(), "已下课", start, Now, true),
                new(2, Guid.NewGuid(), "下一节", Now.AddMinutes(5), Now.AddMinutes(50), true)]);
        new RecordingPlanBookStore(f.BookPath).Save(RecordingPlanBook.Create());
        f.Fresh(Now, source);
        var response = await f.Service.HandleAsync(new(Protocol.Version, Guid.NewGuid(), "recording.automatic",
            Automatic: new(action, Guid.NewGuid())));
        Assert.Equal("Succeeded", response.Outcome);
        Assert.Equal("下一节", Assert.Single(f.Ledger.Document.Entries).Subject);
    }

    // Private stores and the synthetic GuardFixture recorder only: no screen,
    // audio, camera, real timetable, or production endpoint is accessed.
    private sealed class Fixture : IAsyncDisposable
    {
        private const BindingFlags Flags = BindingFlags.Instance | BindingFlags.NonPublic;
        private SchoolClockFrame _frame = SchoolClockFrame.Unavailable("隔离时钟");
        public string DirectoryPath { get; } = Directory.CreateTempSubdirectory("NPEduTools-handoff-").FullName;
        public string ReleasePath => Path.Combine(DirectoryPath, "release-worker");
        public RecordingService Service { get; }
        public string BookPath => Field<string>("_bookPath");
        public RecordingExecutionLedger Ledger => Field<RecordingExecutionLedger>("_ledger");
        public RecorderProcess? Worker => Field<RecorderProcess?>("_worker");
        private string Executable => Path.Combine(AppContext.BaseDirectory, "GuardFixture", "NPEduTools.GuardFixture.exe");
        private Fixture() => Service = new("NPEduTools.Test.handoff." + Guid.NewGuid().ToString("N"), DirectoryPath, () => _frame);
        public T Field<T>(string name) => (T)typeof(RecordingService).GetField(name, Flags)!.GetValue(Service)!;
        public void Set(string name, object value) => typeof(RecordingService).GetField(name, Flags)!.SetValue(Service, value);
        public static async Task<Fixture> Open()
        {
            var f = new Fixture(); f.Field<CancellationTokenSource>("_lifetime").Cancel(); await f.Field<Task>("_loop");
            f.Set("_executable", f.Executable); f.Set("_fixture", "recorder-ready");
            f.Set("_options", new RecordingOptions("synthetic", f.DirectoryPath));
            return f;
        }
        public PlannedRecording[] Plans(TimeOnly nextEnd)
        {
            var book = RecordingPlanBook.Create() with { Recurring = [], Dated =
                [new(Guid.NewGuid(), "上一节", Date, new(12, 0), new(12, 15)),
                 new(Guid.NewGuid(), "下一节", Date, new(12, 15), nextEnd)] };
            new RecordingPlanBookStore(BookPath).Save(book);
            return CalendarRecordingPlanner.Build(book, Date, null);
        }
        public void Enable()
        { Set("_enabled", true); Set("_appLease", RecorderDeadline.After(TimeSpan.FromMinutes(1))); }
        public async Task FinishedWorker(PlannedRecording plan, bool failed = false, bool saving = false, Guid profile = default)
        {
            var control = await StartWorker(plan, saving ? "recorder-slow-saving" : "recorder-finalizing", profile);
            var worker = Worker!;
            await worker.CommandAsync("stop", control);
            await Wait(() => worker.State.Phase == (saving ? "Saving" : "Saved"));
            if (failed) worker.Fail("隔离失败回执");
        }
        public async Task<RecorderControl> StartWorker(PlannedRecording plan, string mode, Guid profile = default)
        {
            var entry = Ledger.Add(profile, Date, plan, "Recording", "隔离自动会话");
            var worker = new RecorderProcess(Executable, mode);
            Set("_worker", worker); Set("_activePlan", plan); Set("_activeProfile", profile);
            var control = new RecorderControl("Automatic", entry.SessionId, entry.Key,
                RecorderDeadline.After(TimeSpan.FromMinutes(1)), RecorderDeadline.After(TimeSpan.FromSeconds(8)));
            await worker.StartAsync(new("synthetic", DirectoryPath), control);
            await Wait(() => worker.State.Phase == "Recording");
            return control;
        }
        public async Task ReleaseWorker()
        { File.WriteAllText(ReleasePath, "done"); await Wait(() => Worker?.Alive != true); }
        public void Fresh(DateTimeOffset now, DaySchedule? source = null)
        {
            var clock = Field<SchoolClockTracker>("_clock");
            Guid connection = Guid.NewGuid(), bridge = Guid.NewGuid(); var at = Stopwatch.GetElapsedTime(0);
            for (int i = 0; i < 3; i++)
            {
                _frame = new(connection, bridge, i + 1, 0, now.AddSeconds(i * .5), 0, "Advancing", "隔离时钟", source);
                clock.Accept(_frame, at - TimeSpan.FromSeconds(1 - i * .5), 0);
            }
            Assert.True(clock.Read(Stopwatch.GetElapsedTime(0)).CanStart);
        }
        public Task Tick() => (Task)typeof(RecordingService).GetMethod("TickAsync", Flags)!.Invoke(Service, null)!;
        private static async Task Wait(Func<bool> ready)
        { using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5)); while (!ready()) await Task.Delay(20, timeout.Token); }
        public async ValueTask DisposeAsync()
        {
            File.WriteAllText(ReleasePath, "done");
            File.WriteAllText(Path.Combine(DirectoryPath, "release-saving"), "done");
            // Drain the synthetic process reader before disposing the service;
            // a late start receipt must not race a fabricated cleanup failure.
            if (Worker is { } worker) await worker.DisposeAsync();
            await Service.DisposeAsync();
            if (File.Exists(BookPath)) File.Delete(BookPath);
            Directory.Delete(DirectoryPath, true);
        }
    }
}

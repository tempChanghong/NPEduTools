using System.Text.Json.Nodes;
using System.IO.Pipes;
using NPEduTools.Core;
using NPEduTools.Contracts;
using NPEduTools.Host;

namespace NPEduTools.Tests;

public sealed class StudyReminderRecoveryTests
{
    private sealed class Clock : TimeProvider
    {
        public long Ms; public DateTimeOffset Wall = DateTimeOffset.Parse("2026-10-12T11:00:00Z");
        public override long TimestampFrequency => 1000; public override long GetTimestamp() => Ms;
        public override DateTimeOffset GetUtcNow() => Wall;
    }
    private sealed class DirectoryFixture : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "study-reminder-tests-" + Guid.NewGuid().ToString("N"));
        public string File => Path.Combine(Root, "study-reminder-state.json");
        public DirectoryFixture() => Directory.CreateDirectory(Root);
        public void Dispose()
        {
            string full = Path.GetFullPath(Root), parent = Path.GetFullPath(Path.GetTempPath());
            if (!full.StartsWith(parent, StringComparison.OrdinalIgnoreCase) || !Path.GetFileName(full).StartsWith("study-reminder-tests-")) throw new InvalidDataException();
            Directory.Delete(full, true); // Exact unique fixture created here; never a workspace/config path.
        }
    }
    private static StudyHostState State()
    {
        var start = new DateTimeOffset(2026, 10, 12, 19, 0, 0, TimeSpan.Zero);
        var settings = new StudySettings(new("Asia/Shanghai", "2026-10-12", null, [], []), new("StatusTrend", 180),
            new(false, "OwnClass", [], 60), new(true, -40, -50, 600, 5), new(false, 5, 30, 30, true, false));
        return new(new string('a', 64), "APPLIED", "NOT_INTEGRATED", "ACTIVE", "Active", "WINDOW_ACTIVE", true,
            start.AddMinutes(10), new(start, start.AddHours(2)), null, settings,
            new("Schedule", "Active", Guid.NewGuid(), "WINDOW_ACTIVE", true, null, null, true));
    }
    private static HostRequest Claim(StudyHostState state) => new(1, Guid.NewGuid(), "study.reminder", StudyReminder:
        new(state.Display!.InstanceId, state.Version!, state.Window!, "Discipline", state.Threshold!.DisciplineId, state.Threshold.SessionId));
    private static StudyDisplayService Host(Func<StudyHostState> state, Clock time, string directory) =>
        new(() => ("binding", true, state()), time, (_, p) => p is null ? ("None", false) : ("High", true), directory);
    [Fact]
    public void Full_Host_reconstruction_uses_saved_progress_never_downtime_or_Windows_school_time()
    {
        using var fixture = new DirectoryFixture(); var clock = new Clock(); var state = State();
        var host = Host(() => state, clock, fixture.Root); var command = Claim(host.Snapshot());
        Assert.Equal("Succeeded", host.Handle(command).Outcome); Assert.True(File.Exists(fixture.File));
        clock.Ms = 300000; Assert.Null(host.Snapshot().Threshold!.DisciplineId); // 300s progress saved.
        var restartClock = new Clock { Wall = clock.Wall.AddYears(2) };
        state = state with { SchoolNow = state.SchoolNow!.Value.AddYears(2), Version = new string('b', 64), Execution = state.Execution! with { SessionId = Guid.NewGuid() } };
        var restarted = Host(() => state, restartClock, fixture.Root);
        Assert.Null(restarted.Snapshot().Threshold!.DisciplineId); Assert.Equal("Rejected", restarted.Handle(command).Outcome);
        restartClock.Ms = 299999; Assert.Null(restarted.Snapshot().Threshold!.DisciplineId);
        restartClock.Ms++; Assert.NotNull(restarted.Snapshot().Threshold!.DisciplineId);
        var newCommand = Claim(restarted.Snapshot()); Assert.Equal("Succeeded", restarted.Handle(newCommand).Outcome);
        Assert.Equal("Rejected", restarted.Handle(newCommand).Outcome);
    }
    [Fact]
    public void Checkpoint_is_bounded_and_repeated_restarts_cannot_accumulate_unobserved_time()
    {
        using var fixture = new DirectoryFixture(); var clock = new Clock(); var store = new StudyReminderStore(fixture.Root, clock);
        Assert.True(store.CanRemind("one", 600)); Assert.False(File.Exists(fixture.File)); Assert.True(store.Claim("one"));
        var first = File.ReadAllBytes(fixture.File);
        for (int i = 1; i <= 299; i++) { clock.Ms = i * 100; Assert.False(store.CanRemind("one", 600)); }
        Assert.Equal(first, File.ReadAllBytes(fixture.File)); clock.Ms = 30000; Assert.False(store.CanRemind("one", 600));
        var saved = File.ReadAllBytes(fixture.File); Assert.NotEqual(first, saved);
        for (int i = 0; i < 3; i++)
        {
            clock = new Clock { Wall = clock.Wall.AddDays(1) }; store = new(fixture.Root, clock);
            clock.Ms = 29999; Assert.False(store.CanRemind("one", 600)); Assert.Equal(saved, File.ReadAllBytes(fixture.File));
        }
        clock.Ms = 570000; Assert.True(store.CanRemind("one", 600)); var ended = File.ReadAllBytes(fixture.File);
        clock.Ms = 700000; Assert.True(store.CanRemind("one", 600)); Assert.Equal(ended, File.ReadAllBytes(fixture.File));
        Assert.True(new StudyReminderStore(fixture.Root, new Clock()).CanRemind("one", 600));
    }
    [Fact]
    public void Settings_changes_binding_hashes_and_monotonic_clock_regression_are_safe()
    {
        using var fixture = new DirectoryFixture(); var clock = new Clock(); var store = new StudyReminderStore(fixture.Root, clock);
        string scope = "binding-do-not-store-credential-shaped-value";
        Assert.True(store.Claim(scope)); Assert.True(store.CanRemind("other", 600));
        clock.Ms = 120000; Assert.False(store.CanRemind(scope, 600)); Assert.True(store.CanRemind(scope, 60));
        Assert.False(store.CanRemind(scope, 900)); clock.Ms = 100000; Assert.False(store.CanRemind(scope, 600));
        Assert.DoesNotContain(scope, File.ReadAllText(fixture.File));
        var restarted = new StudyReminderStore(fixture.Root, new Clock()); Assert.False(restarted.CanRemind(scope, 600));
        Assert.True(restarted.CanRemind("other", 600));
        Assert.False(store.CanRemind(scope, 0)); Assert.False(store.CanRemind(scope, 86401));
    }
    [Theory]
    [InlineData("garbage")] [InlineData("null")] [InlineData("{\"version\":2,\"entries\":[]}")]
    [InlineData("{\"version\":1,\"entries\":null}")] [InlineData("{\"version\":1,\"entries\":[],\"unexpected\":true}")]
    public void Damaged_or_unknown_journal_is_preserved_and_only_discipline_is_blocked(string text)
    {
        using var fixture = new DirectoryFixture(); File.WriteAllText(fixture.File, text);
        var state = State(); var host = Host(() => state, new Clock(), fixture.Root); var snapshot = host.Snapshot();
        Assert.Equal("High", snapshot.Threshold!.Band); Assert.Null(snapshot.Threshold.DisciplineId);
        Assert.Equal("STUDY_REMINDER_STORE_UNAVAILABLE", snapshot.Threshold.ReminderError);
        Assert.Equal("Active", snapshot.Execution!.State); Assert.True(snapshot.Display!.Available);
        Assert.Equal(text, File.ReadAllText(fixture.File));
    }
    [Theory]
    [InlineData("oversize")] [InlineData("duplicate")] [InlineData("negative")] [InlineData("over-age")] [InlineData("bad-hash")] [InlineData("too-many")]
    public void Journal_bounds_fail_closed_without_overwriting(string kind)
    {
        using var fixture = new DirectoryFixture(); string hash = new('a', 64);
        var entries = new JsonArray(); int count = kind == "duplicate" ? 2 : kind == "too-many" ? 17 : 1;
        for (int i = 0; i < count; i++) entries.Add(new JsonObject { ["scopeHash"] = kind == "bad-hash" ? "invalid" : kind == "too-many" ? i.ToString("x64") : hash,
            ["elapsedSeconds"] = kind == "negative" ? -1 : kind == "over-age" ? 86401 : 0 });
        string text = kind == "oversize" ? new string(' ', 8193) : new JsonObject { ["version"] = 1, ["entries"] = entries }.ToJsonString();
        File.WriteAllText(fixture.File, text); var store = new StudyReminderStore(fixture.Root, new Clock());
        Assert.NotNull(store.Error); Assert.False(store.CanRemind("one", 600)); Assert.False(store.Claim("one")); Assert.Equal(text, File.ReadAllText(fixture.File));
    }
    [Fact]
    public void Failed_atomic_save_never_grants_sound_and_keeps_previous_journal()
    {
        using var fixture = new DirectoryFixture(); var state = State(); var clock = new Clock(); var host = Host(() => state, clock, fixture.Root);
        var command = Claim(host.Snapshot()); Directory.CreateDirectory(fixture.File + ".tmp");
        var denied = host.Handle(command); Assert.Equal("Rejected", denied.Outcome); Assert.Equal("STUDY_REMINDER_STORE_UNAVAILABLE", denied.ErrorCode);
        Assert.False(File.Exists(fixture.File)); Assert.Equal("High", denied.Study!.Threshold!.Band); Assert.Equal("Active", denied.Study.Execution!.State);
        Assert.Null(host.Snapshot().Threshold!.DisciplineId);
        Directory.Delete(fixture.File + ".tmp");
        var store = new StudyReminderStore(fixture.Root, clock); Assert.True(store.Claim("binding")); var bytes = File.ReadAllBytes(fixture.File);
        Directory.CreateDirectory(fixture.File + ".tmp"); clock.Ms = 30000;
        Assert.False(store.CanRemind("binding", 600)); Assert.NotNull(store.Error); Assert.Equal(bytes, File.ReadAllBytes(fixture.File));
    }
    [Fact]
    public void Scope_history_is_bounded_and_only_fully_expired_entries_are_pruned()
    {
        using var fixture = new DirectoryFixture(); var clock = new Clock(); var store = new StudyReminderStore(fixture.Root, clock);
        for (int i = 0; i < 16; i++) Assert.True(store.Claim("scope-" + i));
        var bytes = File.ReadAllBytes(fixture.File); Assert.False(store.Claim("overflow")); Assert.Equal("STUDY_REMINDER_STORE_FULL", store.Error);
        Assert.Equal(bytes, File.ReadAllBytes(fixture.File));
        var restart = new StudyReminderStore(fixture.Root, clock); clock.Ms = 86400000;
        Assert.True(restart.Claim("replacement")); Assert.Single(JsonNode.Parse(File.ReadAllText(fixture.File))!["entries"]!.AsArray());
    }
    [Fact]
    public async Task Concurrent_App_claims_and_real_Named_Pipe_reconnect_cannot_replay_sound()
    {
        using var fixture = new DirectoryFixture(); var clock = new Clock(); var state = State(); var host = Host(() => state, clock, fixture.Root);
        var command = Claim(host.Snapshot());
        var replies = await Task.WhenAll(Enumerable.Range(0, 12).Select(_ => Task.Run(() => host.Handle(command with { RequestId = Guid.NewGuid() }))));
        Assert.Single(replies, r => r.Outcome == "Succeeded"); Assert.Equal(11, replies.Count(r => r.Outcome == "Rejected"));
        host = Host(() => state, new Clock(), fixture.Root);
        string pipe = "NPEduTools.Test.reminder-recovery." + Guid.NewGuid().ToString("N");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var server = Task.Run(async () =>
        {
            for (int i = 0; i < 2; i++)
            {
                using var stream = new NamedPipeServerStream(pipe, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await stream.WaitForConnectionAsync(timeout.Token); var request = await Protocol.ReadAsync<HostRequest>(stream, timeout.Token);
                Assert.Null(Protocol.Validate(request)); await Protocol.WriteAsync(stream, host.Handle(request), timeout.Token);
            }
        });
        var old = await HostClient.RequestAsync(pipe, command, timeout.Token); Assert.Equal("Rejected", old.Outcome);
        var observed = await HostClient.RequestAsync(pipe, new HostRequest(1, Guid.NewGuid(), "study.status"), timeout.Token);
        Assert.Equal("Succeeded", observed.Outcome); Assert.Equal("High", observed.Study!.Threshold!.Band);
        Assert.Null(observed.Study.Threshold.DisciplineId); Assert.Equal("Active", observed.Study.Execution!.State);
        await server;
    }
}

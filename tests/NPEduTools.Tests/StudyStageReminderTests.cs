using NPEduTools.Contracts;
using NPEduTools.Host;

namespace NPEduTools.Tests;

public sealed class StudyStageReminderTests
{
    [Fact]
    public void Healthy_display_wire_omits_optional_stage_error_and_fault_roundtrips()
    {
        var state = new StudyDisplayState(Guid.NewGuid(), true, true, 0);
        string healthy = System.Text.Json.JsonSerializer.Serialize(state, Protocol.Json);
        Assert.DoesNotContain("stageAudioError", healthy);
        var fault = state with { StageAudioError = "STUDY_STAGE_STORE_UNAVAILABLE" };
        Assert.Equal(fault, System.Text.Json.JsonSerializer.Deserialize<StudyDisplayState>(System.Text.Json.JsonSerializer.Serialize(fault, Protocol.Json), Protocol.Json));
    }

    private sealed class Fixture : IDisposable
    {
        public readonly string Directory = Path.Combine(Path.GetTempPath(), "study-stage-tests-" + Guid.NewGuid().ToString("N"));
        public string Scope = "private-school-device-binding";
        public bool Selected = true;
        public StudyHostState State;
        public Fixture(string phase = "Preparing")
        {
            System.IO.Directory.CreateDirectory(Directory);
            var start = new DateTimeOffset(2026, 10, 12, 19, 0, 0, TimeSpan.Zero);
            var window = new SchoolNoiseWindow(start, start.AddMinutes(150));
            State = new(new string('a', 64), "APPLIED", "NOT_INTEGRATED", "ACTIVE", phase, "fixture", true,
                phase == "Preparing" ? start.AddSeconds(-60) : phase == "Ended" ? window.End : start.AddSeconds(1),
                window, null, null);
        }
        public StudyDisplayService Host() => new(() => (Scope, Selected, State), directory: Directory);
        public static HostRequest Claim(StudyHostState state) => new(1, Guid.NewGuid(), "study.reminder",
            StudyReminder: new(state.Display!.InstanceId, state.Version!, state.Window!, state.Phase));
        public void Dispose()
        {
            string resolved = Path.GetFullPath(Directory), temp = Path.GetFullPath(Path.GetTempPath());
            Assert.StartsWith(temp, resolved, StringComparison.OrdinalIgnoreCase);
            Assert.StartsWith("study-stage-tests-", Path.GetFileName(resolved));
            System.IO.Directory.Delete(resolved, true);
        }
    }

    [Theory]
    [InlineData("Preparing")] [InlineData("Active")] [InlineData("Ended")]
    public void Durable_stage_claim_survives_Host_reconstruction_and_rejects_old_instance(string phase)
    {
        using var f = new Fixture(phase); var first = f.Host(); var old = Fixture.Claim(first.Snapshot());
        Assert.Equal("Succeeded", first.Handle(old).Outcome);
        var restarted = f.Host(); Assert.Equal("Rejected", restarted.Handle(old).Outcome);
        Assert.Equal("Suppressed", restarted.Handle(Fixture.Claim(restarted.Snapshot())).Outcome);
    }

    [Fact]
    public void Temporarily_unselected_same_binding_does_not_clear_stage_claim()
    {
        using var f = new Fixture(); var host = f.Host();
        Assert.Equal("Succeeded", host.Handle(Fixture.Claim(host.Snapshot())).Outcome);
        f.Selected = false; Assert.False(host.Snapshot().Display!.Available);
        f.Selected = true; Assert.Equal("Suppressed", host.Handle(Fixture.Claim(host.Snapshot())).Outcome);
    }

    [Fact]
    public void Stage_binding_and_window_are_independent_but_version_and_rebinding_do_not_replay()
    {
        using var f = new Fixture(); var host = f.Host();
        Assert.Equal("Succeeded", host.Handle(Fixture.Claim(host.Snapshot())).Outcome);
        f.State = f.State with { Version = new string('b', 64) };
        Assert.Equal("Suppressed", host.Handle(Fixture.Claim(host.Snapshot())).Outcome);
        string original = f.Scope; f.Scope = "another-binding";
        Assert.Equal("Succeeded", host.Handle(Fixture.Claim(host.Snapshot())).Outcome);
        f.Scope = original; Assert.Equal("Suppressed", host.Handle(Fixture.Claim(host.Snapshot())).Outcome);
        f.State = f.State with { Phase = "Active", SchoolNow = f.State.Window!.Start };
        Assert.Equal("Succeeded", host.Handle(Fixture.Claim(host.Snapshot())).Outcome);
        f.State = f.State with { Window = new(f.State.Window!.Start.AddDays(1), f.State.Window.End.AddDays(1)), SchoolNow = f.State.Window.Start.AddDays(1) };
        Assert.Equal("Succeeded", host.Handle(Fixture.Claim(host.Snapshot())).Outcome);
        string journal = File.ReadAllText(Path.Combine(f.Directory, "study-stage-reminders.json"));
        Assert.DoesNotContain(original, journal); Assert.DoesNotContain("2026-10-12", journal); Assert.DoesNotContain("Preparing", journal);
    }

    [Theory]
    [InlineData("broken")] [InlineData("null")] [InlineData("{\"version\":2,\"keys\":[]}")]
    [InlineData("{\"version\":1,\"keys\":null}")] [InlineData("{\"version\":1,\"keys\":[null]}")]
    [InlineData("{\"version\":1,\"keys\":[\"bad\"]}")] [InlineData("{\"version\":1,\"keys\":[],\"unknown\":1}")]
    public void Invalid_stage_journal_is_preserved_without_changing_capture_or_display(string journal)
    {
        using var f = new Fixture("Active"); string path = Path.Combine(f.Directory, "study-stage-reminders.json"); File.WriteAllText(path, journal);
        var session = Guid.NewGuid(); f.State = f.State with { Execution = new("Schedule", "Active", session, "WINDOW_ACTIVE", true, null, null, true) };
        var host = f.Host(); var state = host.Snapshot(); Assert.True(state.Display!.Available);
        Assert.Equal("STUDY_STAGE_STORE_UNAVAILABLE", state.Display.StageAudioError);
        var reply = host.Handle(Fixture.Claim(state)); Assert.Equal("Rejected", reply.Outcome); Assert.Same(f.State.Execution, reply.Study!.Execution);
        Assert.True(reply.Study.Display!.Available); Assert.Equal(journal, File.ReadAllText(path));
    }

    [Fact]
    public void Oversized_duplicate_or_over_capacity_journals_fail_closed()
    {
        using var f = new Fixture(); string path = Path.Combine(f.Directory, "study-stage-reminders.json");
        foreach (string content in new[] { new string('x', 16385),
            System.Text.Json.JsonSerializer.Serialize(new { version = 1, keys = new[] { new string('a', 64), new string('a', 64) } }),
            System.Text.Json.JsonSerializer.Serialize(new { version = 1, keys = Enumerable.Range(0, 129).Select(i => i.ToString("x64")) }) })
        {
            File.WriteAllText(path, content); var host = f.Host();
            Assert.Equal("Rejected", host.Handle(Fixture.Claim(host.Snapshot())).Outcome); Assert.Equal(content, File.ReadAllText(path));
        }
    }

    [Fact]
    public void Failed_atomic_write_does_not_grant_sound_or_replace_previous_journal()
    {
        using var f = new Fixture(); var host = f.Host(); Assert.Equal("Succeeded", host.Handle(Fixture.Claim(host.Snapshot())).Outcome);
        string path = Path.Combine(f.Directory, "study-stage-reminders.json"); byte[] before = File.ReadAllBytes(path);
        System.IO.Directory.CreateDirectory(path + ".tmp"); f.State = f.State with { Phase = "Active", SchoolNow = f.State.Window!.Start };
        var reply = host.Handle(Fixture.Claim(host.Snapshot()));
        Assert.Equal("Rejected", reply.Outcome); Assert.Equal("STUDY_STAGE_STORE_UNAVAILABLE", reply.ErrorCode);
        Assert.True(reply.Study!.Display!.Available); Assert.Equal(before, File.ReadAllBytes(path));
        Assert.Equal("STUDY_STAGE_STORE_UNAVAILABLE", host.Snapshot().Display!.StageAudioError);
    }

    [Fact]
    public async Task Concurrent_App_stage_claims_have_one_durable_grant()
    {
        using var f = new Fixture(); var host = f.Host(); var state = host.Snapshot();
        var replies = await Task.WhenAll(Enumerable.Range(0, 12).Select(_ => Task.Run(() => host.Handle(Fixture.Claim(state)))));
        Assert.Single(replies, reply => reply.Outcome == "Succeeded"); Assert.Equal(11, replies.Count(reply => reply.Outcome == "Suppressed"));
        var restarted = f.Host(); Assert.Equal("Suppressed", restarted.Handle(Fixture.Claim(restarted.Snapshot())).Outcome);
    }

    [Fact]
    public void Journal_preserves_existing_128_event_bound_and_duplicate_reads_do_not_rewrite()
    {
        using var f = new Fixture(); var store = new StudyStageReminderStore(f.Directory); var window = f.State.Window!;
        for (int i = 0; i < 129; i++) Assert.Equal(StudyStageClaim.Granted, store.Claim("binding-" + i, window, "Preparing"));
        string path = Path.Combine(f.Directory, "study-stage-reminders.json"); byte[] saved = File.ReadAllBytes(path);
        using var json = System.Text.Json.JsonDocument.Parse(saved); Assert.Equal(128, json.RootElement.GetProperty("keys").GetArrayLength());
        Assert.InRange(saved.Length, 1, 16384); var restarted = new StudyStageReminderStore(f.Directory);
        for (int i = 0; i < 300; i++) Assert.Equal(StudyStageClaim.Duplicate, restarted.Claim("binding-128", window, "Preparing"));
        Assert.Equal(saved, File.ReadAllBytes(path)); Assert.Equal(StudyStageClaim.Granted, restarted.Claim("binding-0", window, "Preparing"));
    }

    [Fact, System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public async Task Fresh_named_pipe_client_cannot_replay_a_stage_after_Host_reconstruction()
    {
        using var f = new Fixture(); var first = f.Host(); var old = Fixture.Claim(first.Snapshot()); Assert.Equal("Succeeded", first.Handle(old).Outcome);
        var restarted = f.Host(); string pipe = "NPEduTools.Test.stage-recovery." + Guid.NewGuid().ToString("N");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var server = Task.Run(async () =>
        {
            for (int i = 0; i < 2; i++)
            {
                using var stream = new System.IO.Pipes.NamedPipeServerStream(pipe, System.IO.Pipes.PipeDirection.InOut, 1,
                    System.IO.Pipes.PipeTransmissionMode.Byte, System.IO.Pipes.PipeOptions.Asynchronous | System.IO.Pipes.PipeOptions.CurrentUserOnly);
                await stream.WaitForConnectionAsync(timeout.Token); var request = await Protocol.ReadAsync<HostRequest>(stream, timeout.Token);
                Assert.Null(Protocol.Validate(request)); await Protocol.WriteAsync(stream, restarted.Handle(request), timeout.Token);
            }
        });
        Assert.Equal("Rejected", (await HostClient.RequestAsync(pipe, old, timeout.Token)).Outcome);
        Assert.Equal("Suppressed", (await HostClient.RequestAsync(pipe, Fixture.Claim(restarted.Snapshot()), timeout.Token)).Outcome);
        await server;
    }

    [Fact]
    public void Stage_storage_failure_does_not_disable_independent_discipline_or_capture()
    {
        using var f = new Fixture("Active"); string path = Path.Combine(f.Directory, "study-stage-reminders.json"); File.WriteAllText(path, "broken");
        var session = Guid.NewGuid(); f.State = f.State with { SchoolNow = f.State.Window!.Start.AddMinutes(10),
            Execution = new("Schedule", "Active", session, "WINDOW_ACTIVE", true, null, null, true),
            Settings = new(new("Asia/Shanghai", "2026-10-12", null, [], []), new("StatusTrend", 180), new(false, "OwnClass", [], 60),
                new(true, -40, -50, 600, 5), new(false, 5, 30, 30, false, false)) };
        var host = new StudyDisplayService(() => (f.Scope, true, f.State), thresholds: (_, policy) => policy is null ? ("None", false) : ("High", true), directory: f.Directory);
        var state = host.Snapshot(); Assert.Equal("STUDY_STAGE_STORE_UNAVAILABLE", state.Display!.StageAudioError);
        Assert.NotNull(state.Threshold!.DisciplineId);
        var claim = Fixture.Claim(state) with { StudyReminder = new(state.Display.InstanceId, state.Version!, state.Window!, "Discipline", state.Threshold.DisciplineId, session) };
        Assert.Equal("Succeeded", host.Handle(claim).Outcome); Assert.Equal("broken", File.ReadAllText(path));
        Assert.Same(f.State.Execution, host.Snapshot().Execution);
    }
}

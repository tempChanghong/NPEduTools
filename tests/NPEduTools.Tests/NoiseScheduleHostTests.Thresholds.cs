using System.Text.Json;
using NPEduTools.Contracts;
using NPEduTools.Host;

namespace NPEduTools.Tests;

public sealed partial class NoiseScheduleHostTests
{
    [Fact]
    public async Task Idle_manual_status_polls_do_not_reset_scheduled_discipline_qualification()
    {
        await using var f = new Fixture(); var config = StudyConfig(f);
        config["effective"]!["settings"]!["noise"]!["redLineDbfs"] = -70;
        config["effective"]!["settings"]!["noise"]!["mediumLineDbfs"] = -80;
        long sequence = 0; var connection = Guid.NewGuid(); var bridge = Guid.NewGuid();
        await using var study = new StudySettingsService(() => new(connection, bridge, ++sequence, 0,
            f.Base.AddMilliseconds(f.Time.Ms), 0, "Advancing", "synthetic"), () => false, f.Time, false, f.Scheduler);
        study.BindStudy(f.Scope); study.ApplyStudy(config); study.ConfirmStudyExecution(config, Grant(config)); await f.Start();
        var display = new StudyDisplayService(study.DisplaySnapshot, f.Time, f.Noise.StudyThresholds);
        await using var manual = new ManualStudyService(f.Noise, study.DisplaySnapshot, () => true,
            System.IO.Path.Combine(f.Path, "manual"), f.Time, false);
        var session = f.Noise.Snapshot().SessionId;
        f.Captures[0].Sample(); display.Snapshot();
        for (int i = 0; i < 60; i++)
        {
            f.Tick(100); f.Captures[0].Sample(); display.Snapshot();
            var idle = manual.Handle(new(1, Guid.NewGuid(), "manual.study.status"));
            Assert.Equal("Idle", idle.ManualStudy!.State); Assert.Null(idle.Study!.Threshold);
        }
        var state = display.Snapshot();
        Assert.NotNull(state.Threshold!.DisciplineId);
        Assert.True(f.Noise.StudyThresholds(session, state.Settings!.Noise).Qualified);
        Assert.Equal("Active", f.Noise.Snapshot().State); Assert.Single(f.Captures);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task Unrelated_threshold_observers_cannot_reset_the_current_study_owner(bool manual)
    {
        await using var f = new Fixture(); var id = Guid.NewGuid();
        var started = manual ? f.Noise.StartManualStudy(id, "synthetic", 10800) : f.Noise.StartScheduled(id);
        Assert.Equal("Accepted", started.Outcome); await Until(() => f.Noise.Snapshot().State == "Active");
        var policy = new StudyNoise(true, -70, -80, 600, 3);
        f.Noise.StudyThresholds(id, policy);
        for (int i = 0; i < 40; i++)
        {
            f.Time.Ms += 100; f.Captures[0].Sample();
            Assert.Equal(("None", false), f.Noise.StudyThresholds(null, null));
            Assert.Equal(("None", false), f.Noise.StudyThresholds(Guid.NewGuid(), null));
            Assert.Equal(("None", false), f.Noise.StudyThresholds(Guid.NewGuid(), policy));
        }
        Assert.Equal(("High", true), f.Noise.StudyThresholds(id, policy));
        // The current owner still can withdraw its policy. Earlier high frames
        // cannot be reused when the policy is applied again.
        Assert.Equal(("None", false), f.Noise.StudyThresholds(id, null));
        f.Noise.StudyThresholds(id, policy);
        f.Time.Ms += 100; f.Captures[0].Sample();
        Assert.Equal(("High", false), f.Noise.StudyThresholds(id, policy));
        for (int i = 0; i < 29; i++) { f.Time.Ms += 100; f.Captures[0].Sample(); }
        Assert.Equal(("High", true), f.Noise.StudyThresholds(id, policy));
        Assert.Single(f.Captures); Assert.Equal(id, f.Noise.Snapshot().SessionId);
    }

    [Fact]
    public async Task An_ineligible_display_releases_only_its_own_previously_observed_threshold_session()
    {
        await using var f = new Fixture(); var id = Guid.NewGuid();
        Assert.Equal("Accepted", f.Noise.StartScheduled(id).Outcome); await Until(() => f.Noise.Snapshot().State == "Active");
        var policy = new StudyNoise(true, -70, -80, 600, 3);
        var window = new SchoolNoiseWindow(f.Base, f.Base.AddHours(1));
        var settings = StudyConfig(f)["effective"]!["settings"]!.Deserialize<StudySettings>(Protocol.Json)!;
        var state = new StudyHostState("version", "APPLIED", "", "ACTIVE", "Active", "WINDOW_ACTIVE", true,
            f.Base.AddMinutes(1), window, null, settings with { Noise = policy },
            new("Schedule", "Active", id, "WINDOW_ACTIVE", true, null, null, true));
        var display = new StudyDisplayService(() => (f.Scope, true, state), f.Time, f.Noise.StudyThresholds);
        display.Snapshot();
        for (int i = 0; i < 40; i++) { f.Time.Ms += 100; f.Captures[0].Sample(); }
        Assert.NotNull(display.Snapshot().Threshold!.DisciplineId);
        state = state with { Phase = "Blocked", ClockReady = false };
        Assert.Null(display.Snapshot().Threshold);
        state = state with { Phase = "Active", ClockReady = true };
        Assert.Null(display.Snapshot().Threshold!.DisciplineId);
        for (int i = 0; i < 30; i++) { f.Time.Ms += 100; f.Captures[0].Sample(); }
        Assert.NotNull(display.Snapshot().Threshold!.DisciplineId);
    }

    [Theory]
    [InlineData("offline", false)] [InlineData("exam", false)] [InlineData("pause", false)]
    [InlineData("unpair", false)] [InlineData("revoked", false)] [InlineData("clock", false)]
    [InlineData("configuration-stale", false)] [InlineData("noise-off", false)] [InlineData("policy-edit", false)]
    [InlineData("offline", true)] [InlineData("exam", true)] [InlineData("pause", true)]
    [InlineData("unpair", true)] [InlineData("revoked", true)] [InlineData("clock", true)]
    [InlineData("configuration-stale", true)] [InlineData("noise-off", true)] [InlineData("policy-edit", true)]
    public async Task Combined_study_lifecycle_preempts_manual_then_withdraws_old_discipline(string change, bool manual)
    {
        await using var f = new Fixture(); var config = StudyConfig(f);
        config["effective"]!["settings"]!["noise"]!["redLineDbfs"] = -70;
        config["effective"]!["settings"]!["noise"]!["mediumLineDbfs"] = -80;
        if (manual)
        { f.Noise.Handle(f.Command("start", "synthetic")); await Until(() => f.Noise.Snapshot().State == "Active"); }
        long sequence = 0; var connection = Guid.NewGuid(); var bridge = Guid.NewGuid();
        await using var study = new StudySettingsService(() => new(connection, bridge, ++sequence, 0,
            f.Base.AddMilliseconds(f.Frozen ? 0 : f.Time.Ms), 0, "Advancing", "synthetic"), () => f.Exam, f.Time, false, f.Scheduler);
        study.BindStudy(f.Scope); study.ApplyStudy(config); study.ConfirmStudyExecution(config, Grant(config)); await f.Start();
        int expectedCaptures=manual?2:1; if(manual){Assert.True(f.Captures[0].Disposed);manual=false;}
        var display = new StudyDisplayService(study.DisplaySnapshot, f.Time, f.Noise.StudyThresholds, f.Path);
        f.Captures[^1].Sample(); display.Snapshot();
        // The real Host polls school time and presentation continuously; do not jump five
        // seconds between school-clock observations and inadvertently test clock rejection.
        for (int i = 0; i < 60; i++) { f.Tick(100); f.Captures[^1].Sample(); study.Tick(); display.Snapshot(); }
        var before = display.Snapshot(); var session = f.Noise.Snapshot().SessionId;
        Assert.True(before.ClockReady); Assert.Equal("Active", before.Phase);
        if (manual) Assert.Null(before.Threshold);
        else Assert.NotNull(before.Threshold!.DisciplineId);
        var old = new HostRequest(1, Guid.NewGuid(), "study.reminder", StudyReminder: new(before.Display!.InstanceId,
            before.Version!, before.Window!, "Discipline", before.Threshold?.DisciplineId ?? Guid.NewGuid(), session));
        Assert.Null(Protocol.Validate(old)); Assert.False(Protocol.NoiseInterruption(old));

        switch (change)
        {
            case "offline": study.StudyFailed("STUDY_OFFLINE"); break;
            case "exam": f.Exam = true; break;
            case "pause": study.BindStudy(null); break;
            case "unpair": study.BindStudy(null); f.Scheduler.Bind(null, false); break;
            case "revoked": study.StudyFailed("DEVICE_SUSPENDED"); break;
            case "clock": f.Frozen = true; break;
            case "configuration-stale":
                while (f.Time.Ms < 15000) { f.Tick(100); f.Captures[^1].Sample(); study.Tick(); }
                break;
            default:
                var edited = config.DeepClone().AsObject(); edited["version"] = new string('c', 64);
                if (change == "noise-off") edited["effective"]!["settings"]!["noise"]!["enabled"] = false;
                else edited["effective"]!["settings"]!["schedule"]!["rules"]![0]!["start"] = "19:20";
                study.ApplyStudy(edited); break;
        }
        f.Tick(100); study.Tick(); var after = display.Snapshot();
        if (change is "offline" or "configuration-stale")
        {
            Assert.Equal("OFFLINE_CONTINUING", after.Reason); Assert.True(after.Display!.Available);
            Assert.Equal(before.Display!.InstanceId, after.Display.InstanceId); Assert.Null(study.ObserveStudy());
            Assert.NotNull(after.Threshold!.DisciplineId);
            if (change == "offline") Assert.Equal(before.Threshold!.DisciplineId, after.Threshold.DisciplineId);
            var current = old with { StudyReminder = old.StudyReminder! with { DisciplineId = after.Threshold.DisciplineId } };
            Assert.Equal("Succeeded", display.Handle(current).Outcome);
            Assert.NotEqual("Succeeded", display.Handle(current with { RequestId = Guid.NewGuid() }).Outcome);
            Assert.Equal(session, f.Noise.Snapshot().SessionId); Assert.Equal(expectedCaptures, f.Captures.Count);
            return;
        }
        Assert.Null(after.Threshold); Assert.Equal("Rejected", display.Handle(old).Outcome);
        Assert.False(File.Exists(System.IO.Path.Combine(f.Path, "study-reminder-state.json")));
        bool captureContinues = manual || change is "offline" or "configuration-stale";
        if (captureContinues)
        {
            f.Captures[^1].Sample(); after = display.Snapshot();
            Assert.Equal("Active", f.Noise.Snapshot().State); Assert.Equal(session, f.Noise.Snapshot().SessionId);
            Assert.False(f.Captures[^1].Disposed); Assert.Null(after.Threshold);
            if (!manual) { Assert.Equal("UNAVAILABLE", after.PolicyState); Assert.True(after.Execution!.SignalFresh); }
        }
        else
        { await Until(() => f.Noise.Snapshot().State == "Stopped"); Assert.True(f.Captures[^1].Disposed); }
        if (change != "unpair") f.Scheduler.Confirm(f.Policy());
        f.Tick(100); Assert.Equal(expectedCaptures,f.Captures.Count); // Legacy confirmation must not reopen a selected study microphone.
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task Actual_capture_frames_qualify_only_scheduled_owner_and_quiet_between_UI_polls_resets(bool manual)
    {
        await using var f = new Fixture(); var config = StudyConfig(f);
        config["effective"]!["settings"]!["noise"]!["redLineDbfs"] = -70;
        config["effective"]!["settings"]!["noise"]!["mediumLineDbfs"] = -80;
        if (manual) { f.Noise.Handle(f.Command("start", "synthetic")); await Until(() => f.Noise.Snapshot().State == "Active"); }
        Select(f, config); await f.Start();int expectedCaptures=manual?2:1;if(manual){Assert.True(f.Captures[0].Disposed);manual=false;}
        var settings = config["effective"]!["settings"]!.Deserialize<StudySettings>(Protocol.Json)!;
        StudyHostState Sample()
        {
            var execution = f.Scheduler.ObserveStudyExecution()!.Deserialize<StudyCaptureState>(Protocol.Json)!;
            var start = f.Base;
            return new(config["version"]!.GetValue<string>(), "APPLIED", "NOT_INTEGRATED", "ACTIVE", "Active", "WINDOW_ACTIVE", true,
                start.AddMilliseconds(f.Time.Ms), new(start, start.AddHours(1)), null, settings, execution);
        }
        var display = new StudyDisplayService(() => (f.Scope, true, Sample()), f.Time, f.Noise.StudyThresholds);
        f.Captures[^1].Sample(); display.Snapshot(); // Configures observation; preceding frames are not retrospectively qualified.
        for (int i = 0; i < 49; i++) { f.Tick(100); f.Captures[^1].Sample(); }
        Assert.Null(display.Snapshot().Threshold?.DisciplineId);
        f.Captures[^1].Silence(); f.Captures[^1].Sample(); // Both between the two display polls.
        f.Tick(100); f.Captures[^1].Sample(); Assert.Null(display.Snapshot().Threshold?.DisciplineId);
        for (int i = 0; i < 48; i++) { f.Tick(100); f.Captures[^1].Sample(); }
        var state = display.Snapshot();
        if (manual) { Assert.Null(state.Threshold); Assert.Equal("Manual", state.Execution!.Owner); }
        else
        {
            Assert.NotNull(state.Threshold!.DisciplineId);
            var command = new HostRequest(1, Guid.NewGuid(), "study.reminder", StudyReminder:
                new(state.Display!.InstanceId, state.Version!, state.Window!, "Discipline", state.Threshold.DisciplineId, state.Threshold.SessionId));
            Assert.Equal("Succeeded", display.Handle(command).Outcome);
            Assert.Equal("Rejected", display.Handle(command).Outcome);
            f.Tick(1001); Assert.Null(display.Snapshot().Threshold); // Frozen frame cannot keep edge or notice offers alive.
            f.Captures[^1].Sample(); Assert.Null(display.Snapshot().Threshold!.DisciplineId);
        }
        Assert.Equal(expectedCaptures,f.Captures.Count); Assert.Equal("Active", f.Noise.Snapshot().State); // Observing / reminding never opens another microphone.
    }
}

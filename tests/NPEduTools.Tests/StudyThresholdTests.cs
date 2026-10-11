using NPEduTools.Contracts;
using NPEduTools.Core;
using NPEduTools.Host;

namespace NPEduTools.Tests;

public sealed class StudyThresholdTests
{
    private sealed class Clock : TimeProvider
    { public long Ms; public override long TimestampFrequency => 1000; public override long GetTimestamp() => Ms; }
    private static readonly StudyNoise Policy = new(true, -40, -50, 600, 5);
    private static readonly Guid Session = Guid.NewGuid();
    private static NoiseFrame Frame(double level) => new(.1, Math.Pow(10, level / 10), Math.Pow(10, level / 20), 0, false);
    private static void Emit(StudyNoiseThresholds tracker, Clock clock, int count, double level = -35)
    { for (int i = 0; i < count; i++) { clock.Ms += 100; tracker.Receive(Session, Frame(level), level, false); } }
    [Fact]
    public void Every_100ms_PCM_frame_counts_and_thresholds_are_inclusive()
    {
        var clock = new Clock(); var tracker = new StudyNoiseThresholds(clock); tracker.Configure(Session, Policy);
        int emitted = 0;
        var meter = new NoiseMeter(8000, 1, 16, false, frame =>
        { emitted++; clock.Ms += 100; tracker.Receive(Session, frame, NoiseMeter.Dbfs(Math.Sqrt(frame.MeanSquare)), frame.Invalid); });
        byte[] pcm = new byte[800 * 2];
        for (int i = 0; i < 800; i++) System.Buffers.Binary.BinaryPrimitives.WriteInt16LittleEndian(pcm.AsSpan(i * 2), 1000);
        for (int i = 0; i < 49; i++) meter.Push(pcm);
        Assert.Equal("High", tracker.Snapshot().Band); Assert.False(tracker.Snapshot().Qualified);
        meter.Push(pcm); Assert.Equal(50, emitted); Assert.True(tracker.Snapshot().Qualified);
        Emit(tracker, clock, 1, -50); Assert.Equal(("Medium", false), tracker.Snapshot());
        Emit(tracker, clock, 1, -40); Assert.Equal("High", tracker.Snapshot().Band);
        Emit(tracker, clock, 1, -50.01); Assert.Equal(("None", false), tracker.Snapshot());
    }
    [Theory]
    [InlineData("quiet")] [InlineData("silence")] [InlineData("invalid")] [InlineData("gap")]
    public void Any_intervening_frame_or_gap_restarts_continuity(string interruption)
    {
        var clock = new Clock(); var tracker = new StudyNoiseThresholds(clock); tracker.Configure(Session, Policy);
        Emit(tracker, clock, 49);
        if (interruption == "gap") clock.Ms += 501;
        else { clock.Ms += 100; tracker.Receive(Session, Frame(-60), interruption == "silence" ? null : -60, interruption == "invalid"); }
        Emit(tracker, clock, 1); Assert.False(tracker.Snapshot().Qualified);
        Emit(tracker, clock, 49); Assert.True(tracker.Snapshot().Qualified);
    }
    [Fact]
    public void Cached_reads_bursts_stale_frames_and_new_sessions_cannot_qualify()
    {
        var clock = new Clock(); var tracker = new StudyNoiseThresholds(clock); tracker.Configure(Session, Policy);
        for (int i = 0; i < 100; i++) tracker.Receive(Session, Frame(-35), -35, false);
        Assert.False(tracker.Snapshot().Qualified); clock.Ms = 1000; Assert.Equal(("None", false), tracker.Snapshot());
        tracker.Configure(Guid.NewGuid(), Policy); Emit(tracker, clock, 50); Assert.Equal(("None", false), tracker.Snapshot());
        tracker.Configure(Session, Policy); Emit(tracker, clock, 49); tracker.Configure(Session, Policy with { RedLineDbfs = -30 });
        Emit(tracker, clock, 1); Assert.Equal(("Medium", false), tracker.Snapshot());
        tracker.Configure(null, null); Emit(tracker, clock, 100); Assert.Equal(("None", false), tracker.Snapshot());
    }
    private static StudyHostState State()
    {
        var start = new DateTimeOffset(2026, 10, 12, 19, 0, 0, TimeSpan.Zero);
        var settings = new StudySettings(new("Asia/Shanghai", "2026-10-12", null, [], []), new("StatusTrend", 180),
            new(false, "OwnClass", [], 60), Policy, new(false, 5, 30, 30, false, false));
        return new(new string('a', 64), "APPLIED", "NOT_INTEGRATED", "ACTIVE", "Active", "WINDOW_ACTIVE", true,
            start.AddMinutes(10), new(start, start.AddMinutes(150)), null, settings,
            new("Schedule", "Active", Session, "WINDOW_ACTIVE", true, null, null, true));
    }
    private static HostRequest Claim(StudyHostState state) => new(1, Guid.NewGuid(), "study.reminder", StudyReminder:
        new(state.Display!.InstanceId, state.Version!, state.Window!, "Discipline", state.Threshold?.DisciplineId, state.Threshold?.SessionId));
    [Fact]
    public void Host_claims_are_single_owner_and_monotonic_cooldown_survives_App_config_and_session_changes()
    {
        var clock = new Clock(); var state = State();
        var host = new StudyDisplayService(() => ("school", true, state), clock, (_, p) => p is null ? ("None", false) : ("High", true));
        var claim = Claim(host.Snapshot()); Assert.Null(Protocol.Validate(claim));
        Assert.Equal("Succeeded", host.Handle(claim).Outcome); Assert.Equal("Rejected", host.Handle(claim).Outcome);
        clock.Ms = 599999; Assert.Null(host.Snapshot().Threshold!.DisciplineId);
        state = state with { Version = new string('b', 64), Execution = state.Execution! with { SessionId = Guid.NewGuid() } };
        Assert.Null(host.Snapshot().Threshold!.DisciplineId);
        clock.Ms++; Assert.NotNull(host.Snapshot().Threshold!.DisciplineId);
        Assert.Equal("Rejected", host.Handle(claim).Outcome);
        Assert.Equal("Succeeded", host.Handle(Claim(host.Snapshot())).Outcome);
        state = state with { Settings = state.Settings! with { Noise = Policy with { DisciplineCooldownSeconds = 1 } } };
        clock.Ms += 999; Assert.Null(host.Snapshot().Threshold!.DisciplineId); clock.Ms++; Assert.NotNull(host.Snapshot().Threshold!.DisciplineId);
    }
    [Theory]
    [InlineData("manual")] [InlineData("clock")] [InlineData("policy")] [InlineData("preparing")]
    [InlineData("stale")] [InlineData("unset")] [InlineData("disabled")] [InlineData("hidden")]
    public void Non_scheduled_or_unavailable_conditions_do_not_offer_reminders(string condition)
    {
        var state = State();
        var host = new StudyDisplayService(() => ("school", true, state), thresholds: (_, p) => p is null ? ("None", false) : ("High", true));
        var old = Claim(host.Snapshot());
        state = condition switch
        {
            "manual" => state with { Execution = state.Execution! with { Owner = "Manual" } },
            "clock" => state with { ClockReady = false }, "policy" => state with { PolicyState = "UNAVAILABLE" },
            "preparing" => state with { Phase = "Preparing" }, "stale" => state with { Execution = state.Execution! with { SignalFresh = false } },
            "unset" => state with { Settings = state.Settings! with { Noise = Policy with { RedLineDbfs = null, MediumLineDbfs = null } } },
            "disabled" => state with { Settings = state.Settings! with { Noise = Policy with { Enabled = false } } }, _ => state
        };
        if (condition == "hidden") host.Handle(new(1, Guid.NewGuid(), "study.hide", StudyHide: new(old.StudyReminder!.InstanceId, state.Version!, state.Window!)));
        Assert.Null(host.Snapshot().Threshold?.DisciplineId); Assert.Equal("Rejected", host.Handle(old).Outcome);
    }
    [Fact]
    public void Expired_offers_scope_changes_start_priority_and_invalid_commands_are_fenced()
    {
        var clock = new Clock(); var state = State(); string scope = "one";
        var host = new StudyDisplayService(() => (scope, true, state), clock, (_, p) => p is null ? ("None", false) : ("High", true));
        var old = Claim(host.Snapshot()); clock.Ms = 5000; Assert.Equal("Rejected", host.Handle(old).Outcome);
        var current = Claim(host.Snapshot()); Assert.Equal("Succeeded", host.Handle(current).Outcome);
        scope = "two"; Assert.NotNull(host.Snapshot().Threshold!.DisciplineId); Assert.Equal("Rejected", host.Handle(current).Outcome);
        state = state with { SchoolNow = state.Window!.Start.AddSeconds(4.999) }; Assert.Null(host.Snapshot().Threshold!.DisciplineId);
        state = state with { SchoolNow = state.Window!.Start.AddSeconds(5) }; Assert.NotNull(host.Snapshot().Threshold!.DisciplineId);
        var invalid = Claim(host.Snapshot()) with { StudyReminder = current.StudyReminder! with { DisciplineId = Guid.Empty } };
        Assert.Equal("InvalidStudyReminderCommand", Protocol.Validate(invalid));
        Assert.Equal("InvalidStudyReminderCommand", Protocol.Validate(invalid with { StudyReminder = current.StudyReminder! with { Phase = "Active" } }));
    }
    [Fact]
    public void Discipline_lasts_five_seconds_and_stage_transitions_freshness_and_manual_ownership_take_priority()
    {
        var clock = new Clock(); var timeline = new StudyPresentationTimeline(clock);
        var state = State() with { Display = new(Guid.NewGuid(), true, true, 0) };
        timeline.Apply(state); Assert.True(timeline.BeginDiscipline()); Assert.Equal("Discipline", timeline.Notice);
        for (int i = 1; i < 5; i++) { clock.Ms += 1000; timeline.Apply(state); Assert.Equal("Discipline", timeline.Notice); }
        clock.Ms += 1000; timeline.Apply(state); Assert.Null(timeline.Notice);
        Assert.True(timeline.BeginDiscipline()); timeline.Apply(state with { Phase = "Ended", SchoolNow = state.Window!.End }); Assert.Equal("Ended", timeline.Notice);
        Assert.False(timeline.BeginDiscipline()); timeline.Suspend(); Assert.Null(timeline.Notice);
        timeline.Apply(state); Assert.True(timeline.BeginDiscipline()); timeline.Apply(state with { Execution = state.Execution! with { Owner = "Manual" } }); Assert.Null(timeline.Notice);
        timeline.Apply(state); Assert.True(timeline.BeginDiscipline()); clock.Ms += 1500; Assert.Null(timeline.Notice);
    }
}

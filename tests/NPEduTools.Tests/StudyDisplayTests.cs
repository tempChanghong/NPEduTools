using NPEduTools.Contracts;
using NPEduTools.Core;
using NPEduTools.Host;

namespace NPEduTools.Tests;

public sealed class StudyDisplayTests
{
    private sealed class Clock : TimeProvider
    {
        public long Ms;
        public override long TimestampFrequency => 1000;
        public override long GetTimestamp() => Ms;
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.MinValue; // Wall clock must never control presentation.
    }
    private static readonly SchoolNoiseWindow Window = new(DateTimeOffset.Parse("2026-10-12T19:00:00Z"), DateTimeOffset.Parse("2026-10-12T21:30:00Z"));
    private static readonly StudySettings Settings = new(new("Asia/Shanghai", "2026-10-12", null, [], []), new("StatusTrend", 180),
        new(true, "OwnClass", [], 60), new(true, -40, -50, 600, 5), new(false, 5, 30, 30, true, false));
    private static StudyHostState State(string phase = "Active", double seconds = 1) => new(new string('a', 64), "APPLIED", "NOT_INTEGRATED", "ACTIVE",
        phase, "WINDOW_ACTIVE", true, Window.Start.AddSeconds(seconds), Window, null, Settings,
        new("Schedule", "Active", Guid.NewGuid(), "WINDOW_ACTIVE", true, null, null, true));
    private static HostRequest Hide(StudyHostState state, Guid? request = null) => new(1, request ?? Guid.NewGuid(), "study.hide",
        StudyHide: new(state.Display!.InstanceId, state.Version!, state.Window!));
    private static HostRequest Reminder(StudyHostState state) => new(1, Guid.NewGuid(), "study.reminder",
        StudyReminder: new(state.Display!.InstanceId, state.Version!, state.Window!, state.Phase));

    [Fact]
    public void Host_reminder_claim_is_once_per_window_across_App_restarts_and_config_edits()
    {
        var state = State("Preparing", -120); var service = new StudyDisplayService(() => ("scope", true, state));
        var claim = Reminder(service.Snapshot()); Assert.Null(Protocol.Validate(claim));
        Assert.Equal("Succeeded", service.Handle(claim).Outcome);
        Assert.Equal("Suppressed", service.Handle(Reminder(service.Snapshot())).Outcome);
        state = state with { Version = new string('b', 64) };
        Assert.Equal("Suppressed", service.Handle(Reminder(service.Snapshot())).Outcome);
        state = state with { Phase = "Active", SchoolNow = Window.Start };
        Assert.Equal("Succeeded", service.Handle(Reminder(service.Snapshot())).Outcome);
        Assert.Equal("Suppressed", service.Handle(Reminder(service.Snapshot())).Outcome);
        state = state with { Phase = "Outside", Window = null, SchoolNow = Window.End };
        Assert.Equal("Succeeded", service.Handle(Reminder(service.Snapshot())).Outcome);
        Assert.Equal("Suppressed", service.Handle(Reminder(service.Snapshot())).Outcome);
    }
    [Theory]
    [InlineData("Preparing", -121)] [InlineData("Preparing", 0)]
    [InlineData("Active", -1)] [InlineData("Active", 5)]
    [InlineData("Ended", 8999)] [InlineData("Ended", 9005)]
    public void Delayed_or_wrong_window_reminder_is_never_authorized(string phase, int seconds)
    {
        var state = State(phase, seconds); var service = new StudyDisplayService(() => ("scope", true, state));
        Assert.Equal("Rejected", service.Handle(Reminder(service.Snapshot())).Outcome);
    }
    [Fact]
    public void Hidden_study_does_not_play_a_stage_sound_in_another_window()
    {
        var state = State(); var service = new StudyDisplayService(() => ("scope", true, state));
        service.Handle(Hide(service.Snapshot())); Assert.Equal("Rejected", service.Handle(Reminder(service.Snapshot())).Outcome);
    }

    [Fact]
    public void Repeated_hide_and_new_App_client_cannot_extend_the_Host_deadline()
    {
        var clock = new Clock(); var state = State(); var service = new StudyDisplayService(() => ("scope", true, state), clock);
        var first = Hide(service.Snapshot()); Assert.Null(Protocol.Validate(first));
        Assert.Equal(180, service.Handle(first).Study!.Display!.HiddenRemainingSeconds);
        clock.Ms = 100000;
        Assert.Equal(80, service.Handle(Hide(service.Snapshot())).Study!.Display!.HiddenRemainingSeconds);
        clock.Ms = 180000;
        Assert.True(service.Snapshot().Display!.Available);
        Assert.Equal(0, service.Handle(first).Study!.Display!.HiddenRemainingSeconds);
    }
    [Fact]
    public void Config_change_inside_same_window_cannot_extend_hide_and_old_commands_are_rejected()
    {
        var clock = new Clock(); var state = State(); var service = new StudyDisplayService(() => ("scope", true, state), clock);
        var old = Hide(service.Snapshot()); service.Handle(old); clock.Ms = 60000;
        state = state with { Version = new string('b', 64) };
        Assert.Equal(120, service.Snapshot().Display!.HiddenRemainingSeconds);
        Assert.Equal("STUDY_STATE_CHANGED", service.Handle(old).ErrorCode);
    }
    [Theory]
    [InlineData("Preparing")]
    [InlineData("Outside")]
    [InlineData("Blocked")]
    [InlineData("Disabled")]
    public void Hide_is_not_a_stop_or_an_authorization_for_other_phases(string phase)
    {
        var state = State(phase); var service = new StudyDisplayService(() => ("scope", true, state));
        var response = service.Handle(Hide(service.Snapshot()));
        Assert.Equal("Rejected", response.Outcome); Assert.Same(state.Execution, response.Study!.Execution);
    }
    [Fact]
    public void Stale_config_fences_legacy_display_and_cannot_show_or_hide()
    {
        var state = State() with { PolicyState = "UNAVAILABLE", ClockReady = false, Window = null };
        var service = new StudyDisplayService(() => ("scope", true, state));
        Assert.True(service.Snapshot().Display!.Selected); Assert.False(service.Snapshot().Display!.Available);
    }
    [Fact]
    public void End_is_shown_for_five_school_seconds_only_after_observed_active_window()
    {
        var state = State(); var service = new StudyDisplayService(() => ("scope", true, state)); service.Snapshot();
        state = state with { Phase = "Outside", Window = null, SchoolNow = Window.End };
        Assert.Equal("Ended", service.Snapshot().Phase);
        state = state with { SchoolNow = Window.End.AddSeconds(4.999) }; Assert.True(service.Snapshot().Display!.Available);
        state = state with { SchoolNow = Window.End.AddSeconds(5) }; Assert.False(service.Snapshot().Display!.Available);
        var restarted = new StudyDisplayService(() => ("scope", true, state));
        state = state with { SchoolNow = Window.End }; Assert.Equal("Outside", restarted.Snapshot().Phase);
    }
    [Fact]
    public void Exam_clock_loss_and_policy_replacement_do_not_generate_end_notices()
    {
        foreach (var replacement in new[] { State("Blocked") with { Reason = "EXAM_PAUSED" }, State() with { ClockReady = false }, State("Outside") with { Version = new string('b', 64), Window = null, SchoolNow = Window.End } })
        {
            var state = State(); var service = new StudyDisplayService(() => ("scope", true, state)); service.Snapshot();
            state = replacement; service.Snapshot();
            state = state with { Phase = "Outside", Window = null, ClockReady = true, SchoolNow = Window.End };
            Assert.Equal("Outside", service.Snapshot().Phase);
        }
    }
    [Fact]
    public void Scope_and_Host_replacement_fence_old_hide_requests()
    {
        var state = State(); string scope = "one";
        var service = new StudyDisplayService(() => (scope, true, state)); var old = Hide(service.Snapshot()); service.Handle(old);
        scope = "two"; Assert.Equal(0, service.Snapshot().Display!.HiddenRemainingSeconds);
        // Same times/version can belong to a different device binding. The old Host-instance id is insufficient.
        Assert.Equal("STUDY_STATE_CHANGED", service.Handle(old).ErrorCode);
        Assert.Equal("STUDY_STATE_CHANGED", new StudyDisplayService(() => (scope, true, state)).Handle(old).ErrorCode);
    }
    [Theory]
    [InlineData(60)] [InlineData(180)] [InlineData(999)]
    public void Configured_hide_is_bounded(int seconds)
    {
        var state = State() with { Settings = Settings with { Presentation = new("StatusTrend", seconds) } };
        var service = new StudyDisplayService(() => ("scope", true, state));
        Assert.InRange(service.Handle(Hide(service.Snapshot())).Study!.Display!.HiddenRemainingSeconds, 1, 180);
    }
    [Fact]
    public void Preparation_is_ten_seconds_then_start_and_end_have_five_second_windows()
    {
        var clock = new Clock(); var timeline = new StudyPresentationTimeline(clock);
        var state = State("Preparing", -120) with { Display = new(Guid.NewGuid(), true, true, 0) };
        timeline.Apply(state); Assert.Equal("Preparing", timeline.Notice); Assert.Equal("Preparing", timeline.TakeCue());
        for (int second = 1; second <= 119; second++)
        {
            clock.Ms = second * 1000; timeline.Apply(state with { SchoolNow = Window.Start.AddSeconds(second - 120) });
            Assert.Equal(second < 10 ? "Preparing" : null, timeline.Notice); Assert.Null(timeline.TakeCue());
        }
        clock.Ms = 120000; state = state with { Phase = "Active", SchoolNow = Window.Start }; timeline.Apply(state);
        Assert.Equal("Active", timeline.Notice); Assert.Equal("Active", timeline.TakeCue());
        for (int second = 1; second <= 5; second++)
        { clock.Ms += 1000; timeline.Apply(state with { SchoolNow = Window.Start.AddSeconds(second) }); Assert.Equal(second < 5 ? "Active" : null, timeline.Notice); }
        state = state with { Phase = "Ended", SchoolNow = Window.End }; timeline.Apply(state);
        Assert.Equal("Ended", timeline.Notice); Assert.Equal("Ended", timeline.TakeCue());
        clock.Ms += 5000; timeline.Apply(state with { SchoolNow = Window.End.AddSeconds(5) }); Assert.Null(timeline.Notice);
    }
    [Fact]
    public void Reopen_mid_session_does_not_reannounce_start_or_advance_school_time()
    {
        var clock = new Clock(); var timeline = new StudyPresentationTimeline(clock); var state = State(seconds: 600);
        timeline.Apply(state); Assert.Null(timeline.Notice); Assert.Null(timeline.TakeCue());
        clock.Ms = 1000; Assert.Equal(state.SchoolNow, timeline.State!.SchoolNow); Assert.True(timeline.Fresh);
        clock.Ms = 1500; Assert.False(timeline.Fresh); Assert.Null(timeline.Notice);
        timeline.Apply(state); timeline.Suspend(); Assert.False(timeline.Fresh); Assert.Null(timeline.TakeCue());
    }
    [Fact]
    public void Slow_transport_does_not_renew_observation_freshness_or_play_notices()
    {
        var clock = new Clock(); var timeline = new StudyPresentationTimeline(clock); var state = State("Preparing", -120);
        timeline.Apply(state, TimeSpan.FromSeconds(2)); Assert.False(timeline.Fresh); Assert.Null(timeline.Notice); Assert.Null(timeline.TakeCue());
        timeline.Apply(state, TimeSpan.FromSeconds(1)); Assert.True(timeline.Fresh);
        clock.Ms = 500; Assert.False(timeline.Fresh);
        timeline.Apply(state, TimeSpan.FromSeconds(-1)); Assert.False(timeline.Fresh);
    }
    [Fact]
    public void Preparation_visual_returns_after_priority_or_disconnection_without_replaying_audio()
    {
        var clock = new Clock(); var timeline = new StudyPresentationTimeline(clock); var state = State("Preparing", -120);
        timeline.Apply(state); Assert.Equal("Preparing", timeline.TakeCue()); timeline.Suspend();
        clock.Ms = 5000; timeline.Apply(state with { SchoolNow = Window.Start.AddSeconds(-115) });
        Assert.Equal("Preparing", timeline.Notice); Assert.Null(timeline.TakeCue());
    }
    [Fact]
    public void Delayed_start_observation_only_shows_remaining_seconds_and_no_replay_after_hide()
    {
        var clock = new Clock(); var timeline = new StudyPresentationTimeline(clock); var state = State(seconds: 4);
        timeline.Apply(state); Assert.Equal("Active", timeline.Notice); timeline.TakeCue();
        clock.Ms = 1000; timeline.Apply(state with { SchoolNow = Window.Start.AddSeconds(5) }); Assert.Null(timeline.Notice);
        timeline.Suspend(); timeline.Apply(state); Assert.Null(timeline.Notice); Assert.Null(timeline.TakeCue());
    }
}

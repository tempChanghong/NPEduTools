using System.Text.Json.Nodes;
using NPEduTools.Integrations.Npep;

namespace NPEduTools.Tests;

public sealed partial class NoiseScheduleHostTests
{
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task Repeated_stop_cannot_rewrite_completed_state_while_statistics_callback_is_running(bool localCommand)
    {
        await using var f = new Fixture(); await f.Start();
        using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
        f.Noise.Completed += _ => { entered.Set(); release.Wait(TimeSpan.FromSeconds(5)); };
        var id = f.Noise.Snapshot().SessionId!.Value;
        try
        {
            f.Noise.StopScheduled(id); await Until(() => entered.IsSet);
            var completed = f.Noise.Snapshot(); Assert.Equal("Stopped", completed.State);
            if (localCommand) Assert.Equal("Accepted", f.Noise.Handle(f.Command("stop")).Outcome);
            else f.Noise.StopScheduled(id);
            Assert.Equal("Stopped", f.Noise.Snapshot().State);
            Assert.Equal(completed.Revision, f.Noise.Snapshot().Revision);
            Assert.DoesNotContain("Skipped", File.ReadAllText(Path.Combine(f.Path, "noise-schedule-state.json")));
        }
        finally { release.Set(); }
    }
    private static JsonObject StudyResume(Fixture f, JsonObject config) => new() {
        ["commandId"] = Guid.NewGuid().ToString("D"), ["version"] = config["version"]!.DeepClone(),
        ["window"] = f.Scheduler.Observe()["window"]!.DeepClone() };
    private static async Task StopStudy(Fixture f)
    {
        f.Noise.Handle(f.Command("stop")); await Until(() => f.Noise.Snapshot().State == "Stopped"); f.Tick();
        Assert.Equal("WINDOW_SKIPPED", f.Scheduler.ObserveStudyExecution()!.Text("reason"));
    }
    [Fact]
    public async Task Study_resume_consumption_survives_restarts_and_never_clears_a_later_manual_stop()
    {
        await using var f = new Fixture(); var config = StudyConfig(f); Select(f, config); await f.Start(); await StopStudy(f);
        await f.Scheduler.DisposeAsync(); f.Scheduler = f.NewScheduler(); Select(f, config); f.Tick(); f.Tick();
        Assert.Single(f.Captures); Assert.Equal("WINDOW_SKIPPED", f.Scheduler.ObserveStudyExecution()!.Text("reason"));
        config["version"] = new string('b', 64); config["effective"]!["settings"]!["schedule"]!["rules"]![0]!["start"] = "19:01";
        f.Time.Ms = 120000; Select(f, config); f.Tick(); f.Tick(); f.Tick(); Assert.Single(f.Captures);
        var command = StudyResume(f, config); f.Scheduler.ApplyStudyResume(config, command);
        await Until(() => f.Captures.Count == 2 && f.Noise.Snapshot().State == "Active");
        Assert.Equal("ACCEPTED", f.Scheduler.ObserveStudyResumeReceipt()!.Text("outcome"));
        await StopStudy(f); f.Scheduler.ApplyStudyResume(config, command); f.Tick();
        Assert.Equal(2, f.Captures.Count); Assert.Equal("WINDOW_SKIPPED", f.Scheduler.ObserveStudyExecution()!.Text("reason"));
        await f.Scheduler.DisposeAsync(); f.Scheduler = f.NewScheduler(); Select(f, config); f.Tick(); f.Tick();
        f.Scheduler.ApplyStudyResume(config, command); f.Tick(); Assert.Equal(2, f.Captures.Count);
        Assert.Equal("ACCEPTED", f.Scheduler.ObserveStudyResumeReceipt()!.Text("outcome"));
        Assert.Equal("WINDOW_SKIPPED", f.Scheduler.ObserveStudyExecution()!.Text("reason"));
    }
    [Theory]
    [InlineData("version")] [InlineData("window")] [InlineData("late")] [InlineData("end")]
    [InlineData("clock")] [InlineData("exam")]
    public async Task Study_resume_rejects_late_or_ineligible_commands_without_clearing_skip(string change)
    {
        await using var f = new Fixture(); var config = StudyConfig(f); Select(f, config); await f.Start(); await StopStudy(f);
        var command = StudyResume(f, config);
        if (change == "version") command["version"] = new string('c', 64);
        if (change == "window") command["window"]!["end"] = "2026-10-01T19:59:00.000";
        if (change == "end") { f.Time.Ms = 3600000; f.Tick(); f.Tick(); f.Tick(); }
        if (change == "clock") { f.Frozen = true; for (int i = 0; i < 8; i++) f.Tick(); }
        if (change == "exam") f.Exam = true;
        f.Scheduler.ApplyStudyResume(config, command, change == "late" ? TimeSpan.FromSeconds(30) : TimeSpan.Zero);
        Assert.Equal("REJECTED", f.Scheduler.ObserveStudyResumeReceipt()!.Text("outcome")); Assert.Single(f.Captures);
        Assert.Contains("Skipped", File.ReadAllText(Path.Combine(f.Path, "noise-schedule-state.json")));
    }
    [Fact]
    public async Task Study_resume_after_policy_split_preserves_the_later_window_stop()
    {
        await using var f = new Fixture(); var config = StudyConfig(f); Select(f, config); await f.Start(); await StopStudy(f);
        config["version"] = new string('b', 64);
        config["effective"]!["settings"]!["schedule"]!["rules"] = JsonNode.Parse("[{\"days\":[4],\"start\":\"19:00\",\"end\":\"19:15\"},{\"days\":[4],\"start\":\"19:30\",\"end\":\"20:00\"}]");
        Select(f, config); f.Tick(); f.Tick();
        f.Scheduler.ApplyStudyResume(config, StudyResume(f, config)); await Until(() => f.Captures.Count == 2 && f.Noise.Snapshot().State == "Active");
        f.Time.Ms = 900000; f.Tick(); f.Tick(); f.Tick(); await Until(() => f.Noise.Snapshot().State == "Stopped");
        f.Time.Ms = 1800000; f.Tick(); f.Tick(); f.Tick();
        Assert.Equal("WINDOW_SKIPPED", f.Scheduler.ObserveStudyExecution()!.Text("reason")); Assert.Equal(2, f.Captures.Count);
        await f.Scheduler.DisposeAsync(); f.Scheduler = f.NewScheduler(); Select(f, config); f.Tick(); f.Tick();
        Assert.Equal("WINDOW_SKIPPED", f.Scheduler.ObserveStudyExecution()!.Text("reason")); Assert.Equal(2, f.Captures.Count);
    }
    [Theory]
    [InlineData("version")] [InlineData("pause")] [InlineData("scope")]
    public async Task Study_resume_cannot_cross_configuration_or_binding_fences(string change)
    {
        await using var f = new Fixture(); var config = StudyConfig(f); Select(f, config); await f.Start(); await StopStudy(f);
        var command = StudyResume(f, config);
        if (change == "pause") f.Scheduler.Bind(f.Scope, false);
        if (change == "scope") f.Scheduler.Bind(null, false);
        if (change == "version") { var next = config.DeepClone().AsObject(); next["version"] = new string('d', 64); Select(f, next); }
        Assert.Equal("SESSION_SUPERSEDED", Assert.Throws<NpepException>(() => f.Scheduler.ApplyStudyResume(config, command)).Code);
        Assert.Single(f.Captures); Assert.Null(f.Scheduler.ObserveStudyResumeReceipt());
    }
    [Fact]
    public async Task Study_resume_reports_acceptance_before_fresh_frames_and_keeps_manual_capture_independent()
    {
        await using var f = new Fixture(); var config = StudyConfig(f); Select(f, config); await f.Start();
        f.Captures[0].Break(); await Until(() => f.Noise.Snapshot().State == "Faulted"); f.Tick();
        Assert.Equal("WINDOW_FAILED", f.Scheduler.ObserveStudyExecution()!.Text("reason"));
        var command = StudyResume(f, config); f.Scheduler.ApplyStudyResume(config, command);
        await Until(() => f.Captures.Count == 2 && f.Noise.Snapshot().State == "Active");
        Assert.Equal("ACCEPTED", f.Scheduler.ObserveStudyResumeReceipt()!.Text("outcome"));
        Assert.False(f.Scheduler.ObserveStudyExecution()!["signalFresh"]!.GetValue<bool>());
        f.Captures[^1].Sample(); Assert.True(f.Scheduler.ObserveStudyExecution()!["signalFresh"]!.GetValue<bool>());
        await StopStudy(f); f.Noise.Handle(f.Command("start", "synthetic")); await Until(() => f.Noise.Snapshot().State == "Active");
        var manual = f.Noise.Snapshot().SessionId;
        f.Scheduler.ApplyStudyResume(config, StudyResume(f, config)); Assert.Equal("REJECTED", f.Scheduler.ObserveStudyResumeReceipt()!.Text("outcome"));
        Assert.Equal(manual, f.Noise.Snapshot().SessionId); Assert.Equal("Manual", f.Scheduler.ObserveStudyExecution()!.Text("owner"));
    }
}

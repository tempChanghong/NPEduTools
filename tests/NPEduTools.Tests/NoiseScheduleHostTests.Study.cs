using System.Text.Json.Nodes;
using NPEduTools.Integrations.Npep;

namespace NPEduTools.Tests;

public sealed partial class NoiseScheduleHostTests
{
    private static JsonObject StudyConfig(Fixture f)
    {
        var config = JsonNode.Parse(File.ReadAllText(System.IO.Path.Combine(AppContext.BaseDirectory, "Fixtures/study-device-cases.json")))![0]!["value"]!.DeepClone().AsObject();
        f.Scope = config["identity"]!.ToJsonString(); f.Scheduler.Bind(f.Scope, true);
        var s = config["effective"]!["settings"]!["schedule"]!;
        s["effectiveFrom"] = "2026-10-01"; s["effectiveTo"] = null;
        s["rules"] = JsonNode.Parse("[{\"days\":[4],\"start\":\"19:00\",\"end\":\"20:00\"}]"); s["exceptions"] = new JsonArray();
        return config;
    }
    private static JsonObject Grant(JsonObject config, bool confirmed = true) => new() {
        ["confirmed"] = confirmed, ["version"] = config["version"]!.DeepClone(), ["leaseSeconds"] = 86400 };
    private static void Select(Fixture f, JsonObject config, bool grant = true)
    {
        f.Scheduler.SelectStudy(config, f.Scope);
        if (grant) f.Scheduler.ConfirmStudyExecution(config, Grant(config));
    }
    [Fact]
    public async Task Configuration_alone_cannot_start_and_actual_frames_are_distinct_from_start_acceptance()
    {
        await using var f = new Fixture(); var config = StudyConfig(f); Select(f, config, false);
        f.Tick(); f.Tick(); f.Tick(); Assert.Empty(f.Captures);
        Assert.Equal("POLICY_EXPIRED", f.Scheduler.Observe()["reason"]!.GetValue<string>());
        f.Scheduler.ConfirmStudyExecution(config, Grant(config)); await Until(() => f.Noise.Snapshot().State == "Active"); f.Tick();
        Assert.False(f.Scheduler.ObserveStudyExecution()!["signalFresh"]!.GetValue<bool>());
        f.Captures[0].Sample(); f.Tick(); Assert.True(f.Scheduler.ObserveStudyExecution()!["signalFresh"]!.GetValue<bool>());
        Assert.Equal("Schedule", f.Scheduler.ObserveStudyExecution()!["owner"]!.GetValue<string>());
        // Signal freshness follows the actual capture snapshot's existing bounded data-age logic.
        for (int i = 0; i < 8; i++) f.Tick();
        Assert.False(f.Scheduler.ObserveStudyExecution()!["signalFresh"]!.GetValue<bool>());
        f.Captures[0].Silence(); f.Tick(); Assert.Null(f.Noise.Snapshot().CurrentDbfs);
        Assert.True(f.Scheduler.ObserveStudyExecution()!["signalFresh"]!.GetValue<bool>());
    }
    [Theory]
    [InlineData("future")] [InlineData("expired")] [InlineData("holiday")] [InlineData("disabled")] [InlineData("noise-off")]
    public async Task Study_dates_calendar_and_explicit_closure_override_matching_legacy_rules(string change)
    {
        await using var f = new Fixture(); var config = StudyConfig(f); var s = config["effective"]!["settings"]!;
        if (change == "future") s["schedule"]!["effectiveFrom"] = "2026-10-12";
        if (change == "expired") { s["schedule"]!["effectiveFrom"] = "2026-09-01"; s["schedule"]!["effectiveTo"] = "2026-09-30"; }
        if (change == "holiday") s["schedule"]!["exceptions"] = JsonNode.Parse("[{\"date\":\"2026-10-01\",\"kind\":\"Skip\",\"weekday\":null}]");
        if (change == "disabled") { config["effective"]!["source"] = "Disabled"; config["effective"]!["settings"] = null; }
        if (change == "noise-off") s["noise"]!["enabled"] = false;
        Select(f, config); f.Scheduler.Confirm(f.Policy()); f.Tick(); f.Tick(); f.Tick();
        Assert.Empty(f.Captures); Assert.Equal(config["version"]!.GetValue<string>(), f.Scheduler.Observe()["version"]!.GetValue<string>());
    }
    [Theory]
    [InlineData("exam")] [InlineData("pause")] [InlineData("unpair")] [InlineData("expired")] [InlineData("clock")]
    public async Task Study_invalidations_stop_only_the_scheduled_session_and_never_fall_back(string change)
    {
        await using var f = new Fixture(); var config = StudyConfig(f); Select(f, config); await f.Start();
        if (change == "exam") f.Exam = true;
        if (change == "pause") f.Scheduler.Bind(f.Scope, false);
        if (change == "unpair") f.Scheduler.Bind(null, false);
        if (change == "expired") { f.Scheduler.ConfirmStudyExecution(config, Grant(config), TimeSpan.FromSeconds(86400 - 2)); for (int i = 0; i < 4; i++) f.Tick(); }
        if (change == "clock") { f.Frozen = true; for (int i = 0; i < 8; i++) f.Tick(); }
        f.Tick(); await Until(() => f.Noise.Snapshot().State == "Stopped");
        if (change != "unpair") f.Scheduler.Confirm(f.Policy());
        f.Tick(); Assert.Single(f.Captures); Assert.True(f.Captures[0].Disposed);
    }
    [Fact]
    public async Task Manual_capture_is_never_adopted_or_stopped_by_study_or_its_failed_grant()
    {
        await using var f = new Fixture(); var config = StudyConfig(f);
        Assert.Equal("Accepted", f.Noise.Handle(f.Command("start", "synthetic")).Outcome); await Until(() => f.Noise.Snapshot().State == "Active");
        var id = f.Noise.Snapshot().SessionId; Select(f, config); f.Tick();
        Assert.Equal("Manual", f.Scheduler.ObserveStudyExecution()!["owner"]!.GetValue<string>());
        Assert.Null(f.Scheduler.ProtectionWindow(id));
        f.Scheduler.InvalidateStudy(); f.Exam = true; f.Tick();
        Assert.Equal(id, f.Noise.Snapshot().SessionId); Assert.Equal("Active", f.Noise.Snapshot().State); Assert.Single(f.Captures);
    }
    [Fact]
    public async Task Policy_edit_fences_old_grant_and_unconfigured_transition_requires_fresh_legacy_confirmation()
    {
        await using var f = new Fixture(); var config = StudyConfig(f); Select(f, config); await f.Start();
        var next = config.DeepClone().AsObject(); next["version"] = new string('c', 64); next["effective"]!["settings"]!["schedule"]!["rules"]![0]!["start"] = "19:10";
        Select(f, next, false); await Until(() => f.Noise.Snapshot().State == "Stopped");
        Assert.Equal("SESSION_SUPERSEDED", Assert.Throws<NpepException>(() => f.Scheduler.ConfirmStudyExecution(config, Grant(config))).Code);
        f.Scheduler.Confirm(f.Policy()); f.Tick(); Assert.Single(f.Captures);
        next["effective"]!["source"] = "None"; next["effective"]!["settings"] = null;
        Select(f, next, false); f.Tick(); Assert.Single(f.Captures);
        f.Scheduler.Confirm(f.Policy()); f.Tick(); await Until(() => f.Captures.Count == 2 && f.Noise.Snapshot().State == "Active");
    }
    [Fact]
    public async Task Duplicate_and_delayed_grants_cannot_extend_the_execution_lease()
    {
        await using var f = new Fixture(); var config = StudyConfig(f); Select(f, config, false);
        Assert.Throws<NpepException>(() => f.Scheduler.ConfirmStudyExecution(config, Grant(config, false)));
        Assert.Throws<NpepException>(() => f.Scheduler.ConfirmStudyExecution(config, Grant(config), TimeSpan.FromSeconds(86400)));
        f.Scheduler.ConfirmStudyExecution(config, Grant(config), TimeSpan.FromSeconds(86400 - 5)); await f.Start();
        while (f.Time.Ms < 5000) f.Tick(); await Until(() => f.Noise.Snapshot().State == "Stopped");
        Assert.Single(f.Captures);
    }
    [Fact]
    public async Task Startup_cannot_briefly_start_legacy_capture_before_study_or_compatibility_is_known()
    {
        await using var f = new Fixture(); var config = StudyConfig(f);
        f.Scheduler.BindStudyCapture(f.Scope); f.Scheduler.Confirm(f.Policy()); f.Tick(); f.Tick(); f.Tick(); Assert.Empty(f.Captures);
        f.Scheduler.StudyUnavailable("STUDY_OFFLINE"); f.Tick(); Assert.Empty(f.Captures);
        f.Scheduler.StudyUnavailable("STUDY_UNSUPPORTED"); f.Tick(); await Until(() => f.Noise.Snapshot().State == "Active");
        Assert.Single(f.Captures);
        Select(f, config); f.Scheduler.StudyUnavailable("STUDY_UNSUPPORTED");
        await Until(() => f.Noise.Snapshot().State == "Stopped"); f.Scheduler.Confirm(f.Policy()); f.Tick(); Assert.Single(f.Captures);
    }
    [Fact]
    public async Task Transport_loss_retains_witnessed_current_study_until_explicit_revocation()
    {
        await using var f = new Fixture(); var config = StudyConfig(f); long sequence = 0;
        var connection = Guid.NewGuid(); var bridge = Guid.NewGuid();
        await using var study = new NPEduTools.Host.StudySettingsService(() => new(connection,bridge,++sequence,0,
            f.Base.AddMilliseconds(f.Time.Ms),0,"Advancing","synthetic"), () => f.Exam, f.Time, false, f.Scheduler);
        study.BindStudy(f.Scope); study.ApplyStudy(config); study.ConfirmStudyExecution(config, Grant(config)); await f.Start();
        f.Tick(); study.Tick(); f.Tick(); study.Tick();
        study.StudyFailed("STUDY_OFFLINE"); for (int i = 0; i < 40; i++) { f.Tick(); study.Tick(); }
        Assert.Equal("APPLIED", study.Snapshot().PolicyState); Assert.Equal("OFFLINE_CONTINUING", study.Snapshot().Reason);
        Assert.Null(study.ObserveStudy()); Assert.Equal("Active", f.Noise.Snapshot().State); Assert.Single(f.Captures);
        f.Captures[0].Sample(); f.Tick(); Assert.Equal("ACTIVE", study.Snapshot().CaptureState);
        Assert.True(study.Snapshot().Execution!.SignalFresh); Assert.Equal("Schedule", study.Snapshot().Execution!.Owner);
        study.StudyFailed("POLICY_CHANGED"); await Until(() => f.Noise.Snapshot().State == "Stopped");
        f.Scheduler.Confirm(f.Policy()); f.Tick(); Assert.Single(f.Captures);
    }

}

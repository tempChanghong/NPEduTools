using System.Text.Json.Nodes;
using NPEduTools.Contracts;
using NPEduTools.Host;

namespace NPEduTools.Tests;

public sealed partial class NoiseScheduleHostTests
{
    [Theory]
    [InlineData(false, false)] [InlineData(true, false)] [InlineData(false, true)] [InlineData(true, true)]
    public async Task A_135_minute_study_survives_90_dropouts_with_one_capture_display_and_complete_local_report(bool reconnect, bool examMode)
    {
        await using var f = new Fixture(); f.Base = new(2026,10,1,19,15,0,TimeSpan.Zero);
        var config = StudyConfig(f);
        config["effective"]!["settings"]!["schedule"]!["rules"] = JsonNode.Parse("[{\"days\":[4],\"start\":\"19:15\",\"end\":\"21:30\"},{\"days\":[4],\"start\":\"21:40\",\"end\":\"22:00\"}]");
        config["effective"]!["settings"]!["examSchedule"] = config["effective"]!["settings"]!["schedule"]!.DeepClone();
        f.ModeExam = examMode; f.Tick(); f.Tick();
        var grant = Grant(config); if (examMode) grant["mode"] = "Exam";
        long sequence = 0; var connection = Guid.NewGuid(); var bridge = Guid.NewGuid();
        await using var study = new StudySettingsService(() => new(connection, bridge, ++sequence, 0,
            f.Base.AddMilliseconds(f.Time.Ms), 0, "Advancing", "synthetic"), () => f.Exam, f.Time, false, f.Scheduler, () => examMode ? "Exam" : "Daily");
        study.BindStudy(f.Scope); study.ApplyStudy(config); study.ConfirmStudyExecution(config, grant); await f.Start();
        f.Tick(); study.Tick(); f.Tick(); study.Tick();
        var display = new StudyDisplayService(study.DisplaySnapshot, f.Time);
        await using var report = new StudyReportService(study.DisplaySnapshot, f.Noise.StudyReportSnapshot, f.Time, false,
            finishedStatistics: f.Noise.FinishedStudyReport);
        var initial = display.Snapshot(); var session = f.Noise.Snapshot().SessionId;
        Assert.True(initial.Display!.Available); report.Snapshot();
        var bytes = Enumerable.Repeat((byte)42,20000).ToArray();
        string hash = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes));
        study.ApplySeating(new() { ["version"] = initial.Version, ["classId"] = config["scope"]!["classId"]!.DeepClone(),
            ["revision"] = 1, ["asset"] = new JsonObject { ["hash"] = hash, ["byteCount"] = bytes.Length,
                ["width"] = 1200, ["height"] = 800, ["chunks"] = 2 } }, bytes);
        var homework = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(new StudyHomeworkSnapshot(initial.Version!, "2026-10-01",
            [new("work", 1, "数学", "", "完成练习", [new("class", "本班")])]), Protocol.Json);
        study.ApplyStudyHomework(JsonNode.Parse(homework)!.AsObject(), homework,
            Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(homework)));
        int drops = 0, frames = 0;
        while (f.Time.Ms < 8100000 - 500)
        {
            if (frames % 180 == 0) { study.StudyFailed(drops++ % 3 == 0 ? "TEMPORARILY_UNAVAILABLE" : "STUDY_OFFLINE"); }
            f.Captures[0].Sample(); frames++; f.Tick();
            if (reconnect && frames % 180 == 30) { study.ApplyStudy(config); study.ConfirmStudyExecution(config, grant); }
            var state = display.Snapshot();
            Assert.True(state.Display!.Available); Assert.Equal(initial.Display.InstanceId, state.Display.InstanceId);
            Assert.Equal(initial.Window, state.Window); Assert.Equal(session, state.Execution!.SessionId);
            Assert.Single(f.Captures); Assert.Equal("Active", f.Noise.Snapshot().State); report.Snapshot();
            if (!reconnect)
            {
                study.StudyHomeworkFailed("HOMEWORK_OFFLINE");
                Assert.Equal("READY", state.Seating!.State); Assert.Equal("READY", study.Snapshot().Homework!.State);
            }
        }
        Assert.Equal(90, drops);
        study.StudyFailed("STUDY_OFFLINE");
        f.Tick(); Assert.Equal("Ended", display.Snapshot().Phase); report.Snapshot();
        await Until(() => f.Noise.Snapshot().State == "Stopped");
        for (int i = 0; i < 10; i++) { f.Tick(); report.Snapshot(); }
        var result = report.Snapshot(); Assert.True(result.Available); Assert.Equal(8100, result.Data!.ExpectedSeconds);
        // The fixture emits 0.1 s per 0.5 s tick: real missing sampling stays missing, never filled as silence.
        Assert.Equal(frames * .1, result.Data.SampledSeconds, 5); Assert.True(result.Data.Coverage < .21);
        Assert.Equal(frames, result.Data.Frames); Assert.Single(f.Captures);
        for (int i = 0; i < 600; i++) { f.Tick(); report.Snapshot(); }
        Assert.False(report.Snapshot().Available); Assert.False(display.Snapshot().Display!.Available);
        // Even the original 24-hour execution exchange must not become a permit for the next offline window.
        for (int i = 0; i < 610; i++) { f.Tick(); study.Tick(); }
        Assert.Single(f.Captures); Assert.Equal("Stopped", f.Noise.Snapshot().State);
    }

    [Theory]
    [InlineData("AUTH_INVALID")] [InlineData("DEVICE_SUSPENDED")] [InlineData("SESSION_SUPERSEDED")]
    [InlineData("unbind")] [InlineData("disable")] [InlineData("skip")] [InlineData("mode")]
    public async Task Offline_continuation_does_not_override_explicit_stop_authority_or_mode(string action)
    {
        await using var f = new Fixture(); var config = StudyConfig(f); long sequence = 0;
        var connection = Guid.NewGuid(); var bridge = Guid.NewGuid();
        await using var study = new StudySettingsService(() => new(connection, bridge, ++sequence, 0,
            f.Base.AddMilliseconds(f.Time.Ms), 0, "Advancing", "synthetic"), () => f.Exam, f.Time, false, f.Scheduler,
            () => f.ModeExam ? "Exam" : "Daily");
        study.BindStudy(f.Scope); study.ApplyStudy(config); study.ConfirmStudyExecution(config, Grant(config)); await f.Start();
        f.Tick(); study.Tick(); f.Tick(); study.Tick();
        study.StudyFailed("STUDY_OFFLINE"); Assert.Equal("Active", study.Snapshot().Phase);
        if (action == "unbind") study.BindStudy(null);
        else if (action == "disable") { config["effective"]!["source"] = "Disabled"; config["effective"]!["settings"] = null; study.ApplyStudy(config); }
        else if (action == "skip") { Assert.Equal("Accepted", f.Noise.Handle(f.Command("stop")).Outcome); f.Tick(); }
        else if (action == "mode") f.ModeExam = true;
        else study.StudyFailed(action);
        Assert.NotEqual("Active", study.Snapshot().Phase);
    }

    [Fact]
    public async Task Download_without_execution_grant_never_becomes_an_offline_start_permit()
    {
        await using var f = new Fixture(); var config = StudyConfig(f); long sequence = 0;
        var connection = Guid.NewGuid(); var bridge = Guid.NewGuid();
        await using var study = new StudySettingsService(() => new(connection, bridge, ++sequence, 0,
            f.Base.AddMilliseconds(f.Time.Ms), 0, "Advancing", "synthetic"), () => false, f.Time, false, f.Scheduler);
        study.BindStudy(f.Scope); study.ApplyStudy(config); f.Tick(); f.Tick(); study.Snapshot();
        study.StudyFailed("STUDY_OFFLINE");
        Assert.Equal("UNAVAILABLE", study.Snapshot().PolicyState); Assert.Empty(f.Captures);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task A_grant_received_during_preparation_does_not_start_a_new_offline_window(bool explicitFailure)
    {
        await using var f = new Fixture(); f.Base = new(2026,10,1,18,59,30,TimeSpan.Zero);
        var config = StudyConfig(f); long sequence = 0; var connection = Guid.NewGuid(); var bridge = Guid.NewGuid();
        await using var study = new StudySettingsService(() => new(connection, bridge, ++sequence, 0,
            f.Base.AddMilliseconds(f.Time.Ms), 0, "Advancing", "synthetic"), () => false, f.Time, false, f.Scheduler);
        study.BindStudy(f.Scope); study.ApplyStudy(config); study.ConfirmStudyExecution(config, Grant(config));
        f.Tick(); study.Tick(); f.Tick(); study.Tick(); Assert.Equal("Preparing", study.Snapshot().Phase);
        if (explicitFailure) study.StudyFailed("STUDY_OFFLINE");
        for (int i = 0; i < 80; i++) { f.Tick(); study.Tick(); }
        Assert.Empty(f.Captures); Assert.Equal("UNAVAILABLE", study.Snapshot().PolicyState);
    }
}

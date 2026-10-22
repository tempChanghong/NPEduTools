using System.Text.Json.Nodes;
using NPEduTools.Host;
using NPEduTools.Integrations.Npep;

namespace NPEduTools.Tests;

public sealed partial class NoiseScheduleHostTests
{
    [Theory]
    [InlineData("revision")] [InlineData("presentation")] [InlineData("homework")]
    [InlineData("threshold")] [InlineData("photos")] [InlineData("other-mode")]
    public async Task Non_schedule_updates_keep_capture_and_original_grant_deadline(string change)
    {
        await using var f = new Fixture(); var config = StudyConfig(f); Select(f, config, false);
        f.Scheduler.ConfirmStudyExecution(config, Grant(config), TimeSpan.FromSeconds(86400 - 10));
        await f.Start(); var id = f.Noise.Snapshot().SessionId;
        var remaining = f.Scheduler.Observe()["leaseRemainingSeconds"]!.GetValue<int>();
        var next = config.DeepClone().AsObject(); next["version"] = new string('b', 64);
        var settings = next["effective"]!["settings"]!;
        if (change == "presentation") settings["presentation"]!["temporaryHideSeconds"] = 120;
        if (change == "homework") settings["homework"]!["rotationSeconds"] = 90;
        if (change == "threshold") settings["noise"]!["redLineDbfs"] = -30;
        if (change == "photos") settings["photos"]!["cooldownSeconds"] = 60;
        if (change == "other-mode") settings["examSchedule"] = settings["schedule"]!.DeepClone();
        Select(f, next, false);
        Assert.Equal("Active", f.Noise.Snapshot().State); Assert.Equal(id, f.Noise.Snapshot().SessionId);
        Assert.Single(f.Captures); Assert.Equal(remaining, f.Scheduler.Observe()["leaseRemainingSeconds"]!.GetValue<int>());
        Assert.Equal("SESSION_SUPERSEDED", Assert.Throws<NpepException>(() => f.Scheduler.ConfirmStudyExecution(config, Grant(config))).Code);
        while (f.Time.Ms < 10000) { f.Captures[0].Sample(); f.Tick(); }
        await Until(() => f.Noise.Snapshot().State == "Stopped"); Assert.Single(f.Captures);
    }

    [Theory]
    [InlineData("schedule")] [InlineData("disabled")] [InlineData("noise-off")] [InlineData("class")]
    public async Task Capture_affecting_changes_still_fence_the_old_grant(string change)
    {
        await using var f = new Fixture(); var config = StudyConfig(f); Select(f, config); await f.Start();
        var next = config.DeepClone().AsObject(); next["version"] = new string('b', 64);
        if (change == "schedule") next["effective"]!["settings"]!["schedule"]!["rules"]![0]!["start"] = "19:10";
        if (change == "noise-off") next["effective"]!["settings"]!["noise"]!["enabled"] = false;
        if (change == "disabled") { next["effective"]!["source"] = "Disabled"; next["effective"]!["settings"] = null; }
        if (change == "class") next["scope"]!["classId"] = "another-class";
        Select(f, next, false); await Until(() => f.Noise.Snapshot().State == "Stopped"); Assert.Single(f.Captures);
    }

    [Fact]
    public async Task Display_update_then_transport_loss_keeps_witnessed_study_and_capture()
    {
        await using var f = new Fixture(); var config = StudyConfig(f); long sequence = 0;
        var connection = Guid.NewGuid(); var bridge = Guid.NewGuid();
        await using var study = new StudySettingsService(() => new(connection, bridge, ++sequence, 0,
            f.Base.AddMilliseconds(f.Time.Ms), 0, "Advancing", "synthetic"), () => false, f.Time, false, f.Scheduler);
        study.BindStudy(f.Scope); study.ApplyStudy(config); study.ConfirmStudyExecution(config, Grant(config)); await f.Start();
        f.Tick(); study.Tick(); f.Tick(); study.Tick();
        var id = f.Noise.Snapshot().SessionId;
        var next = config.DeepClone().AsObject(); next["version"] = new string('b', 64);
        next["effective"]!["settings"]!["homework"]!["rotationSeconds"] = 90;
        study.ApplyStudy(next); study.StudyFailed("STUDY_OFFLINE");
        for (int i = 0; i < 40; i++) { f.Captures[0].Sample(); f.Tick(); study.Tick(); }
        Assert.Equal("OFFLINE_CONTINUING", study.Snapshot().Reason); Assert.Equal("Active", f.Noise.Snapshot().State);
        Assert.Equal(id, f.Noise.Snapshot().SessionId); Assert.Single(f.Captures);
        study.StudyFailed("AUTH_INVALID"); await Until(() => f.Noise.Snapshot().State == "Stopped");
    }
}

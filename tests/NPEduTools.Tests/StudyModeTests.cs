using System.Text.Json;
using System.Text.Json.Nodes;
using NPEduTools.Contracts;
using NPEduTools.Core;
using NPEduTools.Host;

namespace NPEduTools.Tests;

public sealed class StudyModeTests
{
    private sealed class Clock : TimeProvider
    {
        public long Ms;
        public override long TimestampFrequency => 1000;
        public override long GetTimestamp() => Ms;
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.Parse("2026-10-12T11:00:00Z").AddMilliseconds(Ms);
    }
    private static JsonObject Configuration() => JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures/study-device-cases.json")))![0]!["value"]!.DeepClone().AsObject();
    [Fact]
    public async Task Unset_mode_uses_daily_schedule_and_recovers_after_transport_loss_without_mode_change()
    {
        var time = new Clock();
        var connection = Guid.NewGuid();
        long sequence = 0;
        var clock = new StudyModeClock(() => "None", () => new(connection, connection, ++sequence, 0,
            new DateTimeOffset(2026, 10, 12, 19, 0, 0, TimeSpan.Zero).AddMilliseconds(time.Ms), 0, "Advancing", "synthetic school time"), time);
        await using var service = new StudySettingsService(clock.Snapshot, () => false, time, false, mode: () => clock.Mode);
        var configuration = Configuration();
        service.BindStudy(configuration["identity"]!.ToJsonString());
        for (int i = 0; i < 4; i++) { time.Ms += 500; service.ApplyStudy(configuration); }
        Assert.Equal("Daily", service.Snapshot().Mode);
        Assert.Equal("ClassIsland", service.Snapshot().ClockSource);
        Assert.Equal("Active", service.Snapshot().Phase);
        service.StudyFailed("STUDY_OFFLINE");
        Assert.Equal("UNAVAILABLE", service.Snapshot().PolicyState);
        time.Ms += 1000;
        service.ApplyStudy(configuration);
        Assert.Equal("APPLIED", service.Snapshot().PolicyState);
        Assert.Equal("Active", service.Snapshot().Phase);
        Assert.Equal("Daily", service.Snapshot().Mode);
    }
    [Fact]
    public void Empty_mode_still_validates_shared_thresholds_and_calendar()
    {
        var config = Configuration();
        var settings = config["effective"]!["settings"]!.Deserialize<StudySettings>(Protocol.Json)! with
        { Schedule = new("Asia/Shanghai", "", null, [], []) };
        Assert.Null(SchoolStudySchedule.Validate(settings));
        Assert.Equal("INVALID_NOISE", SchoolStudySchedule.Validate(settings with
        { Noise = settings.Noise with { RedLineDbfs = -50, MediumLineDbfs = -30 } }));
        Assert.Equal("INVALID_DATES", SchoolStudySchedule.Validate(settings with
        { Schedule = new("Asia/Shanghai", "2026-10-13", "2026-10-12", [], []) }));
    }
    [Fact]
    public async Task Exam_uses_Windows_Beijing_fields_without_ClassIsland_and_never_inherits_daily()
    {
        var t = new Clock(); string mode = "Exam";
        var clock = new StudyModeClock(() => mode, () => throw new Exception("Exam must not query ClassIsland"), t);
        Assert.Equal(19, clock.Snapshot().SchoolNow!.Value.Hour);
        var config = Configuration();
        await using var service = new StudySettingsService(clock.Snapshot, () => false, t, false, mode: () => mode);
        service.BindStudy(config["identity"]!.ToJsonString());
        for (int i = 0; i < 4; i++) { t.Ms += 500; service.ApplyStudy(config); }
        Assert.Equal("Outside", service.Snapshot().Phase);
        Assert.Equal("Windows", service.Snapshot().ClockSource);
        config["effective"]!["settings"]!["examSchedule"] = config["effective"]!["settings"]!["schedule"]!.DeepClone();
        service.ApplyStudy(config);
        Assert.Equal("Active", service.Snapshot().Phase);
        Assert.Equal("Exam", service.ObserveStudy()!["mode"]!.GetValue<string>());
        Assert.Null(SchoolStudySchedule.Windows(config["effective"]!["settings"]!.Deserialize<StudySettings>(Protocol.Json)!.ForMode("Daily") with
            { Schedule = new("Asia/Shanghai", "", null, [], []) }, service.Snapshot().SchoolNow!.Value).Current);
    }
    [Theory]
    [InlineData("Preparing", -119)] [InlineData("Active", 1)] [InlineData("Ended", 1)]
    public void Projection_mutes_stage_once_and_never_replays_after_projection(string phase, int seconds)
    {
        var window = new SchoolNoiseWindow(DateTimeOffset.Parse("2026-10-12T19:00:00Z"), DateTimeOffset.Parse("2026-10-12T21:30:00Z"));
        var state = new StudyHostState("v", "APPLIED", "NOT_INTEGRATED", "ACTIVE", phase, "WINDOW_ACTIVE", true,
            (phase == "Ended" ? window.End : window.Start).AddSeconds(seconds), window, null, null, Mode: "Exam", ClockSource: "Windows");
        bool projecting = true;
        var display = new StudyDisplayService(() => ("scope", true, state), projection: () => projecting);
        Assert.False(display.Snapshot().Display!.Available);
        projecting = false;
        var s = display.Snapshot(); Assert.True(s.Display!.Available);
        var request = new HostRequest(1, Guid.NewGuid(), "study.reminder", StudyReminder: new(s.Display.InstanceId, s.Version!, s.Window!, phase));
        Assert.Equal("Suppressed", display.Handle(request).Outcome);
    }
    [Theory]
    [InlineData("Connected", true, "ready", true)] [InlineData("Connected", true, "opening", true)]
    [InlineData("Connected", true, "preparing", false)] [InlineData("Disconnected", true, "ready", false)]
    [InlineData("Connected", false, "ready", false)] [InlineData("Connected", true, "closed", false)]
    public void Only_fresh_actual_player_claim_has_priority(string bridge, bool known, string phase, bool expected)
    {
        Assert.Equal(expected, StudyProjection.Active(new(null, 1, bridge, "", null, null, null, null,
            Player: new(known, [new("session", phase, "exam")]))));
    }
}

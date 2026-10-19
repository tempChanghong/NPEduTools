using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using NPEduTools.Contracts;
using NPEduTools.Core;
using NPEduTools.Host;
using NPEduTools.Integrations.Npep;

namespace NPEduTools.Tests;

public sealed class ManualStudyHomeworkTests
{
    private sealed class Clock : TimeProvider
    {
        public long Ms;
        public DateTimeOffset Base = new(2026, 10, 12, 2, 0, 0, TimeSpan.Zero);
        public override long TimestampFrequency => 1000;
        public override long GetTimestamp() => Ms;
        public override DateTimeOffset GetUtcNow() => Base.AddMilliseconds(Ms);
    }
    private sealed class Capture : INoiseCapture
    {
        public string DeviceName => "Synthetic; no microphone";
        public event Action<NoiseFrame>? Frame;
        public event Action<string>? Failed { add { } remove { } }
        public void Start() { }
        public void Dispose() { }
        public void Sample() => Frame?.Invoke(new(.1, .0001, .01, 0, false));
    }
    private sealed class TemporaryFolder : IDisposable
    {
        public readonly string Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "manual-homework-" + Guid.NewGuid());
        public void Dispose() { if (Directory.Exists(Path)) Directory.Delete(Path, true); }
    }
    [Theory]
    [InlineData(true, false)] [InlineData(false, false)] [InlineData(true, true)]
    public async Task Manual_homework_is_requested_outside_school_windows_and_reaches_the_manual_display(bool clockReady, bool preparing)
    {
        var time = new Clock(); var capture = new Capture();
        var config = JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures/study-device-cases.json")))![0]!["value"]!.DeepClone().AsObject();
        config["effective"]!["settings"]!["schedule"]!["rules"] = new JsonArray();
        long sequence = 0; var connection = Guid.NewGuid(); var bridge = Guid.NewGuid();
        await using var study = new StudySettingsService(() => clockReady
            ? new(connection, bridge, ++sequence, 0, new DateTimeOffset(time.GetUtcNow().ToLocalTime().DateTime, TimeSpan.Zero), 0, "Advancing", "synthetic")
            : SchoolClockFrame.Unavailable("synthetic"), () => false, time, false);
        study.BindStudy(config["identity"]!.ToJsonString());
        for (int i = 0; i < 3; i++) { time.Ms += 500; study.ApplyStudy(config); }
        Assert.NotEqual("Active", study.Snapshot().Phase);
        using var folder = new TemporaryFolder();
        await using var noise = new NoiseService(_ => capture, () => [new("mic", "Synthetic")], time);
        await using var manual = new ManualStudyService(noise, study.DisplaySnapshot, () => false, folder.Path, time, false);
        study.ManualHomeworkNow = manual.HomeworkNow;
        Assert.Null(study.StudyHomeworkRequest());
        var initial = manual.Status();
        var start = time.GetUtcNow().AddSeconds(preparing ? 60 : 0);
        var accepted = manual.Handle(new(1, Guid.NewGuid(), "manual.study.command",
            ManualStudy: new("start", initial.InstanceId, initial.Revision, DeviceId: "mic", StartUtc: start, EndUtc: start.AddMinutes(5))));
        Assert.Equal("Succeeded", accepted.Outcome);
        var request = study.StudyHomeworkRequest();
        Assert.NotNull(request);
        Assert.Equal("Manual", request.Text("purpose"));
        Assert.Equal(time.GetUtcNow().ToLocalTime().ToString("yyyy-MM-dd"), request.Text("date"));
        Assert.Equal(config.Text("version"), request.Text("version"));
        var payload = new StudyHomeworkSnapshot(request.Text("version"), request.Text("date"),
            [new("work", 1, "物理", "", "原作业", [new("one", "行政一班"), new("walk", "物理走班 A")])]);
        void Apply(StudyHomeworkSnapshot value)
        {
            var bytes = JsonSerializer.SerializeToUtf8Bytes(value, Protocol.Json);
            study.ApplyStudyHomework(JsonNode.Parse(bytes)!.AsObject(), bytes, Convert.ToHexStringLower(SHA256.HashData(bytes)));
        }
        Apply(payload);
        var metadata = manual.Display.Snapshot().Homework;
        Assert.NotNull(metadata); Assert.Equal("READY", metadata.State);
        Assert.NotEqual(manual.Display.Snapshot().Version, metadata.Version); // School configuration and manual session are distinct.
        var read = study.ReadHomework(new(1, Guid.NewGuid(), "study.homework", StudyHomework: new(metadata.SnapshotId!, 0)));
        Assert.Equal("Succeeded", read.Outcome);
        Assert.Equal("原作业", StudyHomeworkContract.Parse(Convert.FromBase64String(read.HomeworkChunk!.Data)).Items[0].Content);
        Apply(payload with { Items = [payload.Items[0] with { Content = "更新作业", Revision = 2 }] });
        Assert.NotEqual(metadata.SnapshotId, manual.Display.Snapshot().Homework!.SnapshotId);
        Assert.Equal("Rejected", study.ReadHomework(new(1, Guid.NewGuid(), "study.homework", StudyHomework: new(metadata.SnapshotId!, 0))).Outcome);
        config["version"] = new string('b', 64); study.ApplyStudy(config);
        Assert.NotEqual("READY", manual.Display.Snapshot().Homework?.State);
        Assert.Throws<NpepException>(() => Apply(payload));
        payload = payload with { Version = config.Text("version") }; Apply(payload);
        Assert.Equal("READY", manual.Display.Snapshot().Homework!.State);
        study.StudyFailed("STUDY_OFFLINE");
        Assert.Null(study.StudyHomeworkRequest());
        Assert.NotEqual("READY", manual.Display.Snapshot().Homework?.State);
        study.ApplyStudy(config); Apply(payload);
        config["version"] = new string('c', 64);
        config["effective"]!["settings"]!["homework"]!["enabled"] = false;
        study.ApplyStudy(config);
        Assert.Null(study.StudyHomeworkRequest());
        Assert.Equal("DISABLED", manual.Display.Snapshot().Homework!.State);
        Assert.Throws<NpepException>(() => Apply(payload with { Version = config.Text("version") }));
        study.BindStudy(null);
        Assert.Null(study.StudyHomeworkRequest());
        Assert.NotEqual("READY", manual.Display.Snapshot().Homework?.State);
        Assert.DoesNotContain(manual.Status().State, new[] { "Cancelled", "Interrupted", "Ended" });
    }
}

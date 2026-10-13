using System.Text.Json;
using NPEduTools.Contracts;
using NPEduTools.Core;
using NPEduTools.Host;

namespace NPEduTools.Tests;

public sealed class StudyDeviceTests
{
    private sealed class NeverRead : ILessonStatusReader
    { public Task<StatusResult> ReadAsync(StatusQuery query, CancellationToken token) => throw new InvalidOperationException("No sensor/clock startup"); }
    private sealed class Folder : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "NPEduTools.Test.study-devices." + Guid.NewGuid().ToString("N"));
        public Folder() => Directory.CreateDirectory(Path);
        public void Dispose() => Directory.Delete(Path, true);
    }
    private static HostRequest Select(StudyDeviceState s, string? id) => new(1, Guid.NewGuid(), "study.devices.select",
        StudyCamera: new(id is null ? "clear" : "select", s.InstanceId, s.Revision, id));
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void School_clear_loads_saved_camera_before_comparison_and_waits_for_capture(bool busy)
    {
        using var folder = new Folder();
        string path = System.IO.Path.Combine(folder.Path, "study-camera.json");
        File.WriteAllText(path, JsonSerializer.Serialize("saved-camera"));
        int enumerations = 0;
        var service = new StudyDeviceService(() => { enumerations++; throw new InvalidOperationException("Clear must not enumerate or capture"); }, folder.Path, () => busy)
        { EquipmentLocked = true };

        Assert.Equal(busy ? "EQUIPMENT_PENDING" : null, service.SelectSchoolCamera(null));
        Assert.Equal(busy ? "saved-camera" : null, service.Snapshot().CameraId);
        Assert.Equal(busy ? 0 : 1, service.Snapshot().Revision);
        Assert.True(service.Snapshot().Locked);
        Assert.Equal(busy ? "saved-camera" : null, JsonSerializer.Deserialize<string?>(File.ReadAllText(path)));
        Assert.Equal(0, enumerations);
        if (busy)
        {
            busy = false;
            Assert.Null(service.SelectSchoolCamera(null));
            Assert.Null(new StudyDeviceService(() => [], folder.Path).Snapshot().CameraId);
        }
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void School_clear_cannot_certify_an_unreadable_configuration_even_after_a_status_read(bool readFirst)
    {
        using var folder = new Folder();
        string path = System.IO.Path.Combine(folder.Path, "study-camera.json");
        const string original = "{broken camera configuration";
        File.WriteAllText(path, original);
        var service = new StudyDeviceService(() => throw new InvalidOperationException("No real device"), folder.Path);
        if (readFirst) Assert.Equal("CameraConfigurationUnavailable", service.Snapshot().Error);

        Assert.Equal("EQUIPMENT_UNAVAILABLE", service.SelectSchoolCamera(null));
        Assert.Equal("CameraConfigurationUnavailable", service.Snapshot().Error);
        Assert.Equal(0, service.Snapshot().Revision);
        Assert.Equal(original, File.ReadAllText(path));
    }
    [Fact]
    public void School_selection_already_saved_is_a_noop_without_enumeration_or_capture()
    {
        using var folder = new Folder();
        string path = System.IO.Path.Combine(folder.Path, "study-camera.json");
        File.WriteAllText(path, JsonSerializer.Serialize("saved-camera"));
        var service = new StudyDeviceService(() => throw new InvalidOperationException("No real device"), folder.Path, () => true);
        Assert.Null(service.SelectSchoolCamera("saved-camera"));
        Assert.Equal("saved-camera", service.Snapshot().CameraId);
        Assert.Equal(0, service.Snapshot().Revision);
    }
    [Fact]
    public void Read_does_not_enumerate_and_selection_survives_restart_without_fallback()
    {
        using var folder = new Folder(); int enumerations = 0;
        StudyCameraDevice[] cameras = [new("fixed-a", "同名摄像头"), new("fixed-b", "同名摄像头")];
        var service = new StudyDeviceService(() => { enumerations++; return cameras; }, folder.Path);
        Assert.Null(service.Snapshot().CameraId); Assert.Equal(0, enumerations);
        var chosen = service.Handle(Select(service.Snapshot(), "fixed-a"));
        Assert.Equal("Succeeded", chosen.Outcome); Assert.Equal(1, enumerations);
        var restarted = new StudyDeviceService(() => cameras, folder.Path);
        Assert.Equal("fixed-a", restarted.Snapshot().CameraId);
        cameras = [new("fixed-b", "同名摄像头")];
        var missing = restarted.Handle(new(1, Guid.NewGuid(), "study.devices.refresh"));
        Assert.Equal("fixed-a", missing.StudyDevices!.CameraId); Assert.False(missing.StudyDevices.CameraAvailable);
        Assert.Equal("CameraDeviceUnavailable", restarted.Handle(Select(missing.StudyDevices, "fixed-a")).ErrorCode);
        Assert.Equal("fixed-a", restarted.Snapshot().CameraId);
    }
    [Fact]
    public void Stale_revision_unplugged_device_and_active_capture_cannot_overwrite_saved_selection()
    {
        using var folder = new Folder(); bool busy = false;
        var service = new StudyDeviceService(() => [new("camera", "摄像头")], folder.Path, () => busy);
        var initial = service.Snapshot(); Assert.Equal("Succeeded", service.Handle(Select(initial, "camera")).Outcome);
        Assert.Equal("CameraStateChanged", service.Handle(Select(initial, null)).ErrorCode);
        busy = true; Assert.Equal("StudyDevicesBusy", service.Handle(Select(service.Snapshot(), null)).ErrorCode);
        Assert.Equal("camera", service.Snapshot().CameraId);
        busy = false; Assert.Equal("Succeeded", service.Handle(Select(service.Snapshot(), null)).Outcome);
        Assert.Null(new StudyDeviceService(() => [], folder.Path).Snapshot().CameraId);
    }
    [Fact]
    public void Broken_configuration_and_enumeration_failure_preserve_original_bytes()
    {
        using var folder = new Folder(); string path = System.IO.Path.Combine(folder.Path, "study-camera.json");
        File.WriteAllText(path, "broken original");
        var service = new StudyDeviceService(() => throw new IOException(), folder.Path);
        Assert.Equal("CameraConfigurationUnavailable", service.Snapshot().Error);
        service.Handle(new(1, Guid.NewGuid(), "study.devices.refresh"));
        Assert.Equal("CameraConfigurationUnavailable", service.Handle(Select(service.Snapshot(), null)).ErrorCode);
        Assert.Equal("broken original", File.ReadAllText(path));
    }
    [Fact]
    public void Enumeration_failure_at_save_invalidates_old_availability_without_changing_the_saved_id()
    {
        using var folder = new Folder(); bool fail = false;
        var service = new StudyDeviceService(() => fail ? throw new IOException() : [new("camera", "摄像头")], folder.Path);
        var saved = service.Handle(Select(service.Snapshot(), "camera")).StudyDevices!;
        Assert.True(saved.CameraAvailable);
        fail = true;
        var rejected = service.Handle(Select(saved, "camera"));
        Assert.Equal("CameraDevicesUnavailable", rejected.ErrorCode);
        Assert.Null(rejected.StudyDevices!.CameraAvailable);
        Assert.Empty(rejected.StudyDevices.Cameras);
        Assert.Equal("camera", rejected.StudyDevices.CameraId);
        Assert.Equal(saved.Revision, rejected.StudyDevices.Revision);
        Assert.Equal("camera", new StudyDeviceService(() => [], folder.Path).Snapshot().CameraId);
        fail = false;
        var refreshed = service.Handle(new(1, Guid.NewGuid(), "study.devices.refresh"));
        Assert.Null(refreshed.StudyDevices!.Error);
        Assert.True(refreshed.StudyDevices.CameraAvailable);
    }
    [Theory]
    [InlineData("oversize")]
    [InlineData("invalid-endpoint")]
    [InlineData("invalid-json")]
    public void Invalid_configuration_remains_rejected_across_reads_and_cannot_be_overwritten(string variant)
    {
        using var folder = new Folder(); string path = System.IO.Path.Combine(folder.Path, "study-camera.json");
        string original = variant == "oversize" ? new string('x', 16385) : variant == "invalid-endpoint" ? JsonSerializer.Serialize("bad\nendpoint") : "{broken";
        File.WriteAllText(path, original);
        var service = new StudyDeviceService(() => [new("synthetic", "Synthetic camera")], folder.Path);
        for (int i = 0; i < 3; i++)
        {
            var state = service.Snapshot(); Assert.Null(state.CameraId); Assert.Equal("CameraConfigurationUnavailable", state.Error);
            Assert.Equal("CameraConfigurationUnavailable", service.Handle(Select(state, "synthetic")).ErrorCode);
            Assert.Equal("CameraConfigurationUnavailable", service.Handle(Select(state, null)).ErrorCode);
            Assert.Equal(original, File.ReadAllText(path));
        }
    }
    [Fact]
    public void Catalog_is_bounded_for_the_existing_pipe_frame_even_with_escaped_endpoint_ids()
    {
        using var folder = new Folder();
        var service = new StudyDeviceService(() => Enumerable.Range(0, 128).Select(i => new StudyCameraDevice(new string('汉', 2000) + i, new string('汉', 200))).ToArray(), folder.Path);
        var response = service.Handle(new(1, Guid.NewGuid(), "study.devices.refresh"));
        Assert.NotEmpty(response.StudyDevices!.Cameras);
        Assert.True(JsonSerializer.SerializeToUtf8Bytes(response, Protocol.Json).Length < Protocol.MaxFrameBytes);
    }
    [Fact]
    public void Device_capabilities_cannot_start_capture_or_attach_selection_to_unrelated_requests()
    {
        var instance = Guid.NewGuid();
        Assert.Null(Protocol.Validate(new(1, Guid.NewGuid(), "study.devices.microphone", Noise: new("select", instance, 0, "mic"))));
        Assert.NotNull(Protocol.Validate(new(1, Guid.NewGuid(), "study.devices.microphone", Noise: new("start", instance, 0, "mic"))));
        Assert.NotNull(Protocol.Validate(new(1, Guid.NewGuid(), "study.devices.microphone", Noise: new("stop", instance, 0))));
        Assert.NotNull(Protocol.Validate(new(1, Guid.NewGuid(), "noise.status", StudyCamera: new("clear", instance, 0))));
        Assert.NotNull(Protocol.Validate(new(1, Guid.NewGuid(), "study.devices.select", StudyCamera: new("select", instance, 0, "default\n"))));
        Assert.DoesNotContain("studyCamera", JsonSerializer.Serialize(new HostRequest(1, Guid.NewGuid(), "noise.status"), Protocol.Json));
        Assert.DoesNotContain("studyDevices", JsonSerializer.Serialize(new HostResponse(1, Guid.NewGuid(), "Succeeded", null, "legacy"), Protocol.Json));
    }
    [Fact]
    public async Task Actual_pipe_saves_a_present_microphone_without_starting_and_rejects_missing_device()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var folder = new Folder(); int starts = 0;
        await using var noise = new NoiseService(_ => { starts++; throw new InvalidOperationException(); }, () => [new("fixed-mic", "监测麦克风")], directory: folder.Path);
        var cameras = new StudyDeviceService(() => [new("fixed-camera", "摄像头")], folder.Path);
        string name = "NPEduTools.Test.study-devices." + Guid.NewGuid().ToString("N");
        using var lifetime = new CancellationTokenSource();
        var serving = new PipeServer(name, new NeverRead(), _ => { }, noise: noise, studyDevices: cameras).RunAsync(lifetime.Token);
        try
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var s = noise.Snapshot();
            var mic = await HostClient.RequestAsync(name, new HostRequest(1, Guid.NewGuid(), "study.devices.microphone", Noise: new("select", s.InstanceId, s.Revision, "fixed-mic")), deadline.Token);
            Assert.Equal("Succeeded", mic.Outcome); Assert.Equal("fixed-mic", mic.Noise!.SelectedDeviceId); Assert.Equal("Idle", mic.Noise.State);
            var missing = await HostClient.RequestAsync(name, new HostRequest(1, Guid.NewGuid(), "study.devices.microphone", Noise: new("select", s.InstanceId, mic.Noise.Revision, "unplugged")), deadline.Token);
            Assert.Equal("NoiseDeviceUnavailable", missing.ErrorCode); Assert.Equal("fixed-mic", noise.Snapshot().SelectedDeviceId);
            var camera = await HostClient.RequestAsync(name, Select(cameras.Snapshot(), "fixed-camera"), deadline.Token);
            Assert.Equal("Succeeded", camera.Outcome); Assert.Equal(0, starts);
            Assert.Equal("fixed-camera", new StudyDeviceService(() => [], folder.Path).Snapshot().CameraId);
        }
        finally { lifetime.Cancel(); try { await serving; } catch (OperationCanceledException) { } }
    }
}

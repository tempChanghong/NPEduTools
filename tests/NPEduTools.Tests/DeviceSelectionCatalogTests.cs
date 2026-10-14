using System.Text.Json;
using NPEduTools.Contracts;
using NPEduTools.Core;
using NPEduTools.Host;

namespace NPEduTools.Tests;

public sealed class DeviceSelectionCatalogTests
{
    [Theory]
    [InlineData(false, false, false)] [InlineData(false, false, true)]
    [InlineData(true, false, false)] [InlineData(true, false, true)]
    [InlineData(false, true, false)] [InlineData(false, true, true)]
    [InlineData(true, true, false)] [InlineData(true, true, true)]
    public async Task Save_uses_fresh_complete_inventory_instead_of_display_prefix(bool microphone, bool school, bool countLimit)
    {
        if (!OperatingSystem.IsWindows()) return;
        string directory = Directory.CreateTempSubdirectory("NPEduTools-selection-catalog-").FullName;
        string file = Path.Combine(directory, microphone ? "noise-microphone.json" : "study-camera.json");
        File.WriteAllText(file, JsonSerializer.Serialize("old"));
        string pipe = "NPEduTools.Test.selection-catalog." + Guid.NewGuid().ToString("N");
        int captures = 0;
        NoiseDevice[] micCatalog = [new("old", "Old microphone"), new("target", "Target microphone")];
        StudyCameraDevice[] camCatalog = [new("old", "Old camera"), new("target", "Target camera")];
        await using var noise = new NoiseService(_ => { captures++; throw new InvalidOperationException("No real capture"); },
            () => micCatalog, directory: directory);
        var cameras = new StudyDeviceService(() => camCatalog, directory);
        using var lifetime = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var serving = new PipeServer(pipe, new NoSchoolReader(), _ => { }, noise: noise, studyDevices: cameras).RunAsync(lifetime.Token);
        try
        {
            // The user/administrator could see and select the target before the
            // fresh save-boundary enumeration changes its order and size.
            var initial = await HostClient.RequestAsync(pipe, microphone ? "noise.devices" : "study.devices.refresh", lifetime.Token);
            Assert.Equal("Succeeded", initial.Outcome);
            int count = countLimit ? 129 : 128;
            string caption = countLimit ? "Synthetic endpoint" : new string('设', 256);
            var ids = new[] { "old" }.Concat(Enumerable.Range(0, count - 2).Select(i => "filler-" + i)).Append("target").ToArray();
            micCatalog = ids.Select(id => new NoiseDevice(id, caption)).ToArray();
            camCatalog = ids.Select(id => new StudyCameraDevice(id, caption)).ToArray();
            noise.EquipmentLocked = cameras.EquipmentLocked = school;
            if (school)
            {
                Assert.Null(microphone ? noise.SelectSchoolMicrophone("target") : cameras.SelectSchoolCamera("target"));
                Assert.True(microphone ? noise.EquipmentLocked : cameras.Snapshot().Locked);
            }
            else
            {
                var command = microphone
                    ? new HostRequest(1, Guid.NewGuid(), "study.devices.microphone", Noise: new("select", initial.Noise!.InstanceId, initial.Noise.Revision, "target"))
                    : new HostRequest(1, Guid.NewGuid(), "study.devices.select", StudyCamera: new("select", initial.StudyDevices!.InstanceId, initial.StudyDevices.Revision, "target"));
                var saved = await HostClient.RequestAsync(pipe, command, lifetime.Token);
                Assert.Equal("Succeeded", saved.Outcome);
                if (!microphone)
                {
                    // The first save reply itself must contain the new current
                    // camera; waiting for another refresh would still show a false absence.
                    Assert.True(saved.StudyDevices!.CameraAvailable);
                    Assert.Contains(saved.StudyDevices.Cameras, d => d.Id == "target");
                    Assert.True(JsonSerializer.SerializeToUtf8Bytes(saved, Protocol.Json).Length < Protocol.MaxFrameBytes);
                }
            }
            Assert.Equal("target", JsonSerializer.Deserialize<string>(File.ReadAllText(file)));
            if (microphone)
            {
                Assert.Equal("target", noise.Snapshot().SelectedDeviceId);
                Assert.Equal(1, noise.Snapshot().Revision);
                var refreshed = await HostClient.RequestAsync(pipe, "noise.devices", lifetime.Token);
                Assert.Contains(refreshed.NoiseDevices!, d => d.Id == "target");
            }
            else
            {
                Assert.Equal("target", cameras.Snapshot().CameraId);
                Assert.Equal(1, cameras.Snapshot().Revision);
                Assert.True(cameras.Snapshot().CameraAvailable);
            }
            Assert.Equal(0, captures);
            Assert.Equal("Succeeded", (await HostClient.RequestAsync(pipe, "host.ping", lifetime.Token)).Outcome);
        }
        finally
        {
            await lifetime.CancelAsync(); try { await serving; } catch (OperationCanceledException) { }
            Assert.Equal(Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar), Path.GetDirectoryName(Path.GetFullPath(directory)));
            Assert.StartsWith("NPEduTools-selection-catalog-", Path.GetFileName(directory));
            Directory.Delete(directory, true);
        }
    }

    [Theory]
    [InlineData(false, false)] [InlineData(false, true)]
    [InlineData(true, false)] [InlineData(true, true)]
    public async Task Real_absence_or_enumeration_failure_preserves_old_school_choice(bool microphone, bool fail)
    {
        string directory = Directory.CreateTempSubdirectory("NPEduTools-selection-negative-").FullName;
        string file = Path.Combine(directory, microphone ? "noise-microphone.json" : "study-camera.json");
        string original = JsonSerializer.Serialize("old"); File.WriteAllText(file, original);
        try
        {
            await using var noise = new NoiseService(_ => throw new InvalidOperationException("No capture"),
                () => fail ? throw new IOException("Synthetic inventory failure") : [new("old", "Old")], directory: directory);
            var cameras = new StudyDeviceService(() => fail ? throw new IOException("Synthetic inventory failure") : [new("old", "Old")], directory);
            Assert.Equal("EQUIPMENT_UNAVAILABLE", microphone ? noise.SelectSchoolMicrophone("target") : cameras.SelectSchoolCamera("target"));
            Assert.Equal(original, File.ReadAllText(file));
            Assert.Equal(0, microphone ? noise.Snapshot().Revision : cameras.Snapshot().Revision);
        }
        finally
        {
            Assert.Equal(Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar), Path.GetDirectoryName(Path.GetFullPath(directory)));
            Assert.StartsWith("NPEduTools-selection-negative-", Path.GetFileName(directory));
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public void Camera_save_failure_keeps_old_available_snapshot_then_same_selection_can_retry()
    {
        string directory = Directory.CreateTempSubdirectory("NPEduTools-selection-write-").FullName;
        string file = Path.Combine(directory, "study-camera.json");
        string original = JsonSerializer.Serialize("old"); File.WriteAllText(file, original);
        try
        {
            var catalog = new[] { "old" }.Concat(Enumerable.Range(0, 126).Select(i => "filler-" + i)).Append("target")
                .Select(id => new StudyCameraDevice(id, new string('设', 256))).ToArray();
            var cameras = new StudyDeviceService(() => catalog, directory);
            var initial = cameras.Handle(new(1, Guid.NewGuid(), "study.devices.refresh")).StudyDevices!;
            var command = new HostRequest(1, Guid.NewGuid(), "study.devices.select",
                StudyCamera: new("select", initial.InstanceId, initial.Revision, "target"));
            using (var blocked = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                var failed = cameras.Handle(command);
                Assert.Equal("CameraConfigurationUnavailable", failed.ErrorCode);
                Assert.Equal("old", failed.StudyDevices!.CameraId);
                Assert.Equal(0, failed.StudyDevices.Revision);
                Assert.True(failed.StudyDevices.CameraAvailable);
                Assert.Contains(failed.StudyDevices.Cameras, d => d.Id == "old");
                Assert.Equal(original, File.ReadAllText(file));
            }
            var retry = cameras.Handle(command with { RequestId = Guid.NewGuid() });
            Assert.Equal("Succeeded", retry.Outcome);
            Assert.Equal("target", retry.StudyDevices!.CameraId);
            Assert.True(retry.StudyDevices.CameraAvailable);
            Assert.Equal(1, retry.StudyDevices.Revision);
        }
        finally
        {
            Assert.Equal(Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar), Path.GetDirectoryName(Path.GetFullPath(directory)));
            Assert.StartsWith("NPEduTools-selection-write-", Path.GetFileName(directory));
            Directory.Delete(directory, true);
        }
    }

    private sealed class NoSchoolReader : ILessonStatusReader
    { public Task<StatusResult> ReadAsync(StatusQuery query, CancellationToken token) => throw new InvalidOperationException("No real school or devices"); }
}

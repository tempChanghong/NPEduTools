using System.Text.Json;
using NPEduTools.Contracts;
using NPEduTools.Host;

namespace NPEduTools.Tests;

public sealed partial class StudyDeviceTests
{
    [Theory]
    [InlineData(null, false)] [InlineData(null, true)]
    [InlineData("saved-camera", false)] [InlineData("saved-camera", true)]
    public void Refresh_recovers_a_temporarily_locked_camera_preference_without_overwriting_it(string? camera, bool present)
    {
        if (!OperatingSystem.IsWindows()) return;
        using var folder = new Folder();
        string path = Path.Combine(folder.Path, "study-camera.json");
        string original = JsonSerializer.Serialize(camera); File.WriteAllText(path, original);
        int enumerations = 0;
        var service = new StudyDeviceService(() =>
        {
            enumerations++;
            return present ? [new("saved-camera", "Saved synthetic camera"), new("other-camera", "Other synthetic camera")]
                : [new("other-camera", "Other synthetic camera")];
        }, folder.Path, () => true); // Read recovery is allowed during noise capture; selection changes are not.
        StudyDeviceState blocked;
        using (var held = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            blocked = service.Snapshot();
            Assert.Equal("CameraConfigurationUnavailable", blocked.Error); Assert.Null(blocked.CameraId);
            var retry = service.Handle(new(1, Guid.NewGuid(), "study.devices.refresh"));
            Assert.Equal("Rejected", retry.Outcome); Assert.Equal("CameraConfigurationUnavailable", retry.StudyDevices!.Error);
            Assert.Equal(blocked.Revision, retry.StudyDevices.Revision);
        }
        var recovered = service.Handle(new(1, Guid.NewGuid(), "study.devices.refresh"));
        Assert.Equal("Succeeded", recovered.Outcome); Assert.Null(recovered.StudyDevices!.Error);
        Assert.Equal(camera, recovered.StudyDevices.CameraId);
        Assert.Equal(blocked.Revision + 1, recovered.StudyDevices.Revision);
        if (camera is not null) Assert.Equal(present, recovered.StudyDevices.CameraAvailable);
        Assert.Equal("CameraStateChanged", service.Handle(Select(blocked, "other-camera")).ErrorCode);
        Assert.Equal("StudyDevicesBusy", service.Handle(Select(recovered.StudyDevices, "other-camera")).ErrorCode);
        Assert.Equal(original, File.ReadAllText(path));
        Assert.Equal(2, enumerations); Assert.Equal(camera, new StudyDeviceService(() => [], folder.Path).Snapshot().CameraId);
    }
}

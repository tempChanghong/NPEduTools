using System.Text.Json;
using NPEduTools.Contracts;
using NPEduTools.Host;

namespace NPEduTools.Tests;

public sealed partial class StudyDeviceTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void Refresh_recovers_when_a_broken_camera_preference_is_moved_aside(bool busy, bool locked)
    {
        using var folder = new Folder();
        string path = Path.Combine(folder.Path, "study-camera.json");
        const string original = "{broken camera preference";
        File.WriteAllText(path, original);
        int enumerations = 0;
        var service = new StudyDeviceService(() => { enumerations++; return [new("camera", "Synthetic camera")]; }, folder.Path, () => busy)
            { EquipmentLocked = locked };
        var blocked = service.Snapshot();
        Assert.Equal("CameraConfigurationUnavailable", blocked.Error);
        var stillBroken = service.Handle(new(1, Guid.NewGuid(), "study.devices.refresh"));
        Assert.Equal("Rejected", stillBroken.Outcome);
        Assert.Equal(blocked.Revision, stillBroken.StudyDevices!.Revision);
        Assert.Equal(original, File.ReadAllText(path));

        File.Move(path, path + ".broken"); // Only this isolated fixture's file; preserve the original bytes.
        var reply = service.Handle(new(1, Guid.NewGuid(), "study.devices.refresh"));
        Assert.Equal("Succeeded", reply.Outcome);
        var recovered = reply.StudyDevices!;
        Assert.Null(recovered.Error);
        Assert.Null(recovered.CameraId);
        Assert.Null(recovered.CameraAvailable);
        Assert.Equal(blocked.Revision + 1, recovered.Revision);
        Assert.Equal(locked, recovered.Locked);
        Assert.Equal(2, enumerations);
        Assert.False(File.Exists(path)); // A refresh must not create a replacement preference or choose a device.
        if (locked)
        {
            Assert.Equal("EQUIPMENT_LOCKED", service.Handle(Select(recovered, "camera")).ErrorCode);
            Assert.False(File.Exists(path));
            service.EquipmentLocked = false; // Fixture simulates a subsequent authorized unlock.
        }
        Assert.Equal("CameraStateChanged", service.Handle(Select(blocked, "camera")).ErrorCode);
        if (busy)
        {
            Assert.Equal("StudyDevicesBusy", service.Handle(Select(recovered, "camera")).ErrorCode);
            Assert.False(File.Exists(path));
            busy = false;
        }
        Assert.Equal("Succeeded", service.Handle(Select(recovered, "camera")).Outcome);
        Assert.Equal("camera", new StudyDeviceService(() => [], folder.Path).Snapshot().CameraId);
        Assert.Equal(original, File.ReadAllText(path + ".broken"));
    }

    [Fact]
    public void Refresh_does_not_treat_a_camera_preference_directory_as_a_missing_file()
    {
        using var folder = new Folder();
        string path = Path.Combine(folder.Path, "study-camera.json");
        const string original = "{broken camera preference";
        File.WriteAllText(path, original);
        var service = new StudyDeviceService(() => [new("camera", "Synthetic camera")], folder.Path);
        var blocked = service.Snapshot();
        Assert.Equal("CameraConfigurationUnavailable", blocked.Error);
        File.Move(path, path + ".broken");
        Directory.CreateDirectory(path);
        var reply = service.Handle(new(1, Guid.NewGuid(), "study.devices.refresh"));
        Assert.Equal("Rejected", reply.Outcome);
        Assert.Equal("CameraConfigurationUnavailable", reply.StudyDevices!.Error);
        Assert.Equal(blocked.Revision, reply.StudyDevices.Revision);
        Assert.Equal("CameraConfigurationUnavailable", service.Handle(Select(reply.StudyDevices, "camera")).ErrorCode);
        Assert.Empty(Directory.EnumerateFileSystemEntries(path));
        Assert.Equal(original, File.ReadAllText(path + ".broken"));
    }

    [Theory]
    [InlineData("utf8")]
    [InlineData("utf8-bom")]
    [InlineData("utf16")]
    public void Refresh_preserves_existing_camera_preference_text_encoding_support(string encodingName)
    {
        using var folder = new Folder();
        string path = Path.Combine(folder.Path, "study-camera.json");
        File.WriteAllText(path, "{broken");
        var service = new StudyDeviceService(() => [new("camera", "Synthetic camera")], folder.Path);
        var blocked = service.Snapshot();
        Assert.Equal("CameraConfigurationUnavailable", blocked.Error);
        System.Text.Encoding encoding = encodingName switch
        {
            "utf8" => new System.Text.UTF8Encoding(false),
            "utf8-bom" => new System.Text.UTF8Encoding(true),
            _ => System.Text.Encoding.Unicode
        };
        File.WriteAllText(path, JsonSerializer.Serialize("camera"), encoding);
        var original = File.ReadAllBytes(path);
        var reply = service.Handle(new(1, Guid.NewGuid(), "study.devices.refresh"));
        Assert.Equal("Succeeded", reply.Outcome);
        Assert.Equal("camera", reply.StudyDevices!.CameraId);
        Assert.True(reply.StudyDevices.CameraAvailable);
        Assert.Equal(blocked.Revision + 1, reply.StudyDevices.Revision);
        Assert.Equal(original, File.ReadAllBytes(path));
    }

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

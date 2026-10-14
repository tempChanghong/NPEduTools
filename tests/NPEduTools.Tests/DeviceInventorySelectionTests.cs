using System.Text.Json;
using NPEduTools.Contracts;
using NPEduTools.Host;

namespace NPEduTools.Tests;

public sealed class DeviceInventorySelectionTests
{
    [Theory]
    [InlineData(false, false)] [InlineData(false, true)]
    [InlineData(true, false)] [InlineData(true, true)]
    public async Task Inventory_limits_keep_the_present_saved_device_without_switching_or_capture(bool microphone, bool countLimit)
    {
        string directory = Directory.CreateTempSubdirectory("NPEduTools-selected-inventory-").FullName;
        int count = countLimit ? 129 : 128;
        string name = countLimit ? "Synthetic endpoint" : new string('设', 256);
        string selected = "device-" + (count - 1);
        string file = Path.Combine(directory, microphone ? "noise-microphone.json" : "study-camera.json");
        string original = JsonSerializer.Serialize(selected); File.WriteAllText(file, original);
        int captures = 0;
        try
        {
            if (microphone)
            {
                NoiseDevice[] catalog = Enumerable.Range(0, count).Select(i => new NoiseDevice("device-" + i, name)).ToArray();
                await using var service = new NoiseService(_ => { captures++; throw new InvalidOperationException("No real capture"); },
                    () => catalog, directory: directory);
                var response = service.Handle(new(1, Guid.NewGuid(), "noise.devices"));
                Assert.Equal("Succeeded", response.Outcome); Assert.Equal(selected, response.Noise!.SelectedDeviceId);
                Assert.Contains(response.NoiseDevices!, d => d.Id == selected);
                Assert.InRange(response.NoiseDevices!.Count, 1, 128);
                Assert.True(JsonSerializer.SerializeToUtf8Bytes(response, Protocol.Json).Length < Protocol.MaxFrameBytes);
                Assert.Equal(0, response.Noise.Revision);
            }
            else
            {
                StudyCameraDevice[] catalog = Enumerable.Range(0, count).Select(i => new StudyCameraDevice("device-" + i, name)).ToArray();
                var service = new StudyDeviceService(() => catalog, directory);
                var response = service.Handle(new(1, Guid.NewGuid(), "study.devices.refresh"));
                Assert.Equal("Succeeded", response.Outcome); Assert.Equal(selected, response.StudyDevices!.CameraId);
                Assert.True(response.StudyDevices.CameraAvailable);
                Assert.Contains(response.StudyDevices.Cameras, d => d.Id == selected);
                Assert.InRange(response.StudyDevices.Cameras.Count, 1, 128);
                Assert.True(JsonSerializer.SerializeToUtf8Bytes(response, Protocol.Json).Length < Protocol.MaxFrameBytes);
                Assert.Equal(0, response.StudyDevices.Revision);
            }
            Assert.Equal(original, File.ReadAllText(file)); Assert.Equal(0, captures);
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public void Inventory_does_not_invent_an_absent_camera_or_reorder_an_ordinary_present_choice()
    {
        string directory = Directory.CreateTempSubdirectory("NPEduTools-selected-camera-").FullName;
        string file = Path.Combine(directory, "study-camera.json");
        StudyCameraDevice[] catalog = [new("first", "First"), new("second", "Second")];
        try
        {
            foreach (var selected in new[] { "missing", "second" })
            {
                string original = JsonSerializer.Serialize(selected); File.WriteAllText(file, original);
                var service = new StudyDeviceService(() => catalog, directory);
                var response = service.Handle(new(1, Guid.NewGuid(), "study.devices.refresh"));
                Assert.Equal(selected == "second", response.StudyDevices!.CameraAvailable);
                Assert.Equal(catalog, response.StudyDevices.Cameras);
                Assert.Equal(selected, response.StudyDevices.CameraId); Assert.Equal(original, File.ReadAllText(file));
            }
        }
        finally { Directory.Delete(directory, true); }
    }
}

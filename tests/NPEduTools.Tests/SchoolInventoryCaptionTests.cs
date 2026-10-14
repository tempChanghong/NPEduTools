using System.Text.Json.Nodes;
using NPEduTools.Host;
using Xunit.Abstractions;

namespace NPEduTools.Tests;

public sealed class SchoolInventoryCaptionTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData(true, "教室\u200f设备", "教室设备")]
    [InlineData(false, "教室\u200f设备", "教室设备")]
    [InlineData(true, "教室\U000e0001设备", "教室设备")]
    [InlineData(false, "教室\U000e0001设备", "教室设备")]
    [InlineData(true, "教室设备 🎤", "教室设备 🎤")]
    public async Task School_receipt_uses_a_compatible_caption_without_changing_endpoint_identity_or_local_names(bool microphone, string caption, string expected)
    {
        string directory = Directory.CreateTempSubdirectory("NPEduTools-school-caption-").FullName;
        int captures = 0;
        try
        {
            await using var noise = new NoiseService(_ => { captures++; throw new InvalidOperationException("No real microphone"); },
                () => [new("microphone-id", microphone ? caption : "正常麦克风")], directory: directory);
            var cameras = new StudyDeviceService(() => [new("camera-id", microphone ? "正常摄像头" : caption)], directory);
            var management = new SchoolManagementService(noise, cameras, directory);
            JsonObject observed = management.ObserveSchool();
            // The detailed test log is consumed by the isolated real KV-service fixture.
            output.WriteLine("INVENTORY_EVIDENCE " + observed.ToJsonString());
            Assert.Null(observed["error"]);
            Assert.Equal(expected, observed[microphone ? "microphones" : "cameras"]![0]!["name"]!.GetValue<string>());
            Assert.Equal("microphone-id", observed["microphones"]![0]!["id"]!.GetValue<string>());
            Assert.Equal("camera-id", observed["cameras"]![0]!["id"]!.GetValue<string>());
            Assert.Equal(microphone ? caption : "正常麦克风", noise.Handle(new(1, Guid.NewGuid(), "noise.devices")).NoiseDevices!.Single().Name);
            Assert.Equal(microphone ? "正常摄像头" : caption, cameras.Snapshot().Cameras.Single().Name);
            Assert.Null(observed["selectedMicrophoneId"]); Assert.Null(observed["selectedCameraId"]);
            Assert.Equal(0, captures);
            Assert.False(File.Exists(Path.Combine(directory, "noise-microphone.json")));
            Assert.False(File.Exists(Path.Combine(directory, "study-camera.json")));
        }
        finally
        {
            Assert.Equal(Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar), Path.GetDirectoryName(Path.GetFullPath(directory)));
            Assert.StartsWith("NPEduTools-school-caption-", Path.GetFileName(directory));
            Directory.Delete(directory, true);
        }
    }
}

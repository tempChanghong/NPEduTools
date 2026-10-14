using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using NPEduTools.Contracts;
using NPEduTools.Host;
using NPEduTools.Integrations.Npep;
using Xunit.Abstractions;

namespace NPEduTools.Tests;

public sealed class SchoolInventoryBudgetTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData(true, true)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(false, false)]
    [InlineData(true, true, false)]
    [InlineData(true, true, true, true)]
    public async Task Receipt_reserves_space_for_selected_ids_and_context_without_losing_current_devices(bool longMic, bool longCamera, bool present = true, bool longNames = false)
    {
        string directory = Directory.CreateTempSubdirectory("NPEduTools-school-budget-").FullName;
        int captures = 0;
        try
        {
            string micId = longMic ? new string('麦', 2048) : "mic-selected";
            string camId = longCamera ? new string('摄', 2048) : "camera-selected";
            string micName = longNames ? new string('声', 512) : "当前麦克风";
            string camName = longNames ? new string('像', 512) : "当前摄像头";
            var microphones = Enumerable.Range(0, 27).Select(i => new NoiseDevice(new string('声', 500) + i, "麦克风"))
                .Concat(present ? [new NoiseDevice(micId, micName)] : []).ToArray();
            var cameraList = Enumerable.Range(0, 27).Select(i => new StudyCameraDevice(new string('像', 500) + i, "摄像头"))
                .Concat(present ? [new StudyCameraDevice(camId, camName)] : []).ToArray();
            string micSaved = JsonSerializer.Serialize(micId), camSaved = JsonSerializer.Serialize(camId);
            File.WriteAllText(Path.Combine(directory, "noise-microphone.json"), micSaved);
            File.WriteAllText(Path.Combine(directory, "study-camera.json"), camSaved);
            await using var noise = new NoiseService(_ => { captures++; throw new InvalidOperationException("No real capture"); },
                () => microphones, directory: directory);
            var cameras = new StudyDeviceService(() => cameraList, directory);
            var observed = new SchoolManagementService(noise, cameras, directory).ObserveSchool();
            var localMicrophones = noise.Handle(new(1, Guid.NewGuid(), "noise.devices")).NoiseDevices!;
            var localCameras = cameras.Snapshot().Cameras;
            observed["requestId"] = Guid.NewGuid().ToString("D");
            observed["context"] = new JsonObject {
                ["identity"] = new JsonObject { ["serverInstanceId"] = Guid.NewGuid().ToString("D"),
                    ["deploymentEpoch"] = Guid.NewGuid().ToString("D"), ["deviceId"] = Guid.NewGuid().ToString("D"),
                    ["bindingRevision"] = NpepProtocol.MaxInteger, ["credentialGeneration"] = 1 },
                ["sessionId"] = Guid.NewGuid().ToString("D"), ["runId"] = Guid.NewGuid().ToString("D"), ["statusEpoch"] = NpepProtocol.MaxInteger };
            observed["sequence"] = NpepProtocol.MaxInteger;
            output.WriteLine($"SCHOOL_BUDGET mic={longMic} camera={longCamera} present={present} bytes={Encoding.UTF8.GetByteCount(observed.ToJsonString())}");
            // Compress the fixture evidence so detailed logs do not dump thousands
            // of repeated synthetic endpoint characters. The KV companion restores it.
            using var evidence = new MemoryStream();
            using (var zip = new GZipStream(evidence, CompressionLevel.Fastest, leaveOpen: true))
                zip.Write(Encoding.UTF8.GetBytes(observed.ToJsonString()));
            output.WriteLine("SCHOOL_BUDGET_RECEIPT_GZIP " + Convert.ToBase64String(evidence.ToArray()));
            // Match the exact API preflight serialization and byte limit. No socket,
            // real school credentials or device access is involved.
            Assert.InRange(Encoding.UTF8.GetByteCount(observed.ToJsonString()), 1, 65536);
            Assert.Equal(present, observed["microphones"]!.AsArray().Any(d => d!["id"]!.GetValue<string>() == micId));
            Assert.Equal(present, observed["cameras"]!.AsArray().Any(d => d!["id"]!.GetValue<string>() == camId));
            Assert.Equal(present, cameras.Snapshot().CameraAvailable);
            if (!longMic || !longCamera)
            {
                Assert.Equal(localMicrophones.Select(d => d.Id), observed["microphones"]!.AsArray().Select(d => d!["id"]!.GetValue<string>()));
                Assert.Equal(localCameras.Select(d => d.Id), observed["cameras"]!.AsArray().Select(d => d!["id"]!.GetValue<string>()));
            }
            else
            {
                Assert.True(observed["microphones"]!.AsArray().Count < localMicrophones.Count);
                Assert.True(observed["cameras"]!.AsArray().Count < localCameras.Count);
            }
            Assert.Equal(micId, observed["selectedMicrophoneId"]!.GetValue<string>());
            Assert.Equal(camId, observed["selectedCameraId"]!.GetValue<string>());
            Assert.Equal(0, captures);
            Assert.Equal(0, noise.Snapshot().Revision);
            Assert.Equal(0, cameras.Snapshot().Revision);
            Assert.Equal(micSaved, File.ReadAllText(Path.Combine(directory, "noise-microphone.json")));
            Assert.Equal(camSaved, File.ReadAllText(Path.Combine(directory, "study-camera.json")));
        }
        finally
        {
            Assert.Equal(Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar), Path.GetDirectoryName(Path.GetFullPath(directory)));
            Assert.StartsWith("NPEduTools-school-budget-", Path.GetFileName(directory));
            Directory.Delete(directory, true);
        }
    }

}

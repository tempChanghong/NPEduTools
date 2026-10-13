using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using NPEduTools.Host;

namespace NPEduTools.Tests;

public sealed class SchoolEquipmentInventoryTests
{
    private sealed class Fixture : IAsyncDisposable
    {
        public string DirectoryPath { get; } = Path.Combine(Path.GetTempPath(), "school-equipment-inventory-" + Guid.NewGuid());
        public JsonObject Identity { get; } = new() { ["deviceId"] = Guid.NewGuid().ToString() };
        public NoiseService Noise { get; }
        public StudyDeviceService Cameras { get; }
        public SchoolManagementService Management { get; }
        public Exception? MicrophoneFailure { get; set; }
        public bool CameraFailure { get; set; }
        public int CaptureStarts { get; private set; }
        private readonly JsonObject _credential;
        public Fixture()
        {
            byte[] salt = RandomNumberGenerator.GetBytes(16);
            _credential = new() { ["revision"] = 1L, ["verifier"] = new JsonObject { ["iterations"] = 210000,
                ["salt"] = Convert.ToBase64String(salt), ["hash"] = Convert.ToBase64String(
                    Rfc2898DeriveBytes.Pbkdf2("school-password", salt, 210000, HashAlgorithmName.SHA256, 32)) } };
            Noise = new(_ => { CaptureStarts++; throw new InvalidOperationException("No real capture"); },
                () => MicrophoneFailure is { } failure ? throw failure : [new("mic-one", "one"), new("mic-two", "two")],
                directory: DirectoryPath, management: new NoiseManagementStore(DirectoryPath));
            Cameras = new(() => CameraFailure ? throw new COMException("Synthetic camera enumeration failure")
                : [new("cam-one", "one"), new("cam-two", "two")], DirectoryPath);
            Management = new(Noise, Cameras, DirectoryPath);
            Management.BindSchool(Identity.ToJsonString());
            Management.ApplySchool(Policy(1));
            Assert.Null(Management.ObserveSchool()["error"]);
        }
        public JsonObject Policy(long revision) => new()
        {
            ["identity"] = Identity.DeepClone(), ["credential"] = _credential.DeepClone(),
            ["equipment"] = new JsonObject { ["revision"] = revision, ["locked"] = true,
                ["microphoneId"] = revision == 1 ? "mic-one" : "mic-two", ["cameraId"] = revision == 1 ? "cam-one" : "cam-two" }
        };
        public async ValueTask DisposeAsync()
        {
            await Noise.DisposeAsync();
            if (Directory.Exists(DirectoryPath)) Directory.Delete(DirectoryPath, true);
        }
    }

    [Theory]
    [InlineData("com")]
    [InlineData("io")]
    [InlineData("access")]
    [InlineData("invalid-operation")]
    public async Task RemoteMicrophoneEnumerationFailuresProduceReceiptsAndRecoverWithoutFallback(string kind)
    {
        await using var f = new Fixture();
        f.MicrophoneFailure = kind switch
        {
            "com" => new COMException("Synthetic audio endpoint failure"),
            "io" => new IOException("Synthetic enumeration failure"),
            "access" => new UnauthorizedAccessException("Synthetic enumeration refusal"),
            _ => new InvalidOperationException("Synthetic audio service failure")
        };
        var policy = f.Policy(2);
        string micPath = Path.Combine(f.DirectoryPath, "noise-microphone.json");
        string cameraPath = Path.Combine(f.DirectoryPath, "study-camera.json");
        string micBefore = File.ReadAllText(micPath), cameraBefore = File.ReadAllText(cameraPath);
        Assert.Null(Record.Exception(() => f.Management.ApplySchool(policy)));
        var rejected = f.Management.ObserveSchool();
        Assert.Equal("EQUIPMENT_UNAVAILABLE", rejected["error"]?.GetValue<string>());
        Assert.Equal(1, rejected["appliedRevision"]!.GetValue<long>());
        Assert.Equal("mic-one", rejected["selectedMicrophoneId"]?.GetValue<string>());
        Assert.Equal("cam-one", rejected["selectedCameraId"]?.GetValue<string>());
        Assert.Equal(micBefore, File.ReadAllText(micPath));
        Assert.Equal(cameraBefore, File.ReadAllText(cameraPath));
        f.MicrophoneFailure = null;
        f.Management.ApplySchool(policy);
        var recovered = f.Management.ObserveSchool();
        Assert.Null(recovered["error"]);
        Assert.Equal(2, recovered["appliedRevision"]!.GetValue<long>());
        Assert.Equal("mic-two", recovered["selectedMicrophoneId"]?.GetValue<string>());
        Assert.Equal("cam-two", recovered["selectedCameraId"]?.GetValue<string>());
        Assert.Equal(0, f.CaptureStarts);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task ReadOnlyInventoryCannotPresentEnumerationFailureAsAHealthyEmptyList(bool microphone, bool camera)
    {
        await using var f = new Fixture();
        string micPath = Path.Combine(f.DirectoryPath, "noise-microphone.json");
        string cameraPath = Path.Combine(f.DirectoryPath, "study-camera.json");
        string micBefore = File.ReadAllText(micPath), cameraBefore = File.ReadAllText(cameraPath);
        f.MicrophoneFailure = microphone ? new COMException("Synthetic audio endpoint failure") : null;
        f.CameraFailure = camera;
        var observed = f.Management.ObserveSchool();
        Assert.Equal("EQUIPMENT_UNAVAILABLE", observed["error"]?.GetValue<string>());
        Assert.Equal(microphone ? 0 : 2, observed["microphones"]!.AsArray().Count);
        Assert.Equal(camera ? 0 : 2, observed["cameras"]!.AsArray().Count);
        Assert.Equal(1, observed["appliedRevision"]!.GetValue<long>());
        Assert.Equal("mic-one", observed["selectedMicrophoneId"]?.GetValue<string>());
        Assert.Equal("cam-one", observed["selectedCameraId"]?.GetValue<string>());
        Assert.Equal(micBefore, File.ReadAllText(micPath));
        Assert.Equal(cameraBefore, File.ReadAllText(cameraPath));
        f.MicrophoneFailure = null; f.CameraFailure = false;
        var recovered = f.Management.ObserveSchool();
        Assert.Null(recovered["error"]);
        Assert.Equal(2, recovered["microphones"]!.AsArray().Count);
        Assert.Equal(2, recovered["cameras"]!.AsArray().Count);
        Assert.Equal(0, f.CaptureStarts);
    }

    [Fact]
    public async Task InventoryReportsUnreadableCameraPreferencesWhilePreservingTheirOriginalBytes()
    {
        await using var f = new Fixture();
        string cameraPath = Path.Combine(f.DirectoryPath, "study-camera.json");
        const string broken = "{broken camera preferences";
        File.WriteAllText(cameraPath, broken);
        var cameras = new StudyDeviceService(() => [new("cam-one", "one")], f.DirectoryPath);
        var management = new SchoolManagementService(f.Noise, cameras, f.DirectoryPath);
        Assert.Equal("EQUIPMENT_STORE_UNAVAILABLE", management.ObserveSchool()["error"]?.GetValue<string>());
        Assert.Equal(broken, File.ReadAllText(cameraPath));
        Assert.Null(management.ObserveSchool()["selectedCameraId"]);
        Assert.Equal(1, management.ObserveSchool()["appliedRevision"]!.GetValue<long>());
        Assert.Equal(0, f.CaptureStarts);
    }

    [Fact]
    public async Task MicrophonePersistenceFailureRemainsDistinctFromEnumerationFailure()
    {
        await using var f = new Fixture();
        string block = Path.Combine(f.DirectoryPath, "noise-microphone.json.tmp");
        Directory.CreateDirectory(block);
        f.Management.ApplySchool(f.Policy(2));
        Assert.Equal("EQUIPMENT_STORE_UNAVAILABLE", f.Management.ObserveSchool()["error"]?.GetValue<string>());
        Assert.Equal("mic-one", f.Noise.Snapshot().SelectedDeviceId);
        Assert.Equal("cam-one", f.Cameras.Snapshot().CameraId);
        Directory.Delete(block);
        f.Management.ApplySchool(f.Policy(2));
        Assert.Null(f.Management.ObserveSchool()["error"]);
        Assert.Equal(2, f.Management.ObserveSchool()["appliedRevision"]!.GetValue<long>());
        Assert.Equal(0, f.CaptureStarts);
    }

    [Fact]
    public async Task InventoryFatalAllocationFailureIsNotHiddenAsARecoverableDeviceError()
    {
        await using var f = new Fixture();
        f.MicrophoneFailure = new OutOfMemoryException("Synthetic exception only, no large allocation");
        Assert.Throws<OutOfMemoryException>(() => f.Management.ApplySchool(f.Policy(2)));
        Assert.Equal("mic-one", f.Noise.Snapshot().SelectedDeviceId);
        Assert.Equal(0, f.CaptureStarts);
    }
}

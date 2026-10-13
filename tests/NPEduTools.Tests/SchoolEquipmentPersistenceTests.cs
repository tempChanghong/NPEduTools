using System.Security.Cryptography;
using System.Text.Json.Nodes;
using NPEduTools.Contracts;
using NPEduTools.Host;

namespace NPEduTools.Tests;

public sealed class SchoolEquipmentPersistenceTests
{
    private sealed class Fixture : IAsyncDisposable
    {
        public string DirectoryPath { get; } = Path.Combine(Path.GetTempPath(), "school-equipment-persistence-" + Guid.NewGuid());
        public string StorePath => Path.Combine(DirectoryPath, "school-equipment.json");
        public string BlockPath => StorePath + ".tmp";
        public JsonObject Identity { get; } = new() { ["deviceId"] = Guid.NewGuid().ToString() };
        public NoiseService Noise { get; }
        public StudyDeviceService Cameras { get; }
        public SchoolManagementService Management { get; }
        public Action? CameraEnumerated { get; set; }
        private readonly JsonObject _credential;
        public Fixture()
        {
            byte[] salt = RandomNumberGenerator.GetBytes(16);
            byte[] hash = Rfc2898DeriveBytes.Pbkdf2("school-password", salt, 210000, HashAlgorithmName.SHA256, 32);
            _credential = new() { ["revision"] = 1L, ["verifier"] = new JsonObject { ["iterations"] = 210000,
                ["salt"] = Convert.ToBase64String(salt), ["hash"] = Convert.ToBase64String(hash) } };
            Noise = new(_ => throw new InvalidOperationException("No real capture"),
                () => [new("mic-one", "one"), new("mic-two", "two")], directory: DirectoryPath, management: new NoiseManagementStore(DirectoryPath));
            Cameras = new(() => { CameraEnumerated?.Invoke(); return [new("cam-one", "one"), new("cam-two", "two")]; }, DirectoryPath);
            Management = new(Noise, Cameras, DirectoryPath);
            Management.BindSchool(Identity.ToJsonString());
            Management.ApplySchool(Policy(1));
            Assert.Equal(1, Management.ObserveSchool()["appliedRevision"]!.GetValue<long>());
        }
        public JsonObject Policy(long revision) => new()
        {
            ["identity"] = Identity.DeepClone(), ["credential"] = _credential.DeepClone(),
            ["equipment"] = new JsonObject { ["revision"] = revision, ["locked"] = true,
                ["microphoneId"] = revision == 1 ? "mic-one" : "mic-two", ["cameraId"] = revision == 1 ? "cam-one" : "cam-two" }
        };
        public JsonObject Saved() => JsonNode.Parse(File.ReadAllText(StorePath))!.AsObject();
        public void BlockWrites() => Directory.CreateDirectory(BlockPath);
        public void RestoreWrites() => Directory.Delete(BlockPath);
        public async ValueTask DisposeAsync()
        {
            await Noise.DisposeAsync();
            if (Directory.Exists(DirectoryPath)) Directory.Delete(DirectoryPath, true);
        }
    }

    [Fact]
    public async Task UnsavedPolicyRetriesStayUnappliedAndPreserveSelectionsUntilPersistenceRecovers()
    {
        await using var f = new Fixture();
        var policy = f.Policy(2);
        string original = File.ReadAllText(f.StorePath);
        f.BlockWrites();
        for (int attempt = 0; attempt < 3; attempt++)
        {
            f.Management.ApplySchool(policy);
            var observed = f.Management.ObserveSchool();
            Assert.Equal("EQUIPMENT_STORE_UNAVAILABLE", observed["error"]?.GetValue<string>());
            Assert.Equal(1, observed["appliedRevision"]!.GetValue<long>());
            Assert.Equal("mic-one", observed["selectedMicrophoneId"]?.GetValue<string>());
            Assert.Equal("cam-one", observed["selectedCameraId"]?.GetValue<string>());
            Assert.Equal(original, File.ReadAllText(f.StorePath));
            Assert.True(f.Cameras.Snapshot().Locked);
        }
        f.RestoreWrites();
        f.Management.ApplySchool(policy);
        var recovered = f.Management.ObserveSchool();
        Assert.Null(recovered["error"]);
        Assert.Equal(2, recovered["appliedRevision"]!.GetValue<long>());
        Assert.Equal("mic-two", recovered["selectedMicrophoneId"]?.GetValue<string>());
        Assert.Equal("cam-two", recovered["selectedCameraId"]?.GetValue<string>());
        Assert.Equal(2, f.Saved()["Applied"]!.GetValue<long>());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailedAppliedReceiptRemainsUnconfirmedAndRetriesAfterServiceRecreation(bool recreate)
    {
        await using var f = new Fixture();
        var policy = f.Policy(2);
        f.CameraEnumerated = () => { f.CameraEnumerated = null; f.BlockWrites(); };
        f.Management.ApplySchool(policy);
        Assert.Equal("mic-two", f.Noise.Snapshot().SelectedDeviceId);
        Assert.Equal("cam-two", f.Cameras.Snapshot().CameraId);
        Assert.Equal(1, f.Saved()["Applied"]!.GetValue<long>());
        var management = recreate ? new SchoolManagementService(f.Noise, f.Cameras, f.DirectoryPath) : f.Management;
        for (int attempt = 0; attempt < 3; attempt++)
        {
            management.ApplySchool(policy);
            var observed = management.ObserveSchool();
            Assert.Equal("EQUIPMENT_STORE_UNAVAILABLE", observed["error"]?.GetValue<string>());
            Assert.Equal(1, observed["appliedRevision"]!.GetValue<long>());
            Assert.Equal(1, f.Saved()["Applied"]!.GetValue<long>());
        }
        f.RestoreWrites();
        management.ApplySchool(policy);
        Assert.Null(management.ObserveSchool()["error"]);
        Assert.Equal(2, management.ObserveSchool()["appliedRevision"]!.GetValue<long>());
        Assert.Equal(2, f.Saved()["Applied"]!.GetValue<long>());
        var reloaded = new SchoolManagementService(f.Noise, f.Cameras, f.DirectoryPath);
        Assert.Equal(2, reloaded.ObserveSchool()["appliedRevision"]!.GetValue<long>());
    }

    [Fact]
    public async Task SameSchoolBindingRetriesItsFailedSaveWithoutRevertingToThePreviousSchool()
    {
        await using var f = new Fixture();
        string nextScope = new JsonObject { ["deviceId"] = Guid.NewGuid().ToString() }.ToJsonString();
        string original = File.ReadAllText(f.StorePath);
        f.BlockWrites();
        f.Management.BindSchool(nextScope);
        f.Management.BindSchool(nextScope);
        Assert.Equal("EQUIPMENT_STORE_UNAVAILABLE", f.Management.ObserveSchool()["error"]?.GetValue<string>());
        Assert.Equal(original, File.ReadAllText(f.StorePath));
        f.RestoreWrites();
        f.Management.BindSchool(nextScope);
        Assert.Equal(nextScope, f.Saved()["Scope"]!.GetValue<string>());
        Assert.Equal(0, f.Saved()["Applied"]!.GetValue<long>());
        Assert.True(f.Cameras.Snapshot().Locked);
    }

    [Fact]
    public async Task RefusedUnlockDoesNotBecomeDurableWhenPolicyPersistenceLaterRecovers()
    {
        await using var f = new Fixture();
        f.BlockWrites();
        var reply = f.Noise.ManagementHandle(new(1, Guid.NewGuid(), "noise.management.command",
            NoiseManagement: new("unlock", Secret: "school-password")));
        Assert.Equal("EQUIPMENT_STORE_UNAVAILABLE", reply.ErrorCode);
        Assert.True(f.Cameras.Snapshot().Locked);
        f.RestoreWrites();
        f.Management.ApplySchool(f.Policy(1));
        Assert.Null(f.Management.ObserveSchool()["error"]);
        Assert.False(f.Saved()["Unlocked"]!.GetValue<bool>());
        Assert.True(f.Cameras.Snapshot().Locked);
        Assert.False(new SchoolManagementService(f.Noise, f.Cameras, f.DirectoryPath).ObserveSchool()["unlocked"]!.GetValue<bool>());
    }
}

using System.Text.Json.Nodes;
using NPEduTools.Host;
using NPEduTools.Integrations.Npep;

namespace NPEduTools.Tests;

public sealed partial class SchoolEquipmentPersistenceTests
{
    [Theory]
    [InlineData("negative-revision")]
    [InlineData("negative-applied")]
    [InlineData("applied-after-revision")]
    [InlineData("unsafe-revision")]
    [InlineData("unsafe-applied")]
    [InlineData("bad-microphone")]
    [InlineData("bad-camera")]
    [InlineData("oversize-scope")]
    public async Task InvalidSchoolEquipmentStoreCannotPublishPartialAppliedState(string variant)
    {
        await using var f = new Fixture();
        var saved = f.Saved();
        saved["Unlocked"] = true;
        switch (variant)
        {
            case "negative-revision": saved["Revision"] = -1L; break;
            case "negative-applied": saved["Applied"] = -1L; break;
            case "applied-after-revision": saved["Applied"] = 2L; break;
            case "unsafe-revision": saved["Revision"] = NpepProtocol.MaxInteger + 1; break;
            case "unsafe-applied": saved["Revision"] = NpepProtocol.MaxInteger + 1; saved["Applied"] = NpepProtocol.MaxInteger + 1; break;
            case "bad-microphone": saved["Microphone"] = "bad\nendpoint"; break;
            case "bad-camera": saved["Camera"] = "bad\nendpoint"; break;
            default: saved["Scope"] = new string('x', 4097); break;
        }
        string original = saved.ToJsonString(); File.WriteAllText(f.StorePath, original);
        var reloaded = new SchoolManagementService(f.Noise, f.Cameras, f.DirectoryPath);
        for (int attempt = 0; attempt < 2; attempt++)
        {
            var observed = reloaded.ObserveSchool();
            Assert.True(observed["error"]?.GetValue<string>() == "EQUIPMENT_STORE_UNAVAILABLE" &&
                observed["appliedRevision"]!.GetValue<long>() == 0 && !observed["unlocked"]!.GetValue<bool>(),
                $"{variant}: invalid saved policy leaked into receipt: {observed.ToJsonString()}");
            Assert.True(f.Cameras.Snapshot().Locked);
            Assert.Equal("mic-one", f.Noise.Snapshot().SelectedDeviceId);
            Assert.Equal("cam-one", f.Cameras.Snapshot().CameraId);
            Assert.Equal(original, File.ReadAllText(f.StorePath));
        }
    }

    [Theory]
    [InlineData(9007199254740992L)]
    [InlineData(long.MaxValue)]
    [InlineData(-1L)]
    public async Task InvalidIncomingEquipmentRevisionCannotChangePreferencesOrPersistAnUnreportableVersion(long revision)
    {
        await using var f = new Fixture();
        string original = File.ReadAllText(f.StorePath);
        var error = Record.Exception(() => f.Management.ApplySchool(f.Policy(revision)));
        Assert.True(error is NpepException { Code: "INVALID_RESPONSE" }, $"revision {revision} was accepted or misclassified: {error?.Message ?? "no error"}");
        Assert.Equal(original, File.ReadAllText(f.StorePath));
        Assert.Equal("mic-one", f.Noise.Snapshot().SelectedDeviceId);
        Assert.Equal("cam-one", f.Cameras.Snapshot().CameraId);
        Assert.Equal(1, f.Management.ObserveSchool()["appliedRevision"]!.GetValue<long>());
    }

    [Fact]
    public async Task MaximumWireRevisionRemainsApplicableAndSurvivesValidReload()
    {
        await using var f = new Fixture();
        f.Management.ApplySchool(f.Policy(NpepProtocol.MaxInteger));
        string original = File.ReadAllText(f.StorePath);
        var reloaded = new SchoolManagementService(f.Noise, f.Cameras, f.DirectoryPath);
        var observed = reloaded.ObserveSchool();
        Assert.Null(observed["error"]);
        Assert.Equal(NpepProtocol.MaxInteger, observed["appliedRevision"]!.GetValue<long>());
        Assert.Equal("mic-two", observed["selectedMicrophoneId"]?.GetValue<string>());
        Assert.Equal("cam-two", observed["selectedCameraId"]?.GetValue<string>());
        Assert.Equal(original, File.ReadAllText(f.StorePath));
        Assert.True(f.Cameras.Snapshot().Locked);
    }
}

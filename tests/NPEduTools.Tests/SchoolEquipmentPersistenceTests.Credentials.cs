using System.Text.Json.Nodes;
using NPEduTools.Contracts;
using NPEduTools.Integrations.Npep;

namespace NPEduTools.Tests;

public sealed partial class SchoolEquipmentPersistenceTests
{
    [Theory]
    [InlineData("salt", false)]
    [InlineData("salt", true)]
    [InlineData("hash", false)]
    [InlineData("hash", true)]
    public async Task StructurallyMissingCredentialFieldsKeepTheExistingWholeResponseRejection(string field, bool missing)
    {
        await using var f = new Fixture();
        string credentialPath = Path.Combine(f.DirectoryPath, "noise-management.json.school");
        string original = File.ReadAllText(credentialPath);
        var policy = f.Policy(2);
        var verifier = policy["credential"]!["verifier"]!.AsObject();
        if (missing) verifier.Remove(field); else verifier[field] = null;
        Assert.Equal("INVALID_RESPONSE", Assert.Throws<NpepException>(() => f.Management.ApplySchool(policy)).Code);
        Assert.Equal(1, f.Management.ObserveSchool()["appliedRevision"]!.GetValue<long>());
        Assert.Equal(1, f.Management.ObserveSchool()["credentialRevision"]!.GetValue<long>());
        Assert.Equal("mic-one", f.Noise.Snapshot().SelectedDeviceId);
        Assert.Equal("cam-one", f.Cameras.Snapshot().CameraId);
        Assert.Equal(original, File.ReadAllText(credentialPath));
    }

    [Fact]
    public async Task EquipmentPersistenceRecoveryCannotClearRejectedCredentialReceipt()
    {
        await using var f = new Fixture();
        var rejected = f.Policy(2);
        rejected["credential"]!["verifier"]!["iterations"] = 1;
        f.BlockWrites();
        f.Management.ApplySchool(rejected);
        Assert.Equal("EQUIPMENT_STORE_UNAVAILABLE", f.Management.ObserveSchool()["error"]?.GetValue<string>());
        Assert.Equal(1, f.Management.ObserveSchool()["appliedRevision"]!.GetValue<long>());
        f.RestoreWrites();
        // Retrying the same binding persists equipment only, not the rejected credential.
        f.Management.BindSchool(f.Identity.ToJsonString());
        Assert.Equal("INVALID_RESPONSE", f.Management.ObserveSchool()["error"]?.GetValue<string>());
        f.Management.ApplySchool(rejected);
        Assert.Equal("INVALID_RESPONSE", f.Management.ObserveSchool()["error"]?.GetValue<string>());
        Assert.Equal(2, f.Management.ObserveSchool()["appliedRevision"]!.GetValue<long>());
        f.Management.ApplySchool(f.Policy(2));
        Assert.Null(f.Management.ObserveSchool()["error"]);
    }

    [Fact]
    public async Task ANewBindingDoesNotInheritThePreviousSchoolsRejectedCredentialReceipt()
    {
        await using var f = new Fixture();
        var rejected = f.Policy(1);
        rejected["credential"]!["verifier"]!["iterations"] = 1;
        f.Management.ApplySchool(rejected);
        Assert.Equal("INVALID_RESPONSE", f.Management.ObserveSchool()["error"]?.GetValue<string>());
        f.Management.BindSchool(new JsonObject { ["deviceId"] = Guid.NewGuid().ToString() }.ToJsonString());
        Assert.Null(f.Management.ObserveSchool()["error"]);
        Assert.Equal(0, f.Management.ObserveSchool()["credentialRevision"]!.GetValue<long>());
        Assert.Equal(0, f.Management.ObserveSchool()["appliedRevision"]!.GetValue<long>());
        Assert.True(f.Cameras.Snapshot().Locked);
    }

    [Theory]
    [InlineData("blank-salt")]
    [InlineData("blank-hash")]
    [InlineData("iterations")]
    [InlineData("short-salt")]
    public async Task InvalidCredentialUpdateHasAReceiptEvenWhenIndependentEquipmentPolicySucceeds(string variant)
    {
        await using var f = new Fixture();
        string credentialPath = Path.Combine(f.DirectoryPath, "noise-management.json.school");
        string original = File.ReadAllText(credentialPath);
        var policy = f.Policy(2);
        var verifier = policy["credential"]!["verifier"]!.AsObject();
        switch (variant)
        {
            case "blank-salt": verifier["salt"] = " "; break;
            case "blank-hash": verifier["hash"] = " "; break;
            case "iterations": verifier["iterations"] = 1; break;
            default: verifier["salt"] = "AA=="; break;
        }
        Assert.Null(Record.Exception(() => f.Management.ApplySchool(policy)));
        for (int attempt = 0; attempt < 2; attempt++)
        {
            var observed = f.Management.ObserveSchool();
            Assert.Equal("INVALID_RESPONSE", observed["error"]?.GetValue<string>());
            Assert.Equal(1, observed["credentialRevision"]!.GetValue<long>());
            // Credential rejection cannot turn an independently applied device policy into failure.
            Assert.Equal(2, observed["appliedRevision"]!.GetValue<long>());
            Assert.Equal("mic-two", observed["selectedMicrophoneId"]?.GetValue<string>());
            Assert.Equal("cam-two", observed["selectedCameraId"]?.GetValue<string>());
            Assert.True(f.Cameras.Snapshot().Locked);
            Assert.Equal(original, File.ReadAllText(credentialPath));
        }
        var verification = f.Noise.ManagementHandle(new(1, Guid.NewGuid(), "noise.management.command",
            NoiseManagement: new("verify", Secret: "school-password")));
        Assert.Equal("Succeeded", verification.Outcome);

        var repaired = f.Policy(2);
        repaired["credential"]!["revision"] = 2L;
        f.Management.ApplySchool(repaired);
        var recovered = f.Management.ObserveSchool();
        Assert.Null(recovered["error"]);
        Assert.Equal(2, recovered["credentialRevision"]!.GetValue<long>());
        Assert.Equal(2, recovered["appliedRevision"]!.GetValue<long>());
        Assert.Equal(2, JsonNode.Parse(File.ReadAllText(credentialPath))!["Revision"]!.GetValue<long>());
    }
}

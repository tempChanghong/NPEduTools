using System.Security.Cryptography;
using System.Text.Json.Nodes;
using NPEduTools.Contracts;
using NPEduTools.Host;

namespace NPEduTools.Tests;

public sealed class SchoolManagementTests
{
    private const string Local="local-password",School="school-password";
    internal static JsonObject Credential(long revision=1,string secret=School)
    {
        byte[] salt=RandomNumberGenerator.GetBytes(16),hash=Rfc2898DeriveBytes.Pbkdf2(secret,salt,210000,HashAlgorithmName.SHA256,32);
        return new(){["revision"]=revision,["updatedAt"]="2026-10-11T00:00:00.000Z",["verifier"]=new JsonObject{["iterations"]=210000,["salt"]=Convert.ToBase64String(salt),["hash"]=Convert.ToBase64String(hash)}};
    }
    [Fact]
    public void SchoolLatchRetiresLocalPasswordSurvivesOfflineAndUnpairNeedsNewLocalPassword()
    {
        string path=Path.Combine(Path.GetTempPath(),"school-management-"+Guid.NewGuid());
        try
        {
            var store=new NoiseManagementStore(path);Assert.Null(store.Configure(null,Local,false));
            Assert.Null(store.BindSchool("scope"));Assert.False(store.Configured);
            Assert.Null(store.ApplySchool("scope",Credential()));Assert.Equal("School",store.Source);
            Assert.Equal("MANAGEMENT_SECRET_INVALID",store.VerifyCalibration(Local));Assert.Null(store.VerifyCalibration(School));
            Assert.Equal("SCHOOL_MANAGEMENT_REQUIRED",store.Configure(School,"new-local-password",false));
            store=new(path);Assert.Equal("School",store.Source);Assert.Null(store.VerifyCalibration(School));
            Assert.Null(store.BindSchool(null));Assert.False(store.Configured);Assert.Null(store.Configure(null,"new-local-password",false));
            store=new(path);Assert.Equal("Local",store.Source);Assert.Null(store.VerifyCalibration("new-local-password"));Assert.Equal("MANAGEMENT_SECRET_INVALID",store.VerifyCalibration(Local));
        }
        finally{if(Directory.Exists(path))Directory.Delete(path,true);}
    }
    [Fact]
    public void FailedSchoolWriteRemainsClosedAndNeverUsesLocalFallback()
    {
        string path=Path.Combine(Path.GetTempPath(),"school-management-"+Guid.NewGuid());
        try
        {
            var store=new NoiseManagementStore(path);Assert.Null(store.Configure(null,Local,false));
            Directory.CreateDirectory(Path.Combine(path,"noise-management.json.school.tmp"));
            Assert.Equal("MANAGEMENT_STORE_UNAVAILABLE",store.BindSchool("scope"));
            Assert.Equal("MANAGEMENT_STORE_UNAVAILABLE",store.VerifyCalibration(Local));Assert.False(store.Configured);
        }
        finally{if(Directory.Exists(path))Directory.Delete(path,true);}
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SchoolCameraClearReceiptMatchesPersistedSelectionAfterHostRestart(bool brokenConfiguration)
    {
        string path=Path.Combine(Path.GetTempPath(),"school-camera-startup-"+Guid.NewGuid());
        var identity=new JsonObject{["deviceId"]=Guid.NewGuid().ToString()};
        try
        {
            Directory.CreateDirectory(path);
            string cameraPath=Path.Combine(path,"study-camera.json");
            string original=brokenConfiguration?"{broken camera configuration":"\"saved-camera\"";
            File.WriteAllText(cameraPath,original);
            await using var noise=new NoiseService(_=>throw new InvalidOperationException("No real capture"),()=>[],directory:path,management:new NoiseManagementStore(path));
            var camera=new StudyDeviceService(()=>[],path);
            var management=new SchoolManagementService(noise,camera,path);
            management.BindSchool(identity.ToJsonString());
            var policy=new JsonObject{["identity"]=identity.DeepClone(),["credential"]=Credential(),["equipment"]=new JsonObject{["revision"]=1,["locked"]=true,["microphoneId"]=null,["cameraId"]=null}};

            // No inventory/status read has loaded the camera preference yet.
            management.ApplySchool(policy);
            var observation=management.ObserveSchool();
            Assert.Equal(brokenConfiguration?0:1,observation["appliedRevision"]!.GetValue<long>());
            Assert.Equal(brokenConfiguration?"EQUIPMENT_UNAVAILABLE":null,observation["error"]?.GetValue<string>());
            Assert.Null(observation["selectedCameraId"]);
            Assert.True(camera.Snapshot().Locked);
            if(brokenConfiguration) Assert.Equal(original,File.ReadAllText(cameraPath));
            else Assert.Null(new StudyDeviceService(()=>[],path).Snapshot().CameraId);
        }
        finally{if(Directory.Exists(path))Directory.Delete(path,true);}
    }
}

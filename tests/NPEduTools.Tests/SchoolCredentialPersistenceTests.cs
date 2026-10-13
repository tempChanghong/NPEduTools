using System.Text.Json.Nodes;
using NPEduTools.Host;
using NPEduTools.Integrations.Npep;

namespace NPEduTools.Tests;

public sealed class SchoolCredentialPersistenceTests
{
    private const string Local = "local-password", School = "school-password";

    private sealed class Fixture : IDisposable
    {
        public string DirectoryPath { get; } = Path.Combine(Path.GetTempPath(), "school-credential-validation-" + Guid.NewGuid());
        public string LocalPath => Path.Combine(DirectoryPath, "noise-management.json");
        public string SchoolPath => LocalPath + ".school";
        public string OriginalLocal { get; }
        public Fixture(long revision = 1)
        {
            var store = new NoiseManagementStore(DirectoryPath);
            Assert.Null(store.Configure(null, Local, false));
            OriginalLocal = File.ReadAllText(LocalPath);
            var source = new NoiseManagementStore(DirectoryPath);
            Assert.Null(source.Configure(Local, School, false));
            var credential = JsonNode.Parse(File.ReadAllText(LocalPath))!.AsObject();
            var record = new JsonObject { ["Version"] = 1, ["Scope"] = "scope", ["Revision"] = revision,
                ["Credential"] = credential };
            File.WriteAllText(SchoolPath, record.ToJsonString());
            // A stale local file can coexist after a failed delete; the school latch must win.
            File.WriteAllText(LocalPath, OriginalLocal);
        }
        public JsonObject Saved() => JsonNode.Parse(File.ReadAllText(SchoolPath))!.AsObject();
        public void Dispose() => Directory.Delete(DirectoryPath, true);
    }

    [Theory]
    [InlineData("missing-salt")]
    [InlineData("null-salt")]
    [InlineData("missing-hash")]
    [InlineData("null-hash")]
    [InlineData("blank-salt")]
    [InlineData("blank-hash")]
    [InlineData("unsafe-revision")]
    [InlineData("maximum-int64")]
    [InlineData("bad-base64")]
    public void InvalidSchoolCredentialCannotCrashLoadingOrReviveLocalPassword(string variant)
    {
        using var f = new Fixture();
        var saved = f.Saved(); var credential = saved["Credential"]!.AsObject();
        switch (variant)
        {
            case "missing-salt": credential.Remove("Salt"); break;
            case "null-salt": credential["Salt"] = null; break;
            case "missing-hash": credential.Remove("Hash"); break;
            case "null-hash": credential["Hash"] = null; break;
            case "blank-salt": credential["Salt"] = " "; break;
            case "blank-hash": credential["Hash"] = " "; break;
            case "unsafe-revision": saved["Revision"] = NpepProtocol.MaxInteger + 1; break;
            case "maximum-int64": saved["Revision"] = long.MaxValue; break;
            default: credential["Salt"] = "not base64"; break;
        }
        string original = saved.ToJsonString(); File.WriteAllText(f.SchoolPath, original);
        for (int attempt = 0; attempt < 2; attempt++)
        {
            var store = new NoiseManagementStore(f.DirectoryPath);
            Assert.Equal("School", store.Source);
            Assert.False(store.Configured);
            Assert.Equal("MANAGEMENT_STORE_UNAVAILABLE", store.Error);
            Assert.Equal(0, store.SchoolRevision);
            Assert.Equal("MANAGEMENT_STORE_UNAVAILABLE", store.VerifyCalibration(Local));
            Assert.Equal("MANAGEMENT_STORE_UNAVAILABLE", store.VerifyCalibration(School));
            Assert.Equal("SCHOOL_MANAGEMENT_REQUIRED", store.Configure(null, "replacement-password", false));
            Assert.Equal(original, File.ReadAllText(f.SchoolPath));
            Assert.Equal(f.OriginalLocal, File.ReadAllText(f.LocalPath));
        }
    }

    [Theory]
    [InlineData(1L)]
    [InlineData(9007199254740991L)]
    public void ValidSchoolCredentialStillOverridesStaleLocalFile(long revision)
    {
        using var f = new Fixture(revision);
        var store = new NoiseManagementStore(f.DirectoryPath);
        Assert.True(store.Configured); Assert.Null(store.Error);
        Assert.Equal(revision, store.SchoolRevision);
        Assert.Null(store.VerifyCalibration(School));
        Assert.Equal("MANAGEMENT_SECRET_INVALID", store.VerifyCalibration(Local));
    }

    [Fact]
    public void RetiredSchoolLatchStillLoadsNewLocalCredential()
    {
        using var f = new Fixture();
        var store = new NoiseManagementStore(f.DirectoryPath);
        Assert.Null(store.BindSchool(null));
        Assert.Null(store.Configure(null, "replacement-password", false));
        var reloaded = new NoiseManagementStore(f.DirectoryPath);
        Assert.Equal("Local", reloaded.Source); Assert.True(reloaded.Configured);
        Assert.Null(reloaded.VerifyCalibration("replacement-password"));
        Assert.Equal("MANAGEMENT_SECRET_INVALID", reloaded.VerifyCalibration(School));
        Assert.Equal("MANAGEMENT_SECRET_INVALID", reloaded.VerifyCalibration(Local));
    }
}

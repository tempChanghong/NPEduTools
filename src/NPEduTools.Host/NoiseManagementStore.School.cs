using System.Text.Json;
using System.Text.Json.Nodes;

namespace NPEduTools.Host;

public sealed partial class NoiseManagementStore
{
    private string SchoolPath => _path + ".school";
    private string? _schoolScope;
    private long _schoolRevision;
    private sealed record SchoolCredential(int Version, string? Scope, long Revision, Credential? Credential);
    public string Source => _schoolScope is null ? "Local" : "School";
    public long SchoolRevision => _schoolRevision;
    private void LoadSchool()
    {
        if (!File.Exists(SchoolPath)) return;
        // A durable latch prevents a stale local password from coming back after a restart or unpair.
        _credential = null; _schoolScope = "UNAVAILABLE";
        try
        {
            if (new FileInfo(SchoolPath).Length > 8192) throw new InvalidDataException();
            var saved = JsonSerializer.Deserialize<SchoolCredential>(File.ReadAllText(SchoolPath)) ?? throw new InvalidDataException();
            if (saved.Version != 1 || saved.Revision < 0 || saved.Scope is {Length: > 4096} ||
                saved.Credential is { } c && (c.Version != 1 || Convert.FromBase64String(c.Salt).Length != 16 || Convert.FromBase64String(c.Hash).Length != 32))
                throw new InvalidDataException();
            _schoolScope = saved.Scope; _schoolRevision = saved.Revision; _credential = saved.Credential; _error = null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or InvalidDataException or FormatException)
        { _error = "MANAGEMENT_STORE_UNAVAILABLE"; }
    }
    internal string? BindSchool(string? scope)
    {
        if (_schoolScope == scope) return _error;
        // Retire local credentials in memory before attempting IO. A failure stays closed.
        _credential = null; _schoolScope = scope; _schoolRevision = 0; _tickets.Clear();
        return SaveSchool();
    }
    internal string? ApplySchool(string scope, JsonObject? row)
    {
        if (_schoolScope != scope) return "SESSION_SUPERSEDED";
        if (row is null) return "MANAGEMENT_NOT_CONFIGURED";
        long revision = row["revision"]!.GetValue<long>();
        if(revision<1 || revision>9007199254740991)return "INVALID_RESPONSE";
        if (revision < _schoolRevision) return "POLICY_CHANGED";
        var v = row["verifier"]!.AsObject();
        var credential = new Credential(1, v["salt"]!.GetValue<string>(), v["hash"]!.GetValue<string>());
        if (v["iterations"]!.GetValue<int>() != Iterations || Convert.FromBase64String(credential.Salt).Length != 16 || Convert.FromBase64String(credential.Hash).Length != 32)
            return "INVALID_RESPONSE";
        if (revision == _schoolRevision && credential == _credential && _error is null) return null;
        _credential = credential; _schoolRevision = revision; _tickets.Clear(); _failures = 0; _lockedAt = null;
        return SaveSchool();
    }
    private string? SaveSchool()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            using(var stream = new FileStream(SchoolPath + ".tmp", FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            { JsonSerializer.Serialize(stream, new SchoolCredential(1, _schoolScope, _schoolRevision, _credential)); stream.Flush(true); }
            File.Move(SchoolPath + ".tmp", SchoolPath, true);
            // The latch is durable first; failure to remove the old file cannot revive it.
            if(File.Exists(_path)) File.Delete(_path);
            _error = null; return null;
        }
        catch(Exception e) when(e is IOException or UnauthorizedAccessException)
        { _error = "MANAGEMENT_STORE_UNAVAILABLE"; return _error; }
    }
}

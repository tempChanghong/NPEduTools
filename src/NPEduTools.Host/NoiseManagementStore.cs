using System.Security.Cryptography;
using System.Text.Json;
using NPEduTools.Contracts;

namespace NPEduTools.Host;

/// <summary>Local offline maintenance credential; distinct from the school PIN. Caller holds capture lock.</summary>
public sealed class NoiseManagementStore
{
    private readonly string _path;
    private readonly TimeProvider _time;
    private Credential? _credential;
    private string? _error;
    private int _failures;
    private long? _lockedAt;
    private readonly Dictionary<Guid, Ticket> _tickets = [];
    private sealed record Credential(int Version, string Salt, string Hash);
    private sealed record Ticket(Guid InstanceId, Guid SessionId, string Purpose, Guid RequestId, long IssuedAt);
    private const int Iterations = 210000;
    public NoiseManagementStore(string directory, TimeProvider? time = null)
    {
        _path = Path.Combine(directory, "noise-management.json"); _time = time ?? TimeProvider.System;
        try
        {
            if (!File.Exists(_path)) return;
            if (new FileInfo(_path).Length > 2048) throw new InvalidDataException();
            _credential = JsonSerializer.Deserialize<Credential>(File.ReadAllText(_path));
            if (_credential is not { Version: 1 } || string.IsNullOrWhiteSpace(_credential.Salt) ||
                string.IsNullOrWhiteSpace(_credential.Hash) || Convert.FromBase64String(_credential.Salt).Length != 16 ||
                Convert.FromBase64String(_credential.Hash).Length != 32) throw new InvalidDataException();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or InvalidDataException or FormatException)
        { _error = "MANAGEMENT_STORE_UNAVAILABLE"; }
    }
    public bool Configured => _credential is not null && _error is null;
    public string? Error => _error;
    public string? Configure(string? oldSecret, string newSecret, bool protectedSession)
    {
        if (_error is not null) return _error;
        if (!NoiseManagementContract.SecretValid(newSecret)) return "INVALID_MANAGEMENT_SECRET";
        // Initial provisioning is not an escape hatch once a scheduled session has started.
        if (_credential is null && protectedSession) return "MANAGEMENT_NOT_CONFIGURED";
        if (_credential is not null && Verify(oldSecret) is { } denied) return denied;
        var salt = RandomNumberGenerator.GetBytes(16);
        var c = new Credential(1, Convert.ToBase64String(salt), Convert.ToBase64String(Derive(newSecret, salt)));
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            using (var file = new FileStream(_path + ".tmp", FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            { JsonSerializer.Serialize(file, c); file.Flush(true); }
            File.Move(_path + ".tmp", _path, true); _credential = c; _tickets.Clear(); return null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { _error = "MANAGEMENT_STORE_UNAVAILABLE"; return _error; }
    }
    private static byte[] Derive(string secret, byte[] salt) => Rfc2898DeriveBytes.Pbkdf2(secret, salt, Iterations, HashAlgorithmName.SHA256, 32);
    private string? Verify(string? secret)
    {
        if (_error is not null) return _error;
        if (_credential is null) return "MANAGEMENT_NOT_CONFIGURED";
        if (_lockedAt is { } since && _time.GetElapsedTime(since).TotalSeconds < 60) return "MANAGEMENT_RATE_LIMITED";
        if (_lockedAt is not null) { _lockedAt = null; _failures = 0; }
        if (!NoiseManagementContract.SecretValid(secret) || !CryptographicOperations.FixedTimeEquals(
            Derive(secret!, Convert.FromBase64String(_credential.Salt)), Convert.FromBase64String(_credential.Hash)))
        {
            if (++_failures >= 5) _lockedAt = _time.GetTimestamp();
            return "MANAGEMENT_SECRET_INVALID";
        }
        _failures = 0; return null;
    }
    public (Guid? Ticket, string? Error) Issue(NoiseManagementCommand c, Guid instance, Guid session)
    {
        if (c.InstanceId != instance || c.SessionId != session) return (null, "NoiseStateChanged");
        if (Verify(c.Secret) is { } error) return (null, error);
        foreach (var id in _tickets.Where(p => _time.GetElapsedTime(p.Value.IssuedAt).TotalSeconds >= 60).Select(p => p.Key).ToArray()) _tickets.Remove(id);
        if (_tickets.Count >= 32) return (null, "MANAGEMENT_RATE_LIMITED");
        Guid ticket = Guid.NewGuid();
        _tickets[ticket] = new(instance, session, c.Purpose!, c.TargetRequestId!.Value, _time.GetTimestamp());
        return (ticket, null);
    }
    public bool Consume(Guid? ticket, Guid instance, Guid session, string purpose, Guid requestId)
    {
        if (ticket is null || !_tickets.TryGetValue(ticket.Value, out var t) ||
            t.InstanceId != instance || t.SessionId != session || t.Purpose != purpose || t.RequestId != requestId ||
            _time.GetElapsedTime(t.IssuedAt).TotalSeconds >= 60) return false;
        _tickets.Remove(ticket.Value); return true;
    }
}

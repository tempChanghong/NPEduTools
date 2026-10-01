using System.Text.Json;
using NPEduTools.Contracts;

namespace NPEduTools.Integrations.Npep;

internal sealed record ControlPolicyDocument(int Version, long Revision, bool Allowed, string? Scope,
    Guid ConsentId, Guid ControlEpoch);

/// <summary>Owned by one Host and serialized under NpepRuntime._sync. Never contains credentials.</summary>
internal sealed class NpepControlPolicyStore
{
    private readonly string _path;
    internal ControlPolicyDocument State { get; private set; } = new(3, 0, false, null, Guid.NewGuid(), Guid.NewGuid());
    internal string? Error { get; private set; }

    internal NpepControlPolicyStore(string directory, string fileName = "runtime-control-policy.json")
    {
        _path = Path.Combine(directory, fileName);
        try
        {
            Directory.CreateDirectory(directory);
            if (File.Exists(_path + ".pending")) throw new InvalidDataException();
            if (File.Exists(_path))
            {
                if (new FileInfo(_path).Length > 8192) throw new InvalidDataException();
                var saved = JsonSerializer.Deserialize<ControlPolicyDocument>(File.ReadAllBytes(_path), Protocol.Json);
                if (saved is null || saved.Version is not (1 or 2 or 3) || saved.Revision < 0 || saved.ConsentId == Guid.Empty ||
                    saved.ControlEpoch == Guid.Empty || saved.Allowed && saved.Scope is null ||
                    saved.Scope is { } scope && (scope.Length != 64 || !scope.All(char.IsAsciiHexDigit))) throw new InvalidDataException();
                // Pairing is authoritative. Revalidate the current binding before enabling after migration.
                State = saved.Version < 3 ? saved with { Version = 3, Allowed = false, Scope = null, ConsentId = Guid.NewGuid() } : saved;
            }
            Rotate(); // Persist a new epoch before any future execution after a Host restart.
        }
        catch (Exception e) when (StorageError(e)) { Fail(); }
    }

    internal void Synchronize(string? scope)
    {
        if (Error is not null || State.Scope == scope && State.Allowed == (scope is not null)) return;
        Save(State with { Allowed = scope is not null, Scope = scope, ConsentId = Guid.NewGuid(), ControlEpoch = Guid.NewGuid() });
    }

    internal void Rotate()
    {
        if (Error is null) Save(State with { ControlEpoch = Guid.NewGuid() });
    }

    internal void Set(bool allowed, long revision, string? scope)
    {
        if (Error is not null) throw new NpepException("POLICY_STORE_UNAVAILABLE");
        if (State.Revision != revision || State.Scope != scope) throw new NpepException("POLICY_CHANGED");
        if (allowed && scope is null) throw new NpepException("CONTROL_NOT_PAIRED");
        if (State.Allowed == allowed) return;
        Save(State with { Allowed = allowed, ConsentId = Guid.NewGuid(), ControlEpoch = Guid.NewGuid() });
        if (Error is not null) throw new NpepException("POLICY_STORE_UNAVAILABLE");
    }

    private void Save(ControlPolicyDocument next)
    {
        try
        {
            next = next with { Revision = checked(State.Revision + 1) };
            using (var file = new FileStream(_path + ".pending", FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                file.Write(JsonSerializer.SerializeToUtf8Bytes(next, Protocol.Json));
                file.Flush(true);
            }
            File.Move(_path + ".pending", _path, true);
            State = next;
        }
        catch (Exception e) when (StorageError(e)) { Fail(); }
    }
    private void Fail() { Error = "POLICY_STORE_UNAVAILABLE"; State = State with { Allowed = false }; }
    private static bool StorageError(Exception e) => e is IOException or InvalidDataException or UnauthorizedAccessException or JsonException or OverflowException;
}

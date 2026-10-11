using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace NPEduTools.Host;

/// <summary>Durable elapsed cooldown, not a wall-clock deadline. Downtime never earns elapsed credit.</summary>
public sealed class StudyReminderStore
{
    private const int MaxScopes = 16, MaxSeconds = 86400;
    private readonly object _sync = new();
    private readonly TimeProvider _time;
    private readonly string? _path;
    private readonly Dictionary<string, Counter> _records = [];
    private long _checkpoint;
    private sealed record Counter(double SavedAge, long Since);
    private sealed record Entry(string ScopeHash, double ElapsedSeconds);
    private sealed record Journal(int Version, IReadOnlyList<Entry> Entries);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    { MaxDepth = 8, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };
    public string? Error { get; private set; }
    public StudyReminderStore(string? directory = null, TimeProvider? time = null)
    {
        _time = time ?? TimeProvider.System; _checkpoint = _time.GetTimestamp();
        _path = directory is null ? null : Path.Combine(directory, "study-reminder-state.json");
        try
        {
            if (_path is null || !File.Exists(_path)) return;
            if (new FileInfo(_path).Length > 8192) throw new InvalidDataException();
            var journal = JsonSerializer.Deserialize<Journal>(File.ReadAllText(_path), Json);
            if (journal is not { Version: 1, Entries: not null } || journal.Entries.Count > MaxScopes) throw new InvalidDataException();
            foreach (var entry in journal.Entries)
            {
                if (entry is null || entry.ScopeHash is not { Length: 64 } hash || hash.Any(c => c is not (>= '0' and <= '9' or >= 'a' and <= 'f')) ||
                    !double.IsFinite(entry.ElapsedSeconds) || entry.ElapsedSeconds is < 0 or > MaxSeconds ||
                    !_records.TryAdd(hash, new(entry.ElapsedSeconds, _checkpoint))) throw new InvalidDataException();
            }
        }
        catch (Exception e) when (StorageError(e)) { Error = "STUDY_REMINDER_STORE_UNAVAILABLE"; }
    }
    private static bool StorageError(Exception e) => e is IOException or UnauthorizedAccessException or JsonException or InvalidDataException;
    private static string Key(string scope) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(scope)));
    private double Age(Counter record) => Math.Min(MaxSeconds, record.SavedAge + Math.Max(0, _time.GetElapsedTime(record.Since).TotalSeconds));
    public bool CanRemind(string scope, int seconds)
    {
        lock (_sync)
        {
            if (Error is not null || seconds is < 1 or > MaxSeconds) return false;
            if (!_records.TryGetValue(Key(scope), out var record)) return true;
            double age = Age(record);
            // Only a cooling record needs periodic writes. Checkpoint once at the configured end as well.
            if (_path is not null && (age < seconds && _time.GetElapsedTime(_checkpoint).TotalSeconds >= 30 || age >= seconds && record.SavedAge < seconds))
                if (!Save(_records)) return false;
            return age >= seconds;
        }
    }
    public bool Claim(string scope)
    {
        lock (_sync)
        {
            if (Error is not null) return false;
            var next = new Dictionary<string, Counter>(_records);
            foreach (var key in next.Where(p => Age(p.Value) >= MaxSeconds).Select(p => p.Key).ToArray()) next.Remove(key);
            string hash = Key(scope);
            if (!next.ContainsKey(hash) && next.Count >= MaxScopes) { Error = "STUDY_REMINDER_STORE_FULL"; return false; }
            next[hash] = new(0, _time.GetTimestamp());
            return Save(next, hash); // Persist before a playback grant; uncertain writes never authorize a sound.
        }
    }
    private bool Save(IReadOnlyDictionary<string, Counter> records, string? resetHash = null)
    {
        var entries = records.Select(p => new Entry(p.Key, p.Key == resetHash ? 0 : Age(p.Value))).ToArray();
        try
        {
            if (_path is not null)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
                using (var file = new FileStream(_path + ".tmp", FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
                { JsonSerializer.Serialize(file, new Journal(1, entries), Json); file.Flush(true); }
                File.Move(_path + ".tmp", _path, true);
            }
            long now = _time.GetTimestamp();
            _records.Clear(); foreach (var entry in entries) _records.Add(entry.ScopeHash, new(entry.ElapsedSeconds, now));
            _checkpoint = now; return true;
        }
        catch (Exception e) when (StorageError(e)) { Error = "STUDY_REMINDER_STORE_UNAVAILABLE"; return false; }
    }
}

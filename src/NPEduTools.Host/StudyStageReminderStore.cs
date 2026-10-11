using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using NPEduTools.Contracts;

namespace NPEduTools.Host;

public enum StudyStageClaim { Granted, Duplicate, Unavailable }

/// <summary>Bounded stage-audio tombstones. A grant is saved before any App may play it.</summary>
public sealed class StudyStageReminderStore
{
    private const int Limit = 128;
    private readonly object _sync = new();
    private readonly string? _path;
    private readonly List<string> _keys = [];
    private sealed record Journal(int Version, IReadOnlyList<string> Keys);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    { MaxDepth = 4, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };
    public string? Error { get; private set; }
    public StudyStageReminderStore(string? directory = null)
    {
        _path = directory is null ? null : Path.Combine(directory, "study-stage-reminders.json");
        try
        {
            if (_path is null || !File.Exists(_path)) return;
            if (new FileInfo(_path).Length > 16384) throw new InvalidDataException();
            var journal = JsonSerializer.Deserialize<Journal>(File.ReadAllText(_path), Json);
            if (journal is not { Version: 1, Keys: not null } || journal.Keys.Count > Limit ||
                journal.Keys.Any(key => key is not { Length: 64 } || key.Any(c => c is not (>= '0' and <= '9' or >= 'a' and <= 'f'))) ||
                journal.Keys.Distinct(StringComparer.Ordinal).Count() != journal.Keys.Count) throw new InvalidDataException();
            _keys.AddRange(journal.Keys);
        }
        catch (Exception e) when (StorageError(e)) { Error = "STUDY_STAGE_STORE_UNAVAILABLE"; }
    }
    private static bool StorageError(Exception e) => e is IOException or UnauthorizedAccessException or JsonException or InvalidDataException;
    public StudyStageClaim Claim(string scope, SchoolNoiseWindow window, string phase)
    {
        lock (_sync)
        {
            if (Error is not null) return StudyStageClaim.Unavailable;
            string key = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(
                scope + "\n" + window.Start.ToString("O", CultureInfo.InvariantCulture) + "\n" +
                window.End.ToString("O", CultureInfo.InvariantCulture) + "\n" + phase)));
            if (_keys.Contains(key, StringComparer.Ordinal)) return StudyStageClaim.Duplicate;
            var next = _keys.TakeLast(Limit - 1).Append(key).ToArray();
            try
            {
                if (_path is not null)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
                    using (var file = new FileStream(_path + ".tmp", FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
                    { JsonSerializer.Serialize(file, new Journal(1, next), Json); file.Flush(true); }
                    File.Move(_path + ".tmp", _path, true);
                }
                _keys.Clear(); _keys.AddRange(next); return StudyStageClaim.Granted;
            }
            catch (Exception e) when (StorageError(e)) { Error = "STUDY_STAGE_STORE_UNAVAILABLE"; return StudyStageClaim.Unavailable; }
        }
    }
}

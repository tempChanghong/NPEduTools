using System.Security.Cryptography;
using System.Text.Json;
using NPEduTools.Contracts;
using NPEduTools.Integrations.Npep;
namespace NPEduTools.Host;
public sealed class StudyPhotoJournal(string directory, Func<byte[], bool, byte[]> protect, TimeProvider? time = null)
{
    private readonly object _sync = new();
    private readonly string _root = Path.GetFullPath(directory);
    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private DateTimeOffset? _lastUtc;
    private long? _lastMono;
    private bool _loaded;
    public string? Error { get; private set; }
    private string[] Files() => Directory.Exists(_root) ? Directory.EnumerateFiles(_root, "*.photo").Take(257).ToArray() : [];
    private static bool Owned(string file) => Guid.TryParseExact(Path.GetFileNameWithoutExtension(file), "N", out _);
    private void Atomic(string file, byte[] bytes)
    {
        Directory.CreateDirectory(_root);
        string temporary = file + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { using (var s = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough)) { s.Write(bytes); s.Flush(true); } File.Move(temporary, file, true); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    public bool Claim(DateTimeOffset serverNow, int seconds)
    {
        lock (_sync)
        {
            try
            {
                if (!_loaded)
                {
                    _loaded = true;
                    var file = Path.Combine(_root, "cooldown.json");
                    if (File.Exists(file))
                    {
                        if (new FileInfo(file).Length > 1024) throw new InvalidDataException();
                        _lastUtc = JsonSerializer.Deserialize<DateTimeOffset>(File.ReadAllText(file));
                        if (serverNow < _lastUtc) _lastMono = _time.GetTimestamp(); // Conservative restart hold on rollback.
                    }
                }
                if (Error is not null) return false;
                if (_lastMono is { } mono ? _time.GetElapsedTime(mono).TotalSeconds < seconds
                    : _lastUtc is { } utc && (serverNow - utc).TotalSeconds < seconds) return false;
                Atomic(Path.Combine(_root, "cooldown.json"), JsonSerializer.SerializeToUtf8Bytes(serverNow));
                _lastUtc = serverNow; _lastMono = _time.GetTimestamp(); return true;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
            { Error = "PHOTO_JOURNAL_UNAVAILABLE"; return false; }
        }
    }
    public void Save(StudyPhotoPacket packet)
    {
        lock (_sync)
        {
            var files = Files();
            if (files.Length >= 256 || files.Sum(f => new FileInfo(f).Length) >= 64 * 1024 * 1024) throw new IOException("PHOTO_QUEUE_FULL");
            if (packet.Bytes.Length is <= 0 or > 262144) throw new InvalidDataException();
            NpepPhotoProtocol.Validate("metadata", packet.Metadata);
            byte[] clear = JsonSerializer.SerializeToUtf8Bytes(packet, Protocol.Json), encrypted;
            try { encrypted = protect(clear, true); } finally { CryptographicOperations.ZeroMemory(clear); }
            if (files.Sum(f => new FileInfo(f).Length) + encrypted.Length > 64 * 1024 * 1024) throw new IOException("PHOTO_QUEUE_FULL");
            Atomic(Path.Combine(_root, Guid.Parse(packet.Metadata.Text("eventId")).ToString("N") + ".photo"), encrypted);
        }
    }
    public StudyPhotoPacket? Next(string scope, string camera, string microphone, DateTimeOffset? serverNow)
    {
        lock (_sync)
        {
            try
            {
                foreach (string file in Files().Where(Owned).OrderBy(f => File.GetCreationTimeUtc(f)))
                {
                    if (new FileInfo(file).Length > 524288) throw new InvalidDataException();
                    byte[] clear = protect(File.ReadAllBytes(file), false); StudyPhotoPacket packet;
                    try { packet = JsonSerializer.Deserialize<StudyPhotoPacket>(clear, Protocol.Json) ?? throw new InvalidDataException(); }
                    finally { CryptographicOperations.ZeroMemory(clear); }
                    if (packet.Metadata is null || packet.Bytes is null || string.IsNullOrWhiteSpace(packet.Scope) ||
                        !NoiseContract.ValidDeviceId(packet.Camera) || !NoiseContract.ValidDeviceId(packet.Microphone)) throw new InvalidDataException();
                    NpepPhotoProtocol.Validate("metadata", packet.Metadata);
                    if (packet.Scope != scope || packet.Camera != camera || packet.Microphone != microphone ||
                        serverNow is { } now && now - DateTimeOffset.Parse(packet.Metadata.Text("capturedAt")) > TimeSpan.FromDays(1)) { File.Delete(file); continue; }
                    if (Convert.ToHexStringLower(SHA256.HashData(packet.Bytes)) != packet.Metadata.Text("hash")) throw new InvalidDataException();
                    return packet;
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or InvalidDataException or NpepException or FormatException)
            { Error = "PHOTO_QUEUE_UNAVAILABLE"; }
            return null;
        }
    }
    public int Pending { get { lock (_sync) { try { return Files().Count(Owned); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Error = "PHOTO_QUEUE_UNAVAILABLE"; return 0; } } } }
    public void Remove(Guid id) { lock (_sync) File.Delete(Path.Combine(_root, id.ToString("N") + ".photo")); }
    public void Clear() { lock (_sync) foreach (var file in Files().Where(Owned)) File.Delete(file); }
}

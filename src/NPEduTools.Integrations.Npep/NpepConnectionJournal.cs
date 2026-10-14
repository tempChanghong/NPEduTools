using System.Text.Json;
using System.Threading.Channels;
using NPEduTools.Contracts;

namespace NPEduTools.Integrations.Npep;

// Bounded support history, independent of credentials. Disk writes never run on a network/Host gate.
internal sealed class NpepConnectionJournal : IAsyncDisposable
{
    private readonly object _sync = new();
    private readonly string? _directory;
    private readonly TimeProvider _time;
    private readonly List<NpepConnectionEvent> _events = [];
    private readonly Channel<bool> _dirty = Channel.CreateBounded<bool>(new BoundedChannelOptions(1) { SingleReader = true, FullMode = BoundedChannelFullMode.DropWrite });
    private readonly Task _writer;
    private long _version, _saved;
    private bool _unavailable;
    public NpepConnectionJournal(string? directory, TimeProvider? time = null)
    {
        _directory = directory; _time = time ?? TimeProvider.System;
        if (directory is not null)
        {
            try {
                using var stream = new FileStream(Path.Combine(directory, "connection-history.json"), FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                if (stream.Length > 32768) throw new JsonException();
                var entries = JsonSerializer.Deserialize<NpepConnectionEvent[]>(stream, Protocol.Json) ?? [];
                _events.AddRange(entries.Where(e => e is not null && e.Id != Guid.Empty && e.At >= _time.GetUtcNow().AddDays(-7)).TakeLast(NpepDiagnosticValues.MaxEvents).Select(NpepDiagnosticValues.Safe));
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) { _unavailable = true; }
        }
        else _unavailable = true;
        _writer = WriteLoopAsync();
    }
    public void Add(string kind, string endpoint, string? code = null, int? status = null, Guid? requestId = null, long? elapsedMs = null, bool? retrying = null)
    {
        lock (_sync) {
            _events.RemoveAll(e => e.At < _time.GetUtcNow().AddDays(-7));
            _events.Add(NpepDiagnosticValues.Safe(new(Guid.NewGuid(), _time.GetUtcNow(), kind, endpoint, code, status, requestId, elapsedMs, retrying)));
            if (_events.Count > NpepDiagnosticValues.MaxEvents) _events.RemoveRange(0, _events.Count - NpepDiagnosticValues.MaxEvents);
            _version++;
        }
        _dirty.Writer.TryWrite(true);
    }
    public NpepConnectionHistory Snapshot()
    {
        lock (_sync) return new(_unavailable ? "Unavailable" : _version > _saved ? "Pending" : _events.Count == 0 ? "Empty" : "Stored",
            _events.Where(e => e.At >= _time.GetUtcNow().AddDays(-7)).ToArray());
    }
    private async Task WriteLoopAsync()
    {
        await foreach (var signal in _dirty.Reader.ReadAllAsync())
        {
            while (_dirty.Reader.TryRead(out _)) { }
            NpepConnectionEvent[] entries; long version;
            lock (_sync) { entries = _events.ToArray(); version = _version; }
            if (_directory is null) continue;
            string? temporary = null;
            try {
                Directory.CreateDirectory(_directory);
                var bytes = JsonSerializer.SerializeToUtf8Bytes(entries, Protocol.Json);
                if (bytes.Length > 32768) throw new IOException();
                temporary = Path.Combine(_directory, "connection-history." + Guid.NewGuid().ToString("N") + ".tmp");
                await File.WriteAllBytesAsync(temporary, bytes);
                File.Move(temporary, Path.Combine(_directory, "connection-history.json"), true);
                lock (_sync) { _saved = version; _unavailable = false; }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) { lock (_sync) _unavailable = true; }
            finally { if (temporary is not null) try { File.Delete(temporary); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { } }
        }
    }
    public async ValueTask DisposeAsync()
    {
        _dirty.Writer.TryComplete();
        // A broken profile disk must not prevent Host shutdown or an installer update.
        try { await _writer.WaitAsync(TimeSpan.FromSeconds(5)); } catch (TimeoutException) { }
    }
}

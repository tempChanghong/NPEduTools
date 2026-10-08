using System.Text.Json;
using System.Text.Json.Nodes;
using NPEduTools.Contracts;
using NPEduTools.Core;
using NPEduTools.Integrations.Npep;

namespace NPEduTools.Host;

/// <summary>Owns scheduled sessions only. Policy leases live in memory; user skips live on disk.</summary>
public sealed class NoiseScheduleService : INpepNoiseSchedules, IAsyncDisposable
{
    private readonly NoiseService _noise;
    private readonly Func<SchoolClockFrame> _clock;
    private readonly Func<bool> _exam;
    private readonly TimeProvider _time;
    private readonly Action<string?>? _bindReports;
    private readonly Func<bool>? _statisticsReady;
    private readonly long _origin;
    private readonly string _path;
    private readonly object _sync = new(), _journalLock = new();
    private readonly SchoolClockTracker _tracker = new();
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _loop;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private Journal _journal = new(null, [], [], null, null);
    private sealed record CommandReceipt(string CommandId, string Outcome);
    private sealed record Journal(string? Scope, List<NoiseWindowBlock> Blocks, List<CommandReceipt> Commands, Guid? OwnedId, SchoolNoiseWindow? OwnedWindow);
    private string? _scope, _version, _storeError;
    private bool _eligible;
    private double _confirmedAt = -1, _leaseMs;
    private EffectiveNoiseSchedule _policy = new("None", []);
    private Guid? _owned;
    private SchoolNoiseWindow? _ownedWindow;
    private string _owner = "None", _reason = "NOT_ELIGIBLE";
    private JsonObject? _receipt;
    public NoiseScheduleService(NoiseService noise, Func<SchoolClockFrame> clock, Func<bool> exam,
        string directory, TimeProvider? time = null, bool background = true, Action<string?>? bindReports = null, Func<bool>? statisticsReady = null)
    {
        _noise = noise; _clock = clock; _exam = exam; _time = time ?? TimeProvider.System;
        _origin = _time.GetTimestamp(); _path = Path.Combine(directory, "noise-schedule-state.json");
        _bindReports = bindReports;
        _statisticsReady = statisticsReady;
        try
        {
            if (File.Exists(_path))
            {
                if (new FileInfo(_path).Length > 65536) throw new InvalidDataException();
                _journal = JsonSerializer.Deserialize<Journal>(File.ReadAllText(_path), Json) ?? throw new InvalidDataException();
                if (_journal.Blocks is null || _journal.Commands is null || _journal.Blocks.Count > 128 || _journal.Commands.Count > 32 ||
                    _journal.Blocks.Any(b => b is null || b.Window is null || b.Kind is not ("Skipped" or "Failed") || b.Window.End <= b.Window.Start || b.Window.Start.Offset != TimeSpan.Zero || b.Window.End.Offset != TimeSpan.Zero))
                    throw new InvalidDataException();
                if (_journal.Commands.Any(c => c is null || !Guid.TryParse(c.CommandId, out _) || c.Outcome is not ("ACCEPTED" or "REJECTED"))) throw new InvalidDataException();
                // A hard Host restart never revives the capture or the online lease.
                _journal = _journal with { OwnedId = null, OwnedWindow = null };
            }
        }
        catch (Exception e) when (StorageError(e)) { _storeError = "SCHEDULE_STORE_UNAVAILABLE"; }
        _noise.ManualStopping = Skip;
        _loop = background ? Task.Run(LoopAsync) : Task.CompletedTask;
    }
    private static bool StorageError(Exception e) => e is IOException or UnauthorizedAccessException or JsonException or InvalidDataException;
    private double Elapsed => _time.GetElapsedTime(_origin).TotalMilliseconds;
    private static string MicrophoneKey(string? id) => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(id ?? "")));
    private void Save()
    {
        if (_storeError is not null) return; // Retain the original file for diagnosis; never silently reset it.
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            using (var file = new FileStream(_path + ".tmp", FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            { JsonSerializer.Serialize(file, _journal, Json); file.Flush(true); }
            File.Move(_path + ".tmp", _path, true);
        }
        catch (Exception e) when (StorageError(e)) { _storeError = "SCHEDULE_STORE_UNAVAILABLE"; }
    }
    private bool Skip(Guid id)
    {
        lock (_journalLock)
        {
            if (_journal.OwnedId != id || _journal.OwnedWindow is not { } w) return true;
            _journal.Blocks.Add(new(w, "Skipped")); Trim(); Save();
            return _storeError is null;
        }
    }
    private void Trim()
    {
        // Keep the most recent windows. Old windows cannot overlap today's schedule.
        while (_journal.Blocks.Count > 128) _journal.Blocks.RemoveAt(0);
        while (_journal.Commands.Count > 32) _journal.Commands.RemoveAt(0);
    }
    public void Bind(string? scope, bool eligible)
    {
        lock (_sync)
        {
            _eligible = eligible && scope is not null;
            if (!_eligible) _confirmedAt = -1;
            if (_scope != scope)
            {
                if (_owned is { } id) _noise.StopScheduled(id);
                _owned = null; _ownedWindow = null; _confirmedAt = -1; _version = null;
                _policy = new("None", []); _scope = scope;
                _bindReports?.Invoke(scope); // Fence the report outbox before a scheduled session can start.
                lock (_journalLock)
                {
                    if (_journal.Scope != scope) { _journal = new(scope, [], [], null, null); Save(); }
                }
            }
            if (!_eligible && _owned is { } current) _noise.StopScheduled(current);
            if (scope is null)
            {
                lock (_journalLock) { if (_journal.Scope is not null) { _journal = new(null, [], [], null, null); Save(); } }
            }
        }
    }
    public void Confirm(JsonObject response)
    {
        NpepNoiseScheduleProtocol.Validate("exchangeResponse", response);
        lock (_sync)
        {
            if (!_eligible || _scope is null || !response["confirmed"]!.GetValue<bool>()) return;
            var p = response["policy"]!.AsObject();
            var rules = p["rules"]!.Deserialize<List<SchoolNoiseRule>>(Json)!;
            string source = p["source"]!.GetValue<string>();
            if (SchoolNoiseSchedule.Validate(new(source is "None" or "Disabled" ? "Disabled" : "Override", rules)) is not null) throw new NpepException("INVALID_RESPONSE");
            _policy = new(source, rules);
            _version = p["version"]!.GetValue<string>();
            _confirmedAt = Elapsed; _leaseMs = response["leaseSeconds"]!.GetValue<int>() * 1000d;
            if (response["command"] is JsonObject command) Resume(command);
        }
    }
    private void Resume(JsonObject command)
    {
        string id = command["commandId"]!.GetValue<string>();
        lock (_journalLock)
        {
            var previous = _journal.Commands.FirstOrDefault(c => c.CommandId == id);
            if (previous is not null) { _receipt = JsonSerializer.SerializeToNode(previous, Json)!.AsObject(); return; }
            var reading = _tracker.Read(TimeSpan.FromMilliseconds(Elapsed));
            var window = reading.Now is { Year: >= 2000 and <= 9998 } now ? SchoolNoiseSchedule.Windows(_policy, now).Current : null;
            bool valid = _version == command["version"]!.GetValue<string>() && window is not null && reading.Fresh &&
                ParseWindow(command["window"]!.AsObject()) == window;
            // Persist the consumption and removal together before any new capture.
            if (valid)
            {
                _journal.Blocks.RemoveAll(b => SchoolNoiseSchedule.Overlaps(b.Window, window!));
                _tracker.ConfirmDate();
            }
            var receipt = new CommandReceipt(id, valid && _storeError is null ? "ACCEPTED" : "REJECTED");
            _journal.Commands.Add(receipt); Trim(); Save();
            _receipt = JsonSerializer.SerializeToNode(_storeError is null ? receipt : receipt with { Outcome = "REJECTED" }, Json)!.AsObject();
        }
    }
    public void Tick()
    {
        lock (_sync)
        {
            double elapsed = Elapsed;
            if (_policy.Source is "Grade" or "Class") _tracker.Accept(_clock(), TimeSpan.FromMilliseconds(elapsed), 0);
            var reading = _tracker.Read(TimeSpan.FromMilliseconds(elapsed));
            if (reading.Now is { Year: < 2000 or > 9998 })
            { _tracker.Unavailable("学校日期超出支持范围", TimeSpan.FromMilliseconds(elapsed)); reading = _tracker.Read(TimeSpan.FromMilliseconds(elapsed)); }
            var status = _noise.Snapshot();
            bool active = status.State is "Starting" or "Active" or "Stopping";
            if (_owned == status.SessionId && status.State == "Faulted" && _ownedWindow is { } failed)
            {
                lock (_journalLock) { _journal.Blocks.Add(new(failed, "Failed", MicrophoneKey(status.SelectedDeviceId))); Trim(); Save(); }
                _owned = null;
            }
            _owner = active ? status.SessionId == _owned ? "Schedule" : "Manual" : "None";
            List<NoiseWindowBlock> blocks;
            lock (_journalLock) blocks = [.. _journal.Blocks];
            bool lease = _confirmedAt >= 0 && elapsed >= _confirmedAt && elapsed - _confirmedAt < _leaseMs;
            bool statisticsReady = _statisticsReady?.Invoke() != false;
            var decision = NoiseScheduleEvaluator.Evaluate(new(_policy, reading, _eligible && _storeError is null && statisticsReady, lease,
                !string.IsNullOrWhiteSpace(status.SelectedDeviceId), _exam(), _owner, status.State == "Stopping",
                MicrophoneKey(status.SelectedDeviceId), blocks));
            _reason = _storeError ?? (!statisticsReady && _owner != "Manual" ? "STATISTICS_STORE_UNAVAILABLE" : decision.Reason);
            if (_reason == "WINDOW_ACTIVE" && status.State == "Starting") _reason = "CAPTURE_STARTING";
            if (decision.Action == "Keep" && _owned is { } continuing && _ownedWindow != decision.Window)
            {
                // A continuing capture adopts the newly applied window. STOP/failure records
                // and protection must describe that window, not the one used at startup.
                lock (_journalLock)
                {
                    _journal = _journal with { OwnedWindow = decision.Window }; Save();
                    if (_storeError is null) _ownedWindow = decision.Window;
                }
                if (_storeError is not null)
                { _reason = _storeError; _noise.StopScheduled(continuing); return; }
            }
            if (decision.Action == "Stop" && _owned is { } stopped) _noise.StopScheduled(stopped);
            if (decision.Action == "Start")
            {
                var id = Guid.NewGuid(); _ownedWindow = decision.Window;
                lock (_journalLock) { _journal = _journal with { OwnedId = id, OwnedWindow = decision.Window }; Save(); }
                if (_storeError is not null) return;
                var result = _noise.StartScheduled(id);
                if (result.Outcome == "Accepted") { _owned = id; _owner = "Schedule"; _reason = "CAPTURE_STARTING"; }
                else
                {
                    if (result.ErrorCode is "NoiseBusy" or "NoiseStateChanged") { _reason = "CAPTURE_BUSY"; return; }
                    _reason = "WINDOW_FAILED";
                    lock (_journalLock) { _journal.Blocks.Add(new(decision.Window!, "Failed", MicrophoneKey(status.SelectedDeviceId))); Trim(); Save(); }
                }
            }
        }
    }
    private static string Stamp(DateTimeOffset v) => v.ToString("yyyy-MM-dd'T'HH:mm:ss.fff", System.Globalization.CultureInfo.InvariantCulture);
    private static SchoolNoiseWindow ParseWindow(JsonObject w) => new(
        DateTimeOffset.Parse(w["start"]!.GetValue<string>(), System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal),
        DateTimeOffset.Parse(w["end"]!.GetValue<string>(), System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal));
    private static JsonObject? Window(SchoolNoiseWindow? w) => w is null ? null : new() { ["start"] = Stamp(w.Start), ["end"] = Stamp(w.End) };
    // Called independently of the scheduler lock; capture -> journal is the established lock order for STOP.
    internal JsonObject? ProtectionWindow(Guid? session)
    { lock (_journalLock) return session is not null && _journal.OwnedId == session ? Window(_journal.OwnedWindow) : null; }
    public JsonObject Observe()
    {
        lock (_sync)
        {
            var reading = _tracker.Read(TimeSpan.FromMilliseconds(Elapsed));
            var windows = reading.Now is { Year: >= 2000 and <= 9998 } now ? SchoolNoiseSchedule.Windows(_policy, now) : new SchoolNoiseWindows(null, null);
            return new() { ["capability"] = "noise.schedule", ["version"] = _version, ["source"] = _policy.Source,
                ["owner"] = _owner, ["reason"] = _reason, ["schoolNow"] = reading.Now is { Year: >= 2000 and <= 9998 } n ? Stamp(n) : null,
                ["clockReady"] = reading.CanStart, ["dateNeedsReview"] = reading.DateNeedsReview,
                ["window"] = Window(windows.Current), ["next"] = Window(windows.Next),
                ["sessionId"] = _owner == "Schedule" ? _owned?.ToString("D") : null,
                ["leaseRemainingSeconds"] = _confirmedAt < 0 ? 0 : Math.Max(0, (int)Math.Ceiling((_leaseMs - (Elapsed - _confirmedAt)) / 1000)),
                ["receipt"] = _receipt?.DeepClone() };
        }
    }
    private async Task LoopAsync()
    {
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                try { Tick(); }
                catch (Exception e) when (e is IOException or InvalidDataException or JsonException or NpepException) { _storeError = "SCHEDULE_STORE_UNAVAILABLE"; }
                await Task.Delay(TimeSpan.FromMilliseconds(500), _time, _stop.Token);
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
    }
    public async ValueTask DisposeAsync()
    {
        _stop.Cancel(); await _loop;
        lock (_sync) { _noise.ManualStopping = null; if (_owned is { } id) _noise.StopScheduled(id); }
        _stop.Dispose();
    }
}

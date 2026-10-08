using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using NPEduTools.Contracts;
using NPEduTools.Integrations.Npep;

namespace NPEduTools.Host;

/// <summary>Statistics-only outbox and command journal. Never stores PCM or browser credentials.</summary>
public sealed class NoiseTransport : INpepNoise, INpepNoiseManagement, INpepNoiseDisplay
{
    private readonly NoiseService _noise;
    private readonly string _path;
    private readonly object _sync = new();
    private JsonObject _store = new() { ["scope"] = null, ["reports"] = new JsonArray(), ["commands"] = new JsonArray(), ["checkpoint"] = null };
    private string? _error;
    private long _checkpointAt;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    internal Func<Guid?, JsonObject?>? ManagementWindow { get; set; }
    internal NoiseDisplayService? Display { get; set; }
    public void BindDisplay(string? scope) => Display?.BindDisplay(scope);
    public JsonObject? ObserveDisplay() => Display?.ObserveDisplay();
    public JsonObject? PendingDisplayReturn() => Display?.PendingDisplayReturn();
    public void ConfirmDisplay(JsonObject request, JsonObject reply, double elapsedSeconds) => Display?.ConfirmDisplay(request, reply, elapsedSeconds);
    public void DisplayFailed(string code) => Display?.DisplayFailed(code);
    public JsonObject ObserveProtection()
    {
        var (s, p) = _noise.ManagementSnapshot();
        return new() { ["instanceId"] = s.InstanceId.ToString("D"), ["revision"] = s.Revision,
            ["sessionId"] = s.SessionId?.ToString("D"), ["protected"] = p.Protected,
            ["window"] = p.Protected ? ManagementWindow?.Invoke(s.SessionId) : null };
    }
    public bool RequiresManagement(JsonObject c) => c["action"]?.GetValue<string>() == "STOP" && _noise.Protection().Protected;

    public NoiseTransport(NoiseService noise, string directory)
    {
        _noise = noise; _path = Path.Combine(directory, "noise-outbox.json");
        try
        {
            if (File.Exists(_path))
            {
                if (new FileInfo(_path).Length > 1048576) throw new InvalidDataException();
                _store = JsonNode.Parse(File.ReadAllText(_path)) as JsonObject ?? throw new InvalidDataException();
                if (_store["reports"] is not JsonArray || _store["commands"] is not JsonArray) throw new InvalidDataException();
                if (_store["scope"] is not null && (_store["scope"] is not JsonValue scope || !scope.TryGetValue<string>(out var scopeText) || scopeText.Length > 2048)) throw new InvalidDataException();
                if (_store["excludedSessionId"] is not null && (_store["excludedSessionId"] is not JsonValue excluded ||
                    !excluded.TryGetValue<string>(out var excludedText) || !Guid.TryParseExact(excludedText, "D", out var excludedId) || excludedId == Guid.Empty)) throw new InvalidDataException();
                if (((JsonArray)_store["reports"]!).Count > 200 || ((JsonArray)_store["commands"]!).Count > 64) throw new InvalidDataException();
                foreach (var command in (JsonArray)_store["commands"]!)
                {
                    if (command is not JsonObject c || c["commandId"] is not JsonValue id || !id.TryGetValue<string>(out var text) || !Guid.TryParse(text, out _) ||
                        c["acked"] is not JsonValue ack || !ack.TryGetValue<bool>(out _) || c["receipt"] is not JsonObject receipt) throw new InvalidDataException();
                    NpepNoiseProtocol.Validate("receipt", receipt);
                }
                foreach (var r in (JsonArray)_store["reports"]!)
                { if (r is not JsonObject report) throw new InvalidDataException(); NpepNoiseProtocol.Validate("report", report); }
                if (_store["checkpoint"] is JsonObject checkpoint)
                {
                    checkpoint["outcome"] = "Interrupted";
                    AddReport((JsonObject)checkpoint.DeepClone()); _store["checkpoint"] = null; Save();
                }
            }
        }
        catch (Exception e) when (StorageError(e)) { _error = "NOISE_STORE_UNAVAILABLE"; }
        noise.Completed += Complete;
    }
    private static bool StorageError(Exception e) => e is IOException or UnauthorizedAccessException or JsonException or InvalidDataException or NpepException;
    internal bool Available { get { lock (_sync) return _error is null; } }
    private void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            string temp = _path + ".tmp";
            using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            { JsonSerializer.Serialize(stream, _store, Json); stream.Flush(true); }
            File.Move(temp, _path, true);
        }
        catch (Exception e) when (StorageError(e)) { _error = "NOISE_STORE_UNAVAILABLE"; throw; }
    }
    public void Bind(string? scope)
    {
        lock (_sync)
        {
            if (_error is not null || _store["scope"]?.GetValue<string>() == scope) return;
            // Binding changes discard the old scope's statistics rather than upload them to another class.
            var current = _noise.Snapshot();
            bool owned = ((JsonArray)_store["commands"]!).Any(c => c?["commandId"]?.GetValue<string>() == current.SessionId?.ToString("D"));
            if (owned && current.State is "Starting" or "Active")
                _noise.RemoteCommand(Guid.NewGuid(), "STOP", current.InstanceId, current.Revision, current.SessionId, 60);
            _store = new() { ["scope"] = scope, ["reports"] = new JsonArray(), ["commands"] = new JsonArray(), ["checkpoint"] = null,
                ["excludedSessionId"] = current.SessionId?.ToString("D") };
            try { Save(); } catch (Exception e) when (StorageError(e)) { _error = "NOISE_STORE_UNAVAILABLE"; }
        }
    }
    public JsonObject Observe()
    {
        lock (_sync)
        {
            var s = _noise.Snapshot();
            if (_error is null && _store["scope"] is not null && !Excluded(s) && s.Summary is not null && s.State is "Active" or "Starting" or "Stopping" && Stopwatch.GetElapsedTime(_checkpointAt).TotalSeconds >= 30)
            {
                _store["checkpoint"] = Report(s, "Interrupted"); _checkpointAt = Stopwatch.GetTimestamp();
                try { Save(); } catch (Exception e) when (StorageError(e)) { _error = "NOISE_STORE_UNAVAILABLE"; }
            }
            var status = new JsonObject { ["instanceId"] = s.InstanceId.ToString("D"), ["revision"] = s.Revision,
                ["sessionId"] = s.SessionId?.ToString("D"), ["state"] = s.State, ["deviceName"] = Short(s.DeviceName),
                ["configured"] = !string.IsNullOrWhiteSpace(s.SelectedDeviceId) && _error is null,
                ["startedAt"] = s.StartedAt is { } started ? Stamp(started) : null, ["currentDbfs"] = s.CurrentDbfs, ["quality"] = s.Quality,
                ["summary"] = JsonSerializer.SerializeToNode(s.Summary, Json), ["algorithm"] = s.Algorithm, ["uploadError"] = _error };
            return status;
        }
    }
    private static string? Short(string? value) => value is null ? null : new string(value.Where(c => !char.IsControl(c)).Take(200).ToArray());
    private static string Stamp(DateTimeOffset value) => value.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", System.Globalization.CultureInfo.InvariantCulture);
    private static JsonObject Report(NoiseState s, string outcome) => new() {
        ["sessionId"] = s.SessionId?.ToString("D"), ["startedAt"] = Stamp(s.StartedAt!.Value),
        ["endedAt"] = Stamp(DateTimeOffset.UtcNow < s.StartedAt ? s.StartedAt.Value : DateTimeOffset.UtcNow),
        ["deviceName"] = Short(s.DeviceName), ["algorithm"] = s.Algorithm, ["summary"] = JsonSerializer.SerializeToNode(s.Summary, Json), ["outcome"] = outcome
    };
    private void AddReport(JsonObject report)
    {
        NpepNoiseProtocol.Validate("report", report);
        var reports = (JsonArray)_store["reports"]!;
        if (!reports.Any(r => r!["sessionId"]!.GetValue<string>() == report["sessionId"]!.GetValue<string>())) reports.Add(report);
        while (reports.Count > 200) reports.RemoveAt(0);
        for (int i = reports.Count - 1; i >= 0; i--)
            if (DateTimeOffset.UtcNow - DateTimeOffset.Parse(reports[i]!["endedAt"]!.GetValue<string>()) > TimeSpan.FromDays(14)) reports.RemoveAt(i);
    }
    private void Complete(NoiseState s)
    {
        lock (_sync)
        {
            if (_error is not null || _store["scope"] is null || s.Summary is null || Excluded(s)) return;
            try { AddReport(Report(s, s.State)); _store["checkpoint"] = null; Save(); }
            catch (Exception e) when (StorageError(e)) { _error = "NOISE_STORE_UNAVAILABLE"; }
        }
    }
    private bool Excluded(NoiseState s) => s.SessionId is not null && _store["excludedSessionId"]?.GetValue<string>() == s.SessionId.Value.ToString("D");
    public JsonArray Reports()
    {
        lock (_sync)
        {
            if (_error is not null) return new();
            var reports = (JsonArray)_store["reports"]!; bool changed = false;
            for (int i = reports.Count - 1; i >= 0; i--)
                if (DateTimeOffset.UtcNow - DateTimeOffset.Parse(reports[i]!["endedAt"]!.GetValue<string>()) > TimeSpan.FromDays(14)) { reports.RemoveAt(i); changed = true; }
            if (changed) Save();
            return new(reports.Take(4).Select(r => r!.DeepClone()).ToArray());
        }
    }
    public JsonArray Receipts() { lock (_sync) return _error is null ? new(((JsonArray)_store["commands"]!).Where(c => c!["acked"]?.GetValue<bool>() != true).Take(8).Select(c => c!["receipt"]!.DeepClone()).ToArray()) : new(); }
    public void Acknowledge(JsonObject response)
    {
        lock (_sync)
        {
            if (_error is not null) return;
            var reports = (JsonArray)_store["reports"]!;
            var ids = response["acceptedReports"]!.AsArray().Select(v => v!.GetValue<string>()).ToHashSet();
            var receipts = response["acceptedReceipts"]!.AsArray().Select(v => v!.GetValue<string>()).ToHashSet();
            bool changed = false;
            for (int i = reports.Count - 1; i >= 0; i--) if (ids.Contains(reports[i]!["sessionId"]!.GetValue<string>())) { reports.RemoveAt(i); changed = true; }
            foreach (var c in (JsonArray)_store["commands"]!) if (receipts.Contains(c!["commandId"]!.GetValue<string>()) && c["acked"]?.GetValue<bool>() != true) { c["acked"] = true; changed = true; }
            if (changed) Save();
        }
    }
    public void Execute(JsonObject command, Action authorize) => ExecuteCore(command, authorize, false);
    public void ExecuteManaged(JsonObject command, Action authorize) => ExecuteCore(command, authorize, true);
    private void ExecuteCore(JsonObject command, Action authorize, bool managed)
    {
        NpepNoiseProtocol.Validate("command", command);
        lock (_sync)
        {
            if (_error is not null) return;
            var commands = (JsonArray)_store["commands"]!;
            string id = command["commandId"]!.GetValue<string>();
            var old = commands.FirstOrDefault(c => c!["commandId"]!.GetValue<string>() == id);
            if (old is not null) { old["acked"] = false; Save(); return; }
            authorize();
            // Persist UNKNOWN before any effect. A crash cannot replay a start into a fresh session.
            var receipt = new JsonObject { ["commandId"] = id, ["outcome"] = "UNKNOWN", ["reason"] = "HOST_INTERRUPTED" };
            var entry = new JsonObject { ["commandId"] = id, ["receipt"] = receipt, ["acked"] = false };
            commands.Add(entry); while (commands.Count > 64) commands.RemoveAt(0); Save();
            authorize();
            var result = managed
                ? _noise.RemoteAuthorizedCommand(Guid.Parse(id), command["action"]!.GetValue<string>(), Guid.Parse(command["instanceId"]!.GetValue<string>()),
                    command["revision"]!.GetValue<long>(), command["sessionId"] is null ? null : Guid.Parse(command["sessionId"]!.GetValue<string>()), command["durationSeconds"]!.GetValue<int>())
                : _noise.RemoteCommand(Guid.Parse(id), command["action"]!.GetValue<string>(), Guid.Parse(command["instanceId"]!.GetValue<string>()),
                    command["revision"]!.GetValue<long>(), command["sessionId"] is null ? null : Guid.Parse(command["sessionId"]!.GetValue<string>()), command["durationSeconds"]!.GetValue<int>());
            receipt["outcome"] = result.Outcome == "Accepted" ? "ACCEPTED" : "REJECTED";
            receipt["reason"] = result.ErrorCode; Save();
        }
    }
}

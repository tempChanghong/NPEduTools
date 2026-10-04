using System.Text.Json;
using System.Text.Json.Nodes;
using NPEduTools.Contracts;
using NPEduTools.Integrations.Npep;

namespace NPEduTools.Host;

/// <summary>Display-only fallback. Does not start/stop capture, launch a browser, or hold a runtime mutation gate.</summary>
public sealed class NoiseDisplayService : INpepNoiseDisplay
{
    private readonly object _sync = new();
    private readonly Func<(NoiseState Noise, SchoolNoiseWindow? Window)> _sample;
    private readonly Func<bool> _exam;
    private readonly TimeProvider _time;
    private readonly string _path;
    private string? _scope, _key, _error, _returnKey;
    private long _started, _confirmed, _presenceUntil, _returnUntil;
    private long? _hiddenSince, _lastDisplayed;
    private bool _supported, _confirmedOnce;
    private int _minutes = 10;
    private string? _website;
    private ReturnLease? _lease;
    private readonly Queue<Guid> _returns = new();
    private sealed record ReturnLease(string Scope, SchoolNoiseWindow Window, Guid RequestId,
        DateTimeOffset StartedAt, DateTimeOffset ExpiresAt, int Minutes, bool Pending);
    public NoiseDisplayService(Func<(NoiseState Noise, SchoolNoiseWindow? Window)> sample, Func<bool> exam,
        string directory, TimeProvider? time = null)
    {
        _sample = sample; _exam = exam; _time = time ?? TimeProvider.System;
        _path = Path.Combine(directory, "noise-display-return.json");
        try
        {
            if (File.Exists(_path))
            {
                if (new FileInfo(_path).Length > 8192) throw new InvalidDataException();
                _lease = JsonSerializer.Deserialize<ReturnLease>(File.ReadAllText(_path), Protocol.Json);
                if (_lease is not null && (_lease.RequestId == Guid.Empty || string.IsNullOrEmpty(_lease.Scope) || _lease.Scope.Length > 2048 || _lease.Window is null || _lease.Window.End <= _lease.Window.Start ||
                    _lease.Minutes is < 1 or > 60 || _lease.ExpiresAt <= _lease.StartedAt ||
                    _lease.ExpiresAt - _lease.StartedAt > TimeSpan.FromMinutes(_lease.Minutes))) throw new InvalidDataException();
                // An uncertain/backwards wall clock must not renew a return on process restart.
                if (_lease is not null && _time.GetUtcNow() < _lease.StartedAt) _lease = null;
            }
        }
        catch (Exception e) when (Storage(e)) { _lease = null; _error = "DISPLAY_STORE_UNAVAILABLE"; }
    }
    private static bool Storage(Exception e) => e is IOException or UnauthorizedAccessException or JsonException or InvalidDataException;
    private long Now => _time.GetTimestamp();
    private long After(double seconds) => Now + (long)(seconds * _time.TimestampFrequency);
    private double Remaining(long until) => Math.Max(0, (until - Now) / (double)_time.TimestampFrequency);
    private void Save()
    {
        if (_error == "DISPLAY_STORE_UNAVAILABLE") return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            using (var f = new FileStream(_path + ".tmp", FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            { JsonSerializer.Serialize(f, _lease, Protocol.Json); f.Flush(true); }
            File.Move(_path + ".tmp", _path, true);
        }
        catch (Exception e) when (Storage(e)) { _error = "DISPLAY_STORE_UNAVAILABLE"; }
    }
    public void Bind(string? scope)
    {
        lock (_sync)
        {
            if (_scope == scope) return;
            _scope = scope; _key = _returnKey = null; _confirmedOnce = _supported = false; _presenceUntil = _returnUntil = 0;
            if (_lease is not null && _lease.Scope != scope) { _lease = null; Save(); }
        }
    }
    private (NoiseState Noise, SchoolNoiseWindow? Window) Current()
    {
        var s = _sample();
        if (_exam() || _scope is null || s.Noise.State is not ("Active" or "Starting") || s.Window is null || s.Noise.SessionId is null)
        {
            _key = null; _presenceUntil = 0;
            if (_exam()) { _returnUntil = 0; _returnKey = null; if (_lease is not null) { _lease = null; Save(); } }
            return (s.Noise, null);
        }
        // Session replacement inside the same school window cannot renew a return deadline.
        string key = $"{_scope}|{s.Window.Start:O}|{s.Window.End:O}";
        if (_key != key)
        {
            _key = key; _started = Now; _presenceUntil = 0; _website = null; _hiddenSince = _lastDisplayed = null;
            if (_returnKey != key) _returnUntil = 0;
            if (_lease is { } lease && lease.Scope == _scope && lease.Window == s.Window && _time.GetUtcNow() >= lease.StartedAt)
            { if (_returnKey != key) _returnUntil = After(Math.Clamp((lease.ExpiresAt - _time.GetUtcNow()).TotalSeconds, 0, lease.Minutes * 60)); }
            else if (_lease is not null) { _lease = null; Save(); }
            _returnKey = key;
        }
        return s;
    }
    // Applied only after strict 0.9 validation and current device-context fencing by NpepRuntime.
    public void Confirm(SchoolNoiseWindow window, Guid instance, Guid session, string? website, double ageSeconds,
        int minutes, double returnRemainingSeconds, Guid? returnId, int? leaseMinutes = null)
    {
        lock (_sync)
        {
            var s = Current();
            if (s.Window != window || s.Noise.InstanceId != instance || s.Noise.SessionId != session) return;
            _supported = _confirmedOnce = true; _confirmed = Now; _minutes = minutes;
            if (_error != "DISPLAY_STORE_UNAVAILABLE") _error = null;
            string? previousWebsite = _website; _website = website;
            _presenceUntil = website is "DISPLAY_VISIBLE" or "BLOCKED" ? After(Math.Max(0, 15 - ageSeconds)) : 0;
            if (website is "DISPLAY_VISIBLE" or "BLOCKED")
            { _hiddenSince = null; _lastDisplayed = Now - (long)(ageSeconds * _time.TimestampFrequency); }
            else if (website == "HIDDEN" && previousWebsite != "HIDDEN") _hiddenSince = Now;
            else _hiddenSince ??= _lastDisplayed ?? Now;
            if (returnId is { } id && returnRemainingSeconds > 0)
            {
                long until = After(returnRemainingSeconds);
                if (_returnUntil > Now) until = Math.Min(until, _returnUntil);
                _returnUntil = until;
                var start = _lease?.RequestId == id ? _lease.StartedAt : _time.GetUtcNow();
                _lease = new(_scope!, window, id, start, _time.GetUtcNow().AddSeconds(Remaining(until)), leaseMinutes ?? minutes, false); Save();
            }
            else if (_lease?.Pending != true) { if (_lease is not null) { _lease = null; Save(); } _returnUntil = 0; }
        }
    }
    public void Failed(string code)
    {
        lock (_sync)
        {
            if (_error != "DISPLAY_STORE_UNAVAILABLE") _error = code;
            if (code is "DISPLAY_UNSUPPORTED" or "BINDING_CHANGED" or "INSTANCE_MISMATCH" or "SESSION_SUPERSEDED")
            { _supported = false; _presenceUntil = 0; }
        }
    }
    public NoiseDisplayState Snapshot()
    {
        lock (_sync)
        {
            var s = Current();
            if (s.Window is null) return new("INACTIVE", "当前没有实际定时采集。", false, ErrorCode: _error);
            double remaining = Remaining(_returnUntil);
            string phase = remaining > 0 ? "RETURNING" : !_supported ? (_confirmedOnce ? "UNAVAILABLE" : "NEGOTIATING") :
                Remaining(_presenceUntil) > 0 ? (_website == "BLOCKED" ? "BLOCKED" : "WEB_VISIBLE") :
                _time.GetElapsedTime(_started).TotalSeconds < 20 ||
                _time.GetElapsedTime(_hiddenSince ?? _lastDisplayed ?? _started).TotalSeconds < 20 ? "WAITING" : "FALLBACK";
            string message = phase switch {
                "RETURNING" => $"暂时返回作业板，约 {Math.Ceiling(remaining / 60)} 分钟后恢复展示；监测继续。" + (_lease?.Pending == true ? "学校期限登记待恢复。" : ""),
                "WEB_VISIBLE" => "网页监测展示在线。", "BLOCKED" => "网页正在处理编辑或通知，暂缓备用展示。",
                "FALLBACK" => _time.GetElapsedTime(_confirmed).TotalSeconds > 15 ? "网页展示状态未确认，原生窗口补位；监测继续。" : "网页未展示监测，原生窗口补位。",
                "UNAVAILABLE" => "当前服务不支持或无法确认展示能力；监测状态另行核对。",
                _ => "正在核对网页展示状态；监测继续。" };
            return new(phase, message, phase == "FALLBACK", s.Noise.InstanceId, s.Noise.SessionId, s.Window,
                _minutes, remaining, _website, _error);
        }
    }
    public HostResponse Handle(HostRequest request)
    {
        lock (_sync)
        {
            var state = Snapshot();
            if (request.Capability == "noise.display.status") return new(1, request.RequestId, "Succeeded", null, state.Message, NoiseDisplay: state, Noise: _sample().Noise);
            if (request.NoiseDisplay is not { } command || command.InstanceId != state.InstanceId || command.SessionId != state.SessionId || state.Window is null)
                return new(1, request.RequestId, "Rejected", "DISPLAY_STATE_CHANGED", "定时会话已变化，请刷新。", NoiseDisplay: state);
            if (_error == "DISPLAY_STORE_UNAVAILABLE") return new(1, request.RequestId, "Rejected", _error, "无法保存返回期限，监测继续。", NoiseDisplay: state);
            if (state.ReturnRemainingSeconds <= 0 && _lease?.RequestId != request.RequestId && !_returns.Contains(request.RequestId))
            {
                var previous = _lease; long previousUntil = _returnUntil;
                var start = _time.GetUtcNow(); _returnUntil = After(_minutes * 60);
                _lease = new(_scope!, state.Window, request.RequestId, start, start.AddMinutes(_minutes), _minutes, true); Save();
                if (_error == "DISPLAY_STORE_UNAVAILABLE") { _lease = previous; _returnUntil = previousUntil; }
            }
            if (_error != "DISPLAY_STORE_UNAVAILABLE" && !_returns.Contains(request.RequestId))
            { _returns.Enqueue(request.RequestId); while (_returns.Count > 64) _returns.Dequeue(); }
            state = Snapshot();
            return new(1, request.RequestId, _error == "DISPLAY_STORE_UNAVAILABLE" ? "Rejected" : "Succeeded", _error,
                state.Message, NoiseDisplay: state);
        }
    }
    public JsonObject? PendingReturn()
    {
        lock (_sync)
        {
            var s = Current();
            if (s.Window is null || _lease is not { Pending: true } l) return null;
            return new() { ["requestId"] = l.RequestId.ToString("D"),
                ["offlineStartedAt"] = l.StartedAt.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'"), ["returnMinutes"] = l.Minutes };
        }
    }
    public void BindDisplay(string? scope) => Bind(scope);
    public JsonObject? ObserveDisplay()
    {
        lock (_sync)
        {
            var s = Current();
            if (s.Window is null) return null;
            return new() { ["instanceId"] = s.Noise.InstanceId.ToString("D"), ["revision"] = s.Noise.Revision,
                ["captureSessionId"] = s.Noise.SessionId!.Value.ToString("D"), ["window"] = new JsonObject {
                    ["start"] = s.Window.Start.ToString("yyyy-MM-dd'T'HH:mm:ss.fff"), ["end"] = s.Window.End.ToString("yyyy-MM-dd'T'HH:mm:ss.fff") } };
        }
    }
    public JsonObject? PendingDisplayReturn() => PendingReturn();
    public void DisplayFailed(string code) => Failed(code);
    public void ConfirmDisplay(JsonObject request, JsonObject reply, double elapsedSeconds)
    {
        NpepNoiseDisplayProtocol.ValidateReply(reply, request);
        var w = request["window"]!.AsObject();
        var window = new SchoolNoiseWindow(DateTimeOffset.Parse(w["start"]!.GetValue<string>() + "Z"), DateTimeOffset.Parse(w["end"]!.GetValue<string>() + "Z"));
        var presence = reply["presence"]!.AsObject(); var r = reply["activeReturn"] as JsonObject;
        double remaining = r is null ? 0 : Math.Min(r["remainingSeconds"]!.GetValue<int>(),
            (DateTimeOffset.Parse(r["expiresAt"]!.GetValue<string>()) - DateTimeOffset.Parse(reply["serverNow"]!.GetValue<string>())).TotalSeconds);
        lock (_sync)
        {
            var current = Current();
            if (current.Window != window || current.Noise.InstanceId.ToString("D") != request["instanceId"]!.GetValue<string>() ||
                current.Noise.SessionId?.ToString("D") != request["captureSessionId"]!.GetValue<string>()) return;
            Confirm(window, current.Noise.InstanceId, current.Noise.SessionId.Value,
                presence["state"]!.GetValue<string>(), (presence["ageMs"]?.GetValue<int>() ?? 600000) / 1000d + elapsedSeconds,
                reply["returnMinutes"]!.GetValue<int>(), Math.Max(0, remaining - elapsedSeconds), r is null ? null : Guid.Parse(r["requestId"]!.GetValue<string>()),
                r?["returnMinutes"]?.GetValue<int>());
            // A successful idempotent return may already be expired. Consume the intent; never recreate it.
            if (_lease is { Pending: true } l && request["offlineStartedAt"] is not null && r is not null)
            {
                _lease = remaining <= elapsedSeconds ? null : l with { Pending = false,
                    ExpiresAt = _time.GetUtcNow().AddSeconds(Math.Min(Remaining(_returnUntil), Math.Max(0, remaining - elapsedSeconds))) };
                if (_lease is null) _returnUntil = 0;
                if (_lease is not null && _lease.ExpiresAt <= _lease.StartedAt) _lease = null;
                Save();
            }
        }
    }
}

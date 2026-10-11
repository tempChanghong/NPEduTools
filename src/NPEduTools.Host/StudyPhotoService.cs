using System.Security.Cryptography;
using System.Text.Json.Nodes;
using NPEduTools.Contracts;
using NPEduTools.Integrations.Npep;
namespace NPEduTools.Host;
public sealed class StudyPhotoService : INpepStudyPhotos, IAsyncDisposable
{
    private readonly object _sync = new();
    private readonly Func<(string? Scope, bool Selected, StudyHostState State)> _sample;
    private readonly NoiseService _noise;
    private readonly StudyDeviceService _devices;
    private readonly Func<string, CancellationToken, Task<StudyCameraFrame>> _capture;
    private readonly StudyPhotoJournal _journal;
    private readonly TimeProvider _time;
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _loop;
    private string? _scope, _error, _lastCaptured, _lastStored;
    private JsonObject? _lease;
    private long _leaseAt;
    private double _leaseSeconds;
    private DateTimeOffset? _serverStart;
    private CancellationTokenSource? _shotStop;
    private Task _shot = Task.CompletedTask;
    private string? _shotKey;
    private readonly Dictionary<Guid,(string State,string? Reason)> _exitOutcomes=new();
    internal (string State,string? Reason) ExitOutcome(Guid id)
    { lock(_sync)return _exitOutcomes.TryGetValue(id,out var value)?value:("Unavailable",null); }
    private void ExitResult(Guid id,string state,string? reason=null)
    { if(!_exitOutcomes.ContainsKey(id) && _exitOutcomes.Count>=16)_exitOutcomes.Remove(_exitOutcomes.Keys.First());_exitOutcomes[id]=(state,reason); }
    private sealed record Context(string Scope, StudyHostState State, StudyNoise Policy, NoiseState Noise, string Camera)
    {
        public string Key => $"{Scope}|{State.Version}|{State.Execution!.SessionId}|{Camera}|{Noise.DeviceId}|{(Policy.Estimated is null ? "" : Noise.Estimate?.ProfileId + "/" + Noise.Estimate?.State)}";
        public double? Level => Policy.Estimated is null ? Noise.CurrentDbfs : Noise.Estimate is { State: "Active" } e ? e.CurrentDbA : null;
    }
    public StudyPhotoService(Func<(string? Scope, bool Selected, StudyHostState State)> sample, NoiseService noise,
        StudyDeviceService devices, Func<string, CancellationToken, Task<StudyCameraFrame>> capture,
        StudyPhotoJournal journal, TimeProvider? time = null, bool background = true)
    {
        _sample = sample; _noise = noise; _devices = devices; _capture = capture; _journal = journal; _time = time ?? TimeProvider.System;
        _loop = background ? Task.Run(async () => {
            try { while (!_stop.IsCancellationRequested) {
                try { Tick(); } catch (Exception e) when (e is IOException or UnauthorizedAccessException or NpepException or InvalidDataException)
                { lock (_sync) { _error = "PHOTO_STATE_UNAVAILABLE"; _shotStop?.Cancel(); } }
                await Task.Delay(TimeSpan.FromMilliseconds(200), _time, _stop.Token);
            } }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
        }) : Task.CompletedTask;
    }
    public void BindPhotos(string? scope)
    {
        lock (_sync)
        {
            if (_scope == scope) return;
            _shotStop?.Cancel(); _scope = scope; _lease = null; _serverStart = null;
            // Shutdown keeps the encrypted queue. Explicit revocation clears it via PhotoFailed.
        }
    }
    private Context? Read()
    {
        var sample = _sample(); var s = sample.State; var camera = _devices.Snapshot();
        if (_scope is null || sample.Scope != _scope || s.PolicyState != "APPLIED" || !s.ClockReady || s.Phase != "Active"
            || s.Version is null || s.Window is null || s.SchoolNow is null || s.Settings is not { Photos.Enabled: true, Noise.Enabled: true }
            || s.Execution is not { Owner: "Schedule", State: "Active", SignalFresh: true, SessionId: not null }
            || camera.CameraId is null || camera.CameraAvailable == false || camera.Error is not null) return null;
        var policy = _noise.ResolveCalibration(_scope, s.Version, s.Settings.Noise); var noise = _noise.Snapshot();
        if (policy.High is null || noise.SessionId != s.Execution.SessionId || noise.DeviceId is null) return null;
        return new(_scope, s, policy, noise, camera.CameraId);
    }
    private double Remaining => _lease is null ? 0 : Math.Max(0, _leaseSeconds - _time.GetElapsedTime(_leaseAt).TotalSeconds);
    private DateTimeOffset ServerNow => _serverStart!.Value + _time.GetElapsedTime(_leaseAt);
    public JsonObject? PhotoLeaseRequest()
    {
        lock (_sync) { var c = Read(); return c is null || Remaining > 45 ? null : new JsonObject { ["version"] = c.State.Version }; }
    }
    public void ConfirmPhotoLease(JsonObject response, TimeSpan roundTrip)
    {
        NpepPhotoProtocol.Validate("lease", response);
        lock (_sync)
        {
            var c = Read();
            if (c is null || response.Text("version") != c.State.Version || Guid.Parse(response.Text("sessionId")) != c.State.Execution!.SessionId)
                throw new NpepException("PHOTO_SESSION_CHANGED");
            if (roundTrip < TimeSpan.Zero || roundTrip.TotalSeconds >= 60) throw new NpepException("PHOTO_LEASE_EXPIRED");
            _lease = response.Copy(); _leaseAt = _time.GetTimestamp(); _leaseSeconds = 60 - roundTrip.TotalSeconds;
            _serverStart = DateTimeOffset.Parse(response.Text("createdAt")); _error = null;
        }
    }
    private bool Granted(Context c) => Remaining > 0 && _lease?.Text("version") == c.State.Version
        && _lease?.Text("sessionId") == c.State.Execution!.SessionId!.Value.ToString();
    public void Tick()
    {
        lock (_sync)
        {
            var c = Read();
            if (c is null || !Granted(c)) { _shotStop?.Cancel(); return; }
            if (_shotKey is not null && c.Key != _shotKey) _shotStop?.Cancel();
            // Qualification is independent of WPF presence and the discipline sound cooldown.
            if (_noise.StudyThresholds(c.State.Execution!.SessionId, c.Policy).Qualified && c.Level >= c.Policy.High) Start(c, "Noise", Guid.NewGuid());
        }
    }
    public HostResponse ExitIntent(HostRequest request)
    {
        lock (_sync)
        {
            if(ExitOutcome(request.RequestId).State is "Capturing" or "Captured" or "Stored")
                return new(1,request.RequestId,"Succeeded",null,"本次退出意图已处理，未重复拍照。",StudyPhotos:Snapshot());
            var c = Read();
            bool accepted = c is not null && Granted(c) && c.State.Settings!.Photos.CaptureOnExit && Start(c, "ExitAttempt", request.RequestId);
            if(!accepted)ExitResult(request.RequestId,"Suppressed");
            return new(1, request.RequestId, accepted ? "Succeeded" : "Suppressed", null,
                accepted ? "退出意图已受理；尚不代表照片已拍摄或上传。" : "本次未触发照片；退出仍需管理验证。", StudyPhotos: Snapshot());
        }
    }
    private bool Start(Context c, string reason, Guid eventId)
    {
        if (!_shot.IsCompleted || _journal.Error is not null || !_journal.Claim(ServerNow, c.State.Settings!.Photos.CooldownSeconds)) return false;
        _shotStop?.Dispose(); _shotStop = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
        var token = _shotStop.Token; _shotKey = c.Key; var lease = _lease!.Copy();
        if(reason=="ExitAttempt")ExitResult(eventId,"Capturing");
        var anchor = ServerNow; var started = _time.GetTimestamp(); var remaining = Remaining;
        _shot = Task.Run(async () => {
            try
            {
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token); deadline.CancelAfter(TimeSpan.FromSeconds(5));
                var frame = await _capture(c.Camera, deadline.Token).WaitAsync(deadline.Token);
                lock (_sync)
                {
                    var current = Read();
                    var elapsed = _time.GetElapsedTime(started);
                    if (token.IsCancellationRequested || current?.Key != c.Key || !Granted(current) || elapsed.TotalSeconds >= remaining) {if(reason=="ExitAttempt")ExitResult(eventId,"Failed","PHOTO_CAPTURE_INTERRUPTED");return;}
                    var at = (anchor + elapsed).ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'");
                    var metadata = new JsonObject { ["eventId"] = eventId.ToString(), ["leaseId"] = lease.Text("leaseId"),
                        ["sessionId"] = current.State.Execution!.SessionId!.Value.ToString(), ["version"] = current.State.Version,
                        ["capturedAt"] = at, ["schoolNow"] = current.State.SchoolNow!.Value.DateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff"),
                        ["reason"] = reason, ["unit"] = current.Policy.Estimated is null ? "Dbfs" : "EstimatedDbA",
                        ["level"] = c.Level,
                        ["high"] = current.Policy.High, ["continuousSeconds"] = current.Policy.HighContinuousSeconds,
                        ["calibrationId"] = c.Policy.Estimated is null || c.Level is null ? null : c.Noise.Estimate!.ProfileId,
                        ["hash"] = Convert.ToHexStringLower(SHA256.HashData(frame.Bytes)), ["width"] = frame.Width,
                        ["height"] = frame.Height, ["byteCount"] = frame.Bytes.Length };
                    _journal.Save(new(c.Scope, c.Camera, current.Noise.DeviceId!, metadata, frame.Bytes)); _lastCaptured = at; _error = null;
                    if(reason=="ExitAttempt")ExitResult(eventId,"Captured");
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException or OperationCanceledException or TimeoutException or NpepException or System.ComponentModel.Win32Exception)
            { lock (_sync) {_error = e is OperationCanceledException ? "PHOTO_CAPTURE_INTERRUPTED" : "PHOTO_CAPTURE_FAILED";if(reason=="ExitAttempt")ExitResult(eventId,"Failed",_error);} }
        }); return true;
    }
    public async Task FlushAsync(CancellationToken token) { Task pending; lock (_sync) pending = _shot; await pending.WaitAsync(TimeSpan.FromSeconds(5), token); }
    public StudyPhotoPacket? PendingPhoto()
    {
        lock (_sync)
        {
            var camera = _devices.Snapshot().CameraId; var microphone = _noise.Snapshot().SelectedDeviceId;
            if (_scope is null || camera is null || microphone is null) return null;
            var packet = _journal.Next(_scope, camera, microphone, _serverStart is null ? null : ServerNow);
            return packet;
        }
    }
    public void PhotoStored(Guid eventId)
    {
        lock (_sync) { _journal.Remove(eventId); _lastStored = _time.GetUtcNow().ToString("O"); _error = null;if(_exitOutcomes.ContainsKey(eventId))ExitResult(eventId,"Stored"); }
    }
    public void PhotoFailed(string code, Guid? eventId = null)
    {
        lock (_sync)
        {
            _error = code;
            if (code is "PHOTOS_DISABLED" or "AUTH_INVALID" or "BINDING_CHANGED" or "STUDY_DISABLED" or "PHOTO_NOT_ELIGIBLE")
            {
                _lease = null; _shotStop?.Cancel();
                foreach(var pendingId in _exitOutcomes.Where(p=>p.Value.State is "Captured" or "Capturing").Select(p=>p.Key).ToArray())ExitResult(pendingId,"Failed",code);
                try { _journal.Clear(); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { _error = "PHOTO_QUEUE_UNAVAILABLE"; }
            }
            if (eventId is { } id && code is ("PHOTO_TIME_INVALID" or "PHOTO_DELETED" or "PHOTO_CONTENT_MISMATCH" or "PHOTO_OUTSIDE_WINDOW" or "PHOTO_THRESHOLD_CHANGED" or "PHOTO_SESSION_CHANGED" or "PHOTO_LEASE_INVALID" or "PHOTO_EXPIRED" or "PHOTO_THRESHOLD_NOT_REACHED" or "PHOTO_EXIT_DISABLED" or "PHOTO_COOLDOWN" or "INVALID_PHOTO" or "INVALID_REQUEST"))
            {
                try { _journal.Remove(id);if(_exitOutcomes.ContainsKey(id))ExitResult(id,"Failed",code); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { _error = "PHOTO_QUEUE_UNAVAILABLE"; }
            }
        }
    }
    public StudyPhotoState Snapshot()
    {
        lock (_sync) { var c = Read(); return new(!_shot.IsCompleted ? "Capturing" : _error is not null || _journal.Error is not null ? "Faulted"
            : c is null ? "NotRequired" : Granted(c) ? "Ready" : "WaitingPermission", _journal.Error ?? _error, _journal.Pending, _lastCaptured, _lastStored); }
    }
    public async ValueTask DisposeAsync()
    {
        _stop.Cancel(); _shotStop?.Cancel(); await _loop;
        try { await _shot.WaitAsync(TimeSpan.FromSeconds(6)); } catch (TimeoutException) { }
        _shotStop?.Dispose(); _stop.Dispose();
    }
}

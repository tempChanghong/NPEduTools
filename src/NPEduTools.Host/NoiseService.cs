using NPEduTools.Contracts;
using NPEduTools.Core;

namespace NPEduTools.Host;

public interface INoiseCapture : IDisposable
{
    string DeviceName { get; }
    event Action<NoiseFrame>? Frame;
    event Action<string>? Failed;
    void Start();
}

/// <summary>One explicitly started session. No raw audio, disk storage, cloud traffic or recording dependency.</summary>
public sealed class NoiseService(Func<string, INoiseCapture> create, Func<IReadOnlyList<NoiseDevice>> devices,
    TimeProvider? time = null, string? directory = null, NoiseManagementStore? management = null) : IAsyncDisposable
{
    private readonly object _sync = new();
    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly Guid _instance = Guid.NewGuid();
    private long _revision;
    private bool _disposed, _cleanupFailed;
    private Session? _session;
    private string _state = "Idle", _message = "选择麦克风后开始监测。";
    private string? _selected = LoadSelection(directory);
    public event Action<NoiseState>? Completed;
    // Called before an accepted user STOP, under the capture lock. The callback only
    // journals the intent; it must never call back into the scheduler or capture.
    internal Func<Guid, bool>? ManualStopping { get; set; }
    private (Guid Request, Guid? Ticket, long At)? _exitReservation;
    private bool _shuttingDown;
    private bool ScheduledLive => management is not null && _state is "Starting" or "Active" or "Stopping" && _session is { Scheduled: true, Work.IsCompleted: false };
    private bool ExitPending => _exitReservation is { } r && _time.GetElapsedTime(r.At).TotalSeconds < 60;

    public NoiseProtectionState Protection()
    {
        lock (_sync) return new(ScheduledLive, management?.Configured == true, _instance, _session?.Id, management?.Error);
    }
    internal (NoiseState Noise, NoiseProtectionState Protection) ManagementSnapshot()
    { lock (_sync) return (Snapshot(), Protection()); }
    private HostResponse ManagementReply(HostRequest r, string? error = null, Guid? ticket = null) =>
        new(Protocol.Version, r.RequestId, error is null ? "Succeeded" : "Rejected", error, error switch
        {
            "MANAGEMENT_NOT_CONFIGURED" => "请先在无定时监测时设置本机管理口令；本次可由大屏网页验证学校 PIN 后结束监测。",
            "MANAGEMENT_SECRET_INVALID" => "管理口令不正确。",
            "MANAGEMENT_RATE_LIMITED" => "尝试过于频繁，请一分钟后重试。",
            "MANAGEMENT_STORE_UNAVAILABLE" => "管理口令文件不可用，原文件已保留；请联系管理员。",
            "MANAGEMENT_REQUIRED" => "学校定时监测受到保护；停止或退出需管理验证，关闭窗口不会停止采集。",
            "SCHEDULE_STORE_UNAVAILABLE" => "未能保存本时段跳过记录，未受理正常停止；请检查存储状态。",
            "NoiseStateChanged" => "当前监测会话已变化，请重新核实后验证。",
            null => "管理验证已完成。",
            _ => "管理验证未通过。"
        }, Noise: Snapshot(), NoiseProtection: Protection() with { Ticket = ticket });

    public HostResponse ManagementHandle(HostRequest r)
    {
        lock (_sync)
        {
            if (Protocol.Validate(r) is { } invalid) return ManagementReply(r, invalid);
            if (r.Capability == "noise.management.status") return ManagementReply(r, management?.Error);
            if (r.Capability == "noise.management.prepare-exit")
            {
                if (ExitPending && _exitReservation!.Value.Request == r.RequestId && _exitReservation.Value.Ticket == r.NoiseAuthorization)
                    return ManagementReply(r);
                if (AuthorizeInterruption(r) is { } denied) return denied;
                _exitReservation = (r.RequestId, r.NoiseAuthorization, _time.GetTimestamp());
                return ManagementReply(r);
            }
            if (management is null) return ManagementReply(r, "MANAGEMENT_UNSUPPORTED");
            var c = r.NoiseManagement!;
            if (c.Action == "configure") return ManagementReply(r, management.Configure(c.Secret, c.NewSecret!, ScheduledLive));
            if (!ScheduledLive || _session is null) return ManagementReply(r, "NoiseStateChanged");
            var grant = management.Issue(c, _instance, _session.Id);
            return ManagementReply(r, grant.Error, grant.Ticket);
        }
    }

    /// <summary>Checked under the same lock as scheduled start. UI claims cannot bypass this boundary.</summary>
    public HostResponse? AuthorizeInterruption(HostRequest r)
    {
        lock (_sync)
        {
            if (!Protocol.NoiseInterruption(r)) return null;
            if (r.Capability == "host.stop" && ExitPending)
            {
                if (_exitReservation!.Value.Request != r.RequestId || _exitReservation.Value.Ticket != r.NoiseAuthorization)
                    return ManagementReply(r, "MANAGEMENT_REQUIRED");
                _shuttingDown = true; return null;
            }
            if (ScheduledLive && _session is { } s)
            {
                if (management!.Consume(r.NoiseAuthorization, _instance, s.Id, Protocol.NoisePurpose(r), r.RequestId) != true)
                    return ManagementReply(r, "MANAGEMENT_REQUIRED");
                if (ManualStopping?.Invoke(s.Id) == false) return ManagementReply(r, "SCHEDULE_STORE_UNAVAILABLE");
                _revision++; _state = "Stopping"; _message = "管理授权已确认，正在停止本时段监测…"; s.Stop.Cancel();
            }
            if (r.Capability == "host.stop") _shuttingDown = true;
            return null;
        }
    }
    internal void AbortShutdown() { lock (_sync) _shuttingDown = false; }
    private static string? LoadSelection(string? path)
    {
        try
        {
            if (path is null) return null;
            string file = Path.Combine(path, "noise-microphone.json");
            // A valid 2048-character ID fits even when JSON escapes every character.
            // Leave invalid files untouched; status must remain readable so the user can select again.
            if (new FileInfo(file).Length > 16 * 1024) return null;
            var id = System.Text.Json.JsonSerializer.Deserialize<string>(File.ReadAllText(file));
            return NoiseContract.ValidDeviceId(id) ? id : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Text.Json.JsonException) { return null; }
    }
    private void Select(string id)
    {
        if (directory is not null)
        {
            Directory.CreateDirectory(directory);
            string file = Path.Combine(directory, "noise-microphone.json");
            File.WriteAllText(file + ".tmp", System.Text.Json.JsonSerializer.Serialize(id));
            File.Move(file + ".tmp", file, true);
        }
        _selected = id;
    }

    private sealed class Session(string device, long timestamp, DateTimeOffset started)
    {
        public string DeviceId { get; } = device;
        public bool Scheduled;
        public Guid Id = Guid.NewGuid();
        public double LimitSeconds = 10800;
        public string? DeviceName;
        public long Started { get; } = timestamp;
        public DateTimeOffset StartedAt { get; } = started;
        public long LastData = timestamp;
        public long Frames;
        public double Sampled, Energy, Peak, Clipped, LastPoint = -1;
        public double? Current, FinishedSeconds;
        public string Quality = "Waiting", Failure = "";
        public Queue<NoisePoint> Trend { get; } = new();
        public CancellationTokenSource Stop { get; } = new();
        public Task Work = Task.CompletedTask;
        public Task Watch = Task.CompletedTask;
    }

    public HostResponse Handle(HostRequest request) => Handle(request, null, 10800, false);
    private HostResponse Handle(HostRequest request, Guid? sessionId, double limit, bool trustedStop)
    {
        if (request.Capability == "noise.devices")
        {
            try { return Reply(request, "Succeeded", null, "麦克风列表已更新。", devices().Take(128).ToArray()); }
            catch (Exception error) when (error is not OutOfMemoryException)
            { return Reply(request, "Rejected", "NoiseDevicesUnavailable", "无法枚举麦克风，请检查 Windows 音频服务。", []); }
        }
        lock (_sync)
        {
            if (request.Capability == "noise.status") return Reply(request, "Succeeded", null, _message);
            if (request.Noise is not { } cmd || !NoiseContract.Valid(cmd)) return Reply(request, "Rejected", "InvalidNoiseCommand", "监测请求无效。");
            if (_disposed) return Reply(request, "Rejected", "NoiseStopping", "后台正在退出。");
            if (cmd.InstanceId != _instance || cmd.Revision != _revision)
                return Reply(request, "Rejected", "NoiseStateChanged", "监测状态已变化，请核对当前状态后重试。");
            if (cmd.Action == "stop")
            {
                if (_session is { } current && !current.Work.IsCompleted)
                {
                    if (ScheduledLive && !trustedStop && management!.Consume(request.NoiseAuthorization, _instance, current.Id, "stop", request.RequestId) != true)
                        return ManagementReply(request, "MANAGEMENT_REQUIRED");
                    if (ManualStopping?.Invoke(current.Id) == false) return ManagementReply(request, "SCHEDULE_STORE_UNAVAILABLE");
                    _revision++; _state = "Stopping"; _message = "正在停止并释放麦克风…";
                    current.Stop.Cancel();
                }
                return Reply(request, "Accepted", null, _message);
            }
            if (_cleanupFailed)
                return Reply(request, "Rejected", "NoiseCleanupRequired", "上次麦克风未能确认释放，请重启后台后再试。");
            if (_session is { Work.IsCompleted: false })
                return Reply(request, "Rejected", "NoiseBusy", "已有监测或麦克风尚未释放，请先停止并等待完成。");
            try { Select(cmd.DeviceId!); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            { return Reply(request, "Rejected", "NoiseConfigurationUnavailable", "无法保存麦克风配置。"); }
            if (cmd.Action == "select") { _revision++; return Reply(request, "Succeeded", null, "已保存麦克风；网页可使用该设备开始监测。"); }
            _session?.Stop.Dispose();
            var session = new Session(cmd.DeviceId!, _time.GetTimestamp(), _time.GetUtcNow()) { Id = sessionId ?? Guid.NewGuid(), LimitSeconds = limit };
            _session = session; _revision++; _state = "Starting"; _message = "正在打开选中的麦克风…";
            session.Work = Task.Run(() => RunAsync(session));
            session.Watch = Task.Run(() => WatchAsync(session));
            return Reply(request, "Accepted", null, _message);
        }
    }

    public HostResponse RemoteCommand(Guid commandId, string action, Guid instanceId, long revision, Guid? sessionId, int duration)
        => RemoteCommandCore(commandId, action, instanceId, revision, sessionId, duration, false);
    internal HostResponse RemoteAuthorizedCommand(Guid commandId, string action, Guid instanceId, long revision, Guid? sessionId, int duration)
        => RemoteCommandCore(commandId, action, instanceId, revision, sessionId, duration, true);
    private HostResponse RemoteCommandCore(Guid commandId, string action, Guid instanceId, long revision, Guid? sessionId, int duration, bool trustedStop)
    {
        lock (_sync)
        {
            var request = new HostRequest(Protocol.Version, commandId, "noise.command");
            if (action == "STOP" && sessionId != _session?.Id)
                return Reply(request, "Rejected", "NoiseStateChanged", "监测会话已变化。");
            if (action == "START" && string.IsNullOrWhiteSpace(_selected))
                return Reply(request, "Rejected", "MicrophoneNotConfigured", "请先在本机保存麦克风。");
            return Handle(request with { Noise = new(action == "START" ? "start" : "stop", instanceId, revision, action == "START" ? _selected : null) }, commandId, Math.Clamp(duration, 60, 10800), trustedStop);
        }
    }

    internal HostResponse StartScheduled(Guid id)
    {
        lock (_sync)
        {
            if (_shuttingDown || ExitPending) return new(Protocol.Version, id, "Rejected", "NoiseBusy", "后台维护退出中，暂不启动定时监测。");
            var s = Snapshot();
            var result = Handle(new(Protocol.Version, id, "noise.command", Noise:
                new("start", s.InstanceId, s.Revision, _selected)), id, 10800, false);
            if (result.Outcome == "Accepted" && _session is { } current) current.Scheduled = true;
            return result;
        }
    }
    internal void StopScheduled(Guid id)
    {
        lock (_sync)
        {
            if (_session is not { } s || s.Id != id || s.Work.IsCompleted) return;
            _revision++; _state = "Stopping"; _message = "自动监测暂停，正在释放麦克风…";
            s.Stop.Cancel(); // An automatic stop must not become a user skip.
        }
    }

    private HostResponse Reply(HostRequest request, string outcome, string? error, string message, IReadOnlyList<NoiseDevice>? choices = null)
        => new(Protocol.Version, request.RequestId, outcome, error, message, Noise: Snapshot(), NoiseDevices: choices);

    public NoiseState Snapshot()
    {
        lock (_sync)
        {
            var s = _session;
            double elapsed = s is null ? 0 : s.FinishedSeconds ?? _time.GetElapsedTime(s.Started).TotalSeconds;
            bool stale = s is not null && _state == "Active" && _time.GetElapsedTime(s.LastData).TotalSeconds > 1;
            var summary = s is null ? null : new NoiseSummary(elapsed, s.Sampled,
                elapsed > 0 ? Math.Clamp(s.Sampled / elapsed, 0, 1) : 0,
                s.Sampled > 0 ? NoiseMeter.Dbfs(Math.Sqrt(s.Energy / s.Sampled)) : null,
                NoiseMeter.Dbfs(s.Peak), s.Sampled > 0 ? 100 * s.Clipped / s.Sampled : 0, s.Frames);
            return new(_instance, _revision, _state, _message, s?.DeviceId, s?.DeviceName, s?.StartedAt,
                _state == "Active" && !stale ? s?.Current : null,
                stale ? "NoData" : s?.Quality ?? "Waiting", summary, s?.Trend.ToArray() ?? [], SessionId: s?.Id, SelectedDeviceId: _selected);
        }
    }

    private void Receive(Session s, NoiseFrame frame)
    {
        lock (_sync)
        {
            if (_session != s || s.Stop.IsCancellationRequested) return;
            s.LastData = _time.GetTimestamp();
            bool invalid = frame.Invalid || !double.IsFinite(frame.MeanSquare) || !double.IsFinite(frame.Peak) ||
                !double.IsFinite(frame.Seconds) || frame.Seconds <= 0 || frame.Seconds > 1 || frame.MeanSquare < 0 || frame.Peak < 0 ||
                !double.IsFinite(frame.ClippedRatio) || frame.ClippedRatio is < 0 or > 1;
            s.Current = invalid ? null : NoiseMeter.Dbfs(Math.Sqrt(frame.MeanSquare));
            s.Quality = invalid ? "Invalid" : frame.MeanSquare == 0 ? "DigitalSilence" :
                frame.ClippedRatio > 0.01 ? "Clipping" : "Good";
            if (!invalid)
            {
                s.Sampled += frame.Seconds; s.Energy += frame.MeanSquare * frame.Seconds;
                s.Clipped += frame.ClippedRatio * frame.Seconds; s.Peak = Math.Max(s.Peak, frame.Peak); s.Frames++;
            }
            double elapsed = _time.GetElapsedTime(s.Started).TotalSeconds;
            if (elapsed - s.LastPoint >= 0.5)
            {
                s.Trend.Enqueue(new(elapsed, s.Current, s.Quality)); s.LastPoint = elapsed;
                while (s.Trend.Count > 120) s.Trend.Dequeue();
            }
        }
    }

    private void Fail(Session s, string message)
    {
        lock (_sync)
        {
            if (_session != s || s.Stop.IsCancellationRequested) return;
            s.Failure = message; _state = "Stopping";
            _message = message + " 正在释放设备；完成前不会启动第二个采集。";
            s.Stop.Cancel();
        }
    }

    private async Task WatchAsync(Session s)
    {
        try
        {
            while (!s.Stop.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(250), _time, s.Stop.Token);
                lock (_sync)
                {
                    if (_session != s) return;
                    if (_time.GetElapsedTime(s.Started).TotalSeconds >= s.LimitSeconds)
                    { _state = "Stopping"; _message = "监测已到时，正在释放麦克风。"; s.Stop.Cancel(); continue; }
                    if (_state == "Starting" && _time.GetElapsedTime(s.Started).TotalSeconds > 5)
                        Fail(s, "麦克风启动超时，请检查 Windows 音频设备。");
                    else if (_state == "Active" && _time.GetElapsedTime(s.LastData).TotalSeconds > 3)
                        Fail(s, "超过 3 秒未收到音频，监测已停止。请检查麦克风后手动重试。");
                }
            }
        }
        catch (OperationCanceledException) when (s.Stop.IsCancellationRequested) { }
    }

    private async Task RunAsync(Session s)
    {
        INoiseCapture? capture = null;
        try
        {
            s.Stop.Token.ThrowIfCancellationRequested();
            capture = create(s.DeviceId);
            capture.Frame += frame => Receive(s, frame);
            capture.Failed += message => Fail(s, message);
            s.Stop.Token.ThrowIfCancellationRequested();
            capture.Start();
            lock (_sync)
            {
                s.DeviceName = capture.DeviceName;
                if (!s.Stop.IsCancellationRequested)
                {
                    _state = "Active";
                    _message = s.Scheduled ? "学校排程自动监测中；手动停止将跳过本次时段。不保存或上传原音频。" : "本机监测中；不保存或上传原音频。";
                }
            }
            await Task.Delay(Timeout.InfiniteTimeSpan, s.Stop.Token);
        }
        catch (OperationCanceledException) when (s.Stop.IsCancellationRequested) { }
        catch (Exception error) when (error is not OutOfMemoryException)
        { lock (_sync) s.Failure = "无法采集所选麦克风，请检查设备连接、Windows 麦克风权限或其他程序的独占使用。"; }
        finally
        {
            NoiseState finished;
            s.Stop.Cancel();
            try { capture?.Dispose(); }
            catch (Exception error) when (error is not OutOfMemoryException)
            { lock (_sync) { _cleanupFailed = true; s.Failure = "麦克风清理发生错误，请重启后台后再试。"; } }
            lock (_sync)
            {
                s.FinishedSeconds = _time.GetElapsedTime(s.Started).TotalSeconds;
                _state = s.Failure.Length > 0 ? "Faulted" : "Stopped";
                _message = s.Failure.Length > 0 ? s.Failure : "监测已停止，麦克风已释放。本次摘要保留至下次开始或后台退出。";
                s.Current = null; if (s.Failure.Length > 0) s.Quality = "NoData";
                _revision++;
                finished = Snapshot();
            }
            Completed?.Invoke(finished);
        }
    }

    public async ValueTask DisposeAsync()
    {
        Task work;
        lock (_sync)
        {
            _disposed = true;
            _session?.Stop.Cancel();
            work = _session is { } current ? Task.WhenAll(current.Work, current.Watch) : Task.CompletedTask;
        }
        await work;
        lock (_sync) _session?.Stop.Dispose();
    }
}

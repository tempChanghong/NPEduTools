using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using NPEduTools.Contracts;
using NPEduTools.Core;

namespace NPEduTools.Host;

/// <summary>Authenticated reverse connection with explicit, bounded normal-quit requests.</summary>
public sealed class ExamAwareService : IAsyncDisposable
{
    private sealed record Receipt(Guid Id, string Capability);
    private sealed record Saved(int Version, string? Path, long Revision, int Port, string Key, Receipt[] Receipts);
    private readonly string _path;
    private readonly IExamAwareTarget _target;
    private readonly SemaphoreSlim _commands = new(1);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly object _sync = new();
    private FileStream? _lock;
    private Saved _saved = new(1, null, 0, 0, "", []);
    private TcpListener? _listener;
    private Task[] _loops = [];
    private string? _failure;
    private Guid? _peer;
    private ExamAwareSample? _sample;
    private long _sampleAt, _lastOpen;
    private sealed class Session(TcpClient client, string key, string serverNonce, string clientNonce)
    {
        public TcpClient Client { get; } = client;
        public string Key { get; } = key;
        public string ServerNonce { get; } = serverNonce;
        public string ClientNonce { get; } = clientNonce;
        public long OutSequence;
    }
    private Session? _session;
    private ExamAwareQuitState? _quit;
    private ExamAwareQuitAck? _ack;
    private Task _quitTask = Task.CompletedTask;
    private Task _autoStartTask = Task.CompletedTask;
    private ExamAwareAutoStartState? _autoStart;
    private ExamAwareAutoStartAck? _autoStartAck;
    private Session? _autoStartSession;

    public ExamAwareService(string directory, IExamAwareTarget target)
    {
        _target = target;
        _path = Path.Combine(directory, "examaware.json");
        try
        {
            Directory.CreateDirectory(directory);
            _lock = new FileStream(Path.Combine(directory, "examaware.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            if (File.Exists(_path))
            {
                if (new FileInfo(_path).Length > 32768) throw new InvalidDataException();
                _saved = JsonSerializer.Deserialize<Saved>(File.ReadAllText(_path), Protocol.Json) ?? throw new InvalidDataException();
                if (_saved.Version != 1 || _saved.Port is < 1024 or > 65535 || _saved.Revision < 0 ||
                    _saved.Key is null || _saved.Key.Length != 64 || !IsHex(_saved.Key) || _saved.Receipts is null || _saved.Receipts.Length > 64 ||
                    _saved.Receipts.Any(x => x is null || x.Id == Guid.Empty || x.Capability is not ("examaware.start" or "examaware.settings" or "examaware.plugins" or "examaware.quit" or "examaware.pairing.reset" or "examaware.autostart.set")) ||
                    _saved.Path is { Length: > 2048 })
                    throw new InvalidDataException();
            }
            _listener = new TcpListener(IPAddress.Loopback, _saved.Port);
            _listener.Server.ExclusiveAddressUse = true;
            _listener.Start(4);
            if (_saved.Port == 0)
                Save(_saved with { Port = ((IPEndPoint)_listener.LocalEndpoint).Port, Key = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant() });
            _loops = Enumerable.Range(0, 2).Select(_ => ListenAsync(_lifetime.Token)).ToArray();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or SocketException or ArgumentException)
        {
            _listener?.Stop();
            _failure = "连接服务不可用：配置无法读取、目录被占用或配对端口被占用。原配置已保留。";
        }
    }

    public ExamAwareStatus Snapshot()
    {
        lock (_sync)
        {
            bool fresh = _peer is not null && _sample is not null && Stopwatch.GetElapsedTime(_sampleAt) < TimeSpan.FromSeconds(7);
            bool supported = fresh && _sample!.Version == "1.5.2" && _sample.Platform == "win32";
            return new(_saved.Path, _saved.Revision,
                _failure is not null ? "Unavailable" : !fresh ? "Disconnected" : supported ? "Connected" : "UnsupportedVersion",
                _failure ?? (!fresh ? "尚未连接桥接插件；可先打开软件并完成配对。" : supported ? "桥接已连接" : "已连接，但此版本尚未验证。当前支持 Windows 1.5.2。"),
                fresh ? _sample!.Version : null, supported ? _sample!.AutoStartRegistered : null,
                fresh ? _sample!.Packaged : null, _failure is null ? _saved.Port : null, _quit,
                supported && _sample!.CanSetAutoStart, _autoStart);
        }
    }

    public async Task<HostResponse> HandleAsync(HostRequest request, CancellationToken token = default)
    {
        await _commands.WaitAsync(token);
        try
        {
            HostResponse Reply(string outcome, string? code, string message, ExamAwarePairing? pairing = null) =>
                new(Protocol.Version, request.RequestId, outcome, code, message, ExamAware: Snapshot(), ExamAwarePairing: pairing);
            if (_failure is not null) return Reply("Rejected", "ExamAwareUnavailable", _failure);
            if (request.Capability == "examaware.status") return Reply("Succeeded", null, "ExamAware 状态已读取。");
            if (request.Capability == "examaware.pairing.get")
                return Reply("Succeeded", null, "配对信息已读取。", new(2, "127.0.0.1", _saved.Port, _saved.Key));
            if (_saved.Receipts.Any(x => x.Id == request.RequestId))
                return Reply("Rejected", "AlreadyAccepted", "该请求已受理过，没有重复执行。请查看操作状态。");
            if (!_autoStartTask.IsCompleted)
                return Reply("Rejected", "AutoStartBusy", "正在确认登录自启动设置，请稍候再操作。");
            if (request.Capability == "examaware.autostart.set")
            {
                if (request.AutoStartEnabled is not { } enabled)
                    return Reply("Rejected", "InvalidAutoStartParameters", "请明确指定开启或关闭登录自启动。");
                if (!_quitTask.IsCompleted) return Reply("Rejected", "QuitBusy", "正在确认退出结果，请稍候再操作。");
                if (_saved.Path is null) return Reply("Rejected", "PathMissing", "请先保存程序位置，以便核对要设置的进程。");
                if (request.ExpectedRevision != _saved.Revision)
                    return Reply("Rejected", "RevisionConflict", "程序位置或配对已变化，请刷新后重新确认设置。");
                string path = _target.Validate(_saved.Path);
                Session session;
                int pid;
                lock (_sync)
                {
                    if (Snapshot().BridgeState != "Connected" || _session is null)
                        return Reply("Rejected", "BridgeDisconnected", "桥接尚未就绪，没有发送设置请求。");
                    if (!_sample!.CanSetAutoStart)
                        return Reply("Rejected", "BridgeUpgradeRequired", "请更新桥接插件到 0.3.0 或兼容版本，并授权登录自启动设置。");
                    session = _session; pid = _sample.ProcessId;
                }
                var process = _target.Capture(pid, path);
                try { Save(_saved with { Receipts = Receipts(request) }); }
                catch { process.Dispose(); throw; }
                lock (_sync) { _autoStartSession = session; _autoStartAck = null; _autoStart = new(request.RequestId, "Sending", "正在设置登录自启动并读取登记结果…", enabled); }
                _autoStartTask = SetAutoStartAsync(request.RequestId, enabled, process, session);
                return Reply("Accepted", null, "设置请求已受理，正在等待 ExamAware 读回实际登记状态。");
            }
            if (request.Capability is "examaware.quit" or "examaware.pairing.reset")
            {
                if (!_quitTask.IsCompleted) return Reply("Rejected", "QuitBusy", "正在确认退出结果，请稍候再操作。");
                if (request.Capability == "examaware.pairing.reset")
                {
                    Save(_saved with { Key = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant(),
                        Revision = checked(_saved.Revision + 1), Receipts = Receipts(request) });
                    lock (_sync) { _session?.Client.Dispose(); _session = null; _peer = null; _sample = null; }
                    return Reply("Succeeded", null, "旧配对已撤销。请重新导出配对文件并在 ExamAware 中导入。");
                }
                if (_saved.Path is null) return Reply("Rejected", "PathMissing", "请先保存程序位置，以便核对要退出的进程。");
                if (request.ExpectedRevision is { } expected && expected != _saved.Revision)
                    return Reply("Rejected", "RevisionConflict", "程序位置或配对已变化，请重新确认退出对象。");
                string quitPath = _target.Validate(_saved.Path);
                Session session;
                int pid;
                lock (_sync)
                {
                    if (Snapshot().BridgeState != "Connected" || _session is null)
                        return Reply("Rejected", "BridgeDisconnected", "桥接尚未就绪，没有发送退出请求。");
                    session = _session; pid = _sample!.ProcessId;
                }
                var process = _target.Capture(pid, quitPath);
                try { Save(_saved with { Receipts = Receipts(request) }); }
                catch { process.Dispose(); throw; }
                lock (_sync) { _ack = null; _quit = new(request.RequestId, "Sending", "正在发送正常退出请求…", pid); }
                _quitTask = QuitAsync(request.RequestId, pid, process, session);
                return Reply("Accepted", null, "退出请求已受理，正在等待插件应答和目标进程结束。");
            }
            if (request.Capability == "examaware.config.set")
            {
                if (!_quitTask.IsCompleted) return Reply("Rejected", "QuitBusy", "正在确认退出结果，请稍候再修改程序位置。");
                if (request.ExpectedRevision != _saved.Revision) return Reply("Rejected", "RevisionConflict", "配置已变化，请刷新后重试。");
                string path = _target.Validate(request.ExecutablePath!);
                Save(_saved with { Path = path, Revision = checked(_saved.Revision + 1) });
                lock (_sync) _autoStart = null;
                return Reply("Succeeded", null, "程序位置已保存。版本以桥接读回为准。");
            }
            if (request.Capability is not ("examaware.start" or "examaware.settings" or "examaware.plugins"))
                return Reply("Rejected", "UnknownCapability", "不支持的 ExamAware 操作。");
            if (!_quitTask.IsCompleted) return Reply("Rejected", "QuitBusy", "正在确认退出结果，请稍候再打开软件。");
            if (_saved.Path is null) return Reply("Rejected", "PathMissing", "请先选择并保存 ExamAware2 的程序位置。");
            string executable = _target.Validate(_saved.Path);
            if (_lastOpen != 0 && Stopwatch.GetElapsedTime(_lastOpen) < TimeSpan.FromSeconds(3))
                return Reply("Rejected", "LaunchBusy", "刚刚已发送打开请求，请稍候。");
            // Persist acceptance before a side effect. A crash may leave an uncertain result, never a replay.
            Save(_saved with { Receipts = _saved.Receipts.TakeLast(63).Append(new Receipt(request.RequestId, request.Capability)).ToArray() });
            _lastOpen = Stopwatch.GetTimestamp();
            _target.Open(executable, request.Capability switch
            { "examaware.settings" => "examaware://settings/basic", "examaware.plugins" => "examaware://settings/plugins", _ => null });
            return Reply("Accepted", null, "已发送打开请求；桥接连接状态会单独更新。");
        }
        catch (LaunchTargetException ex)
        { return new(Protocol.Version, request.RequestId, "Rejected", ex.Code, ex.Message, ExamAware: Snapshot()); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or ArgumentException or InvalidOperationException)
        { return new(Protocol.Version, request.RequestId, "Rejected", "ExamAwareOperationFailed", "未能完成操作，请检查文件、权限或已有实例。", ExamAware: Snapshot()); }
        finally { _commands.Release(); }
    }

    public async Task VerifyConnectedProcessAsync(string path, long revision)
    {
        await _commands.WaitAsync();
        try
        {
            if (_saved.Revision != revision || !string.Equals(_saved.Path, path, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("ExamAware2 程序位置或配对已变化，请重新核实。");
            int pid;
            lock (_sync)
            {
                if (Snapshot().BridgeState != "Connected" || _sample is null)
                    throw new InvalidOperationException("ExamAware2 桥接尚未就绪，请检查连接后重试。");
                pid = _sample.ProcessId;
            }
            using var process = _target.Capture(pid, _target.Validate(path));
            if (process.HasExited) throw new InvalidOperationException("ExamAware2 已退出，请重试准备软件。");
        }
        finally { _commands.Release(); }
    }

    private Receipt[] Receipts(HostRequest request) => _saved.Receipts.TakeLast(63).Append(new Receipt(request.RequestId, request.Capability)).ToArray();

    private async Task SetAutoStartAsync(Guid id, bool enabled, IExamAwareProcess process, Session session)
    {
        void Update(string state, string message, bool? registered = null)
        { lock (_sync) _autoStart = new(id, state, message, enabled, registered); }
        using (process)
        {
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
                timeout.CancelAfter(TimeSpan.FromSeconds(10));
                long issued = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                string payload = JsonSerializer.Serialize(new ExamAwareAutoStartCommand(id, "autostart.set", issued, issued + 3000, enabled), Protocol.Json);
                long sequence = ++session.OutSequence;
                using var sendDeadline = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token);
                sendDeadline.CancelAfter(TimeSpan.FromSeconds(3));
                await Protocol.WriteAsync(session.Client.GetStream(), new ExamAwareFrame(2, "command", sequence, payload,
                    Sign(session.Key, FrameText("host", session.ServerNonce, session.ClientNonce, sequence, "command", payload))), sendDeadline.Token);
                while (true)
                {
                    ExamAwareAutoStartAck? ack;
                    bool sameSession;
                    lock (_sync) { ack = _autoStartAck; sameSession = ReferenceEquals(_session, session); }
                    if (ack is not null)
                    {
                        string state = ack.State;
                        if (state == "Succeeded" && ack.Registered != enabled) state = "Unconfirmed";
                        Update(state, state switch {
                            "Succeeded" => enabled ? "已读回：登录自启动已登记。Windows 仍可能禁用该启动项。" : "已读回：登录自启动未登记。",
                            "Mismatch" => "设置后的登记状态与请求不一致，请打开 ExamAware 设置检查。没有自动重试。",
                            "Denied" => "ExamAware 拒绝设置，请检查桥接插件的 app.configure 权限。",
                            "Expired" => "设置请求已过期，插件没有执行。请重新检查连接后操作。",
                            "Failed" => "设置调用失败，结果可能不确定；请查看最新登记状态。没有自动重试。",
                            _ => "未能读回设置结果，不能确认是否已更改。请检查 ExamAware 设置。没有自动重试。"
                        }, ack.Registered);
                        return;
                    }
                    if (!sameSession || process.HasExited)
                    { Update("Unconfirmed", "连接或目标进程已结束，不能确认是否已更改。请重新连接后查看登记状态；不会自动重试。"); return; }
                    await Task.Delay(50, timeout.Token);
                }
            }
            catch (Exception ex) when (ex is OperationCanceledException or IOException or SocketException or ObjectDisposedException or InvalidOperationException or System.ComponentModel.Win32Exception)
            { Update("Unconfirmed", "未能确认登录自启动设置结果，请检查 ExamAware 的登记状态。没有自动重试。"); }
        }
    }

    private async Task QuitAsync(Guid id, int pid, IExamAwareProcess process, Session session)
    {
        void Update(string state, string message) { lock (_sync) _quit = new(id, state, message, pid); }
        using (process)
        {
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
                timeout.CancelAfter(TimeSpan.FromSeconds(10));
                long issued = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                string payload = JsonSerializer.Serialize(new ExamAwareQuitCommand(id, "quit", issued, issued + 3000), Protocol.Json);
                long sequence = ++session.OutSequence;
                using var sendDeadline = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token);
                sendDeadline.CancelAfter(TimeSpan.FromSeconds(3));
                await Protocol.WriteAsync(session.Client.GetStream(), new ExamAwareFrame(2, "command", sequence, payload,
                    Sign(session.Key, FrameText("host", session.ServerNonce, session.ClientNonce, sequence, "command", payload))), sendDeadline.Token);
                while (true)
                {
                    ExamAwareQuitAck? ack;
                    lock (_sync) ack = _ack;
                    if (process.HasExited)
                    {
                        Update("Exited", ack?.State == "Accepted" ? "已确认目标 ExamAware 进程退出。" : "已确认目标进程结束；未收到插件的受理应答。");
                        return;
                    }
                    if (ack?.State is "Denied" or "Failed" or "Expired")
                    {
                        Update(ack.State, ack.State switch {
                            "Denied" => "ExamAware 拒绝退出：可能受学校管理策略或插件权限限制。",
                            "Expired" => "退出请求已过期，插件没有执行。请检查本机时间后重新操作。",
                            _ => "ExamAware 的正常退出调用失败，未强制结束进程。" });
                        return;
                    }
                    if (ack?.State == "Accepted") Update("AwaitingExit", "插件已受理，正在等待进程结束；如有未保存内容，请在 ExamAware 中处理。");
                    await Task.Delay(100, timeout.Token);
                }
            }
            catch (OperationCanceledException)
            { Update("Unconfirmed", "尚未确认退出。请检查 ExamAware 的未保存内容、关闭确认或管理策略；不会强制结束进程。"); }
            catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException or InvalidOperationException or System.ComponentModel.Win32Exception)
            { Update("Unconfirmed", "未能确认退出结果。连接中断不代表进程退出，请检查 ExamAware。没有自动重试。"); }
        }
    }

    public static string FrameText(string direction, string server, string client, long sequence, string type, string payload) =>
        $"{direction}\n{server}\n{client}\n{sequence}\n{type}\n{payload}";

    private void Save(Saved next)
    {
        string temporary = _path + ".tmp";
        using (var file = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
        { JsonSerializer.Serialize(file, next, Protocol.Json); file.Flush(true); }
        File.Move(temporary, _path, true);
        lock (_sync) _saved = next;
    }

    private async Task ListenAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            Guid id = Guid.NewGuid();
            try
            {
                using var client = await _listener!.AcceptTcpClientAsync(token);
                client.NoDelay = true;
                await using var stream = client.GetStream();
                string nonce = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
                string key;
                lock (_sync) key = _saved.Key;
                using var handshake = CancellationTokenSource.CreateLinkedTokenSource(token);
                handshake.CancelAfter(TimeSpan.FromSeconds(4));
                await Protocol.WriteAsync(stream, new ExamAwareHello(2, "hello", nonce, Sign(key, "host\n" + nonce)), handshake.Token);
                var challenge = await Protocol.ReadAsync<ExamAwareHello>(stream, handshake.Token);
                if (challenge.Version != 2 || challenge.Type != "challenge" || challenge.Nonce is not { Length: 64 } || !IsHex(challenge.Nonce) ||
                    !Verify(key, $"peer-auth\n{nonce}\n{challenge.Nonce}", challenge.Proof)) continue;
                await Protocol.WriteAsync(stream, new ExamAwareHello(2, "ready", challenge.Nonce,
                    Sign(key, $"host-auth\n{nonce}\n{challenge.Nonce}")), handshake.Token);
                var session = new Session(client, key, nonce, challenge.Nonce);
                long sequence = 1;
                while (!token.IsCancellationRequested)
                {
                    using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
                    deadline.CancelAfter(TimeSpan.FromSeconds(sequence == 1 ? 4 : 7));
                    var frame = await Protocol.ReadAsync<ExamAwareFrame>(stream, deadline.Token);
                    if (frame.Version != 2 || frame.Type is not ("status" or "reply" or "autostart.reply") || frame.Sequence != sequence || frame.Payload is null ||
                        !Verify(key, FrameText("peer", nonce, challenge.Nonce, sequence, frame.Type, frame.Payload), frame.Proof)) break;
                    sequence++;
                    if (frame.Type == "autostart.reply")
                    {
                        var ack = JsonSerializer.Deserialize<ExamAwareAutoStartAck>(frame.Payload, Protocol.Json);
                        if (ack is null || ack.State is not ("Succeeded" or "Mismatch" or "Denied" or "Failed" or "Expired" or "Unconfirmed") ||
                            (ack.State is "Succeeded" or "Mismatch" && ack.Registered is null)) break;
                        lock (_sync)
                        {
                            if (_saved.Key != key || _peer != id) break;
                            if (ReferenceEquals(_autoStartSession, session) && _autoStart?.RequestId == ack.RequestId && _autoStart.State == "Sending" && _autoStartAck is null)
                            {
                                _autoStartAck = ack;
                                if (_sample is not null) _sample = _sample with { AutoStartRegistered = ack.Registered };
                            }
                        }
                        continue;
                    }
                    if (frame.Type == "reply")
                    {
                        var ack = JsonSerializer.Deserialize<ExamAwareQuitAck>(frame.Payload, Protocol.Json);
                        if (ack is null || ack.State is not ("Accepted" or "Denied" or "Failed" or "Expired")) break;
                        lock (_sync)
                        {
                            if (_saved.Key != key || _peer != id) break;
                            if (_quit?.RequestId == ack.RequestId && _quit.State is "Sending" or "AwaitingExit") _ack = ack;
                        }
                        continue;
                    }
                    var sample = JsonSerializer.Deserialize<ExamAwareSample>(frame.Payload, Protocol.Json);
                    if (sample is null || sample.Name is null || sample.Name.Length > 128 || sample.Version is null || sample.Version.Length > 64 || sample.Platform is null || sample.Platform.Length > 16) break;
                    lock (_sync)
                    {
                        if (_saved.Key != key || (_peer is not null && _peer != id && Stopwatch.GetElapsedTime(_sampleAt) < TimeSpan.FromSeconds(7))) break;
                        _peer = id; _session = session; _sample = sample; _sampleAt = Stopwatch.GetTimestamp();
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException or JsonException or ArgumentException or ObjectDisposedException) { }
            finally { lock (_sync) { if (_peer == id) { _peer = null; _sample = null; _session = null; } } }
        }
    }

    public static string Sign(string key, string text) => Convert.ToHexString(HMACSHA256.HashData(Convert.FromHexString(key), Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
    public static bool Verify(string key, string text, string? proof) => proof is { Length: 64 } && IsHex(proof) &&
        CryptographicOperations.FixedTimeEquals(Convert.FromHexString(Sign(key, text)), Convert.FromHexString(proof));
    private static bool IsHex(string value) => value.All(Uri.IsHexDigit);

    public async ValueTask DisposeAsync()
    {
        await _lifetime.CancelAsync();
        _listener?.Stop();
        await Task.WhenAll(_loops);
        await _quitTask;
        await _autoStartTask;
        _lock?.Dispose();
        _lifetime.Dispose();
        _commands.Dispose();
    }
}

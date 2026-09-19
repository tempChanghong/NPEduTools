using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using NPEduTools.Contracts;
using NPEduTools.Core;

namespace NPEduTools.Host;

/// <summary>Paired, read-only reverse connection. No quit, configuration or exam-data commands.</summary>
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
                    _saved.Receipts.Any(x => x is null || x.Id == Guid.Empty || x.Capability is not ("examaware.start" or "examaware.settings" or "examaware.plugins")) ||
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
                _failure ?? (!fresh ? "尚未连接桥接插件；可先打开软件并完成配对。" : supported ? "桥接已连接（只读）" : "已连接，但此版本尚未验证。当前支持 Windows 1.5.2。"),
                fresh ? _sample!.Version : null, supported ? _sample!.AutoStartRegistered : null,
                fresh ? _sample!.Packaged : null, _failure is null ? _saved.Port : null);
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
                return Reply("Succeeded", null, "配对信息已读取。", new(1, "127.0.0.1", _saved.Port, _saved.Key));
            if (request.Capability == "examaware.config.set")
            {
                if (request.ExpectedRevision != _saved.Revision) return Reply("Rejected", "RevisionConflict", "配置已变化，请刷新后重试。");
                string path = _target.Validate(request.ExecutablePath!);
                Save(_saved with { Path = path, Revision = checked(_saved.Revision + 1) });
                return Reply("Succeeded", null, "程序位置已保存。版本以桥接读回为准。");
            }
            if (request.Capability is not ("examaware.start" or "examaware.settings" or "examaware.plugins"))
                return Reply("Rejected", "UnknownCapability", "不支持的 ExamAware 操作。");
            var receipt = _saved.Receipts.FirstOrDefault(x => x.Id == request.RequestId);
            if (receipt is not null)
                return Reply("Rejected", "AlreadyAccepted", "该请求已受理过，未重复启动。请查看软件窗口或使用新请求重试。");
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
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or ArgumentException)
        { return new(Protocol.Version, request.RequestId, "Rejected", "ExamAwareOperationFailed", "未能完成操作，请检查文件、权限或已有实例。", ExamAware: Snapshot()); }
        finally { _commands.Release(); }
    }

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
                using var handshake = CancellationTokenSource.CreateLinkedTokenSource(token);
                handshake.CancelAfter(TimeSpan.FromSeconds(4));
                await Protocol.WriteAsync(stream, new ExamAwareHello(1, "hello", nonce, Sign(_saved.Key, "host\n" + nonce)), handshake.Token);
                long sequence = 1;
                while (!token.IsCancellationRequested)
                {
                    using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
                    deadline.CancelAfter(TimeSpan.FromSeconds(sequence == 1 ? 4 : 7));
                    var frame = await Protocol.ReadAsync<ExamAwareFrame>(stream, deadline.Token);
                    if (frame.Version != 1 || frame.Type != "status" || frame.Sequence != sequence || frame.Payload is null ||
                        !Verify(_saved.Key, $"peer\n{nonce}\n{sequence}\n{frame.Payload}", frame.Proof)) break;
                    var sample = JsonSerializer.Deserialize<ExamAwareSample>(frame.Payload, Protocol.Json);
                    if (sample is null || sample.Name is null || sample.Name.Length > 128 || sample.Version is null || sample.Version.Length > 64 || sample.Platform is null || sample.Platform.Length > 16) break;
                    lock (_sync)
                    {
                        if (_peer is not null && _peer != id && Stopwatch.GetElapsedTime(_sampleAt) < TimeSpan.FromSeconds(7)) break;
                        _peer = id; _sample = sample; _sampleAt = Stopwatch.GetTimestamp();
                    }
                    sequence++;
                }
            }
            catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException or JsonException or ArgumentException) { }
            finally { lock (_sync) { if (_peer == id) { _peer = null; _sample = null; } } }
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
        _lock?.Dispose();
        _lifetime.Dispose();
        _commands.Dispose();
    }
}

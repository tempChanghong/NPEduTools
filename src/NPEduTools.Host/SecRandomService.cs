using System.Text.Json;
using NPEduTools.Contracts;
using NPEduTools.Integrations.SecRandom;

namespace NPEduTools.Host;

/// <summary>Local-only V3 operations. Persist intent before sending; never automatically repeat a draw.</summary>
public sealed class SecRandomService : IAsyncDisposable
{
    private sealed record Saved(int Version, string? Path, long Revision, SecRandomOperation[] Receipts);
    private readonly string _file;
    private readonly RuntimeOperationGate _gate;
    private readonly Func<string, SecRandomAction, CancellationToken, Task<SecRandomReply>> _send;
    private readonly Func<string, string> _validate;
    private readonly Action<string> _start;
    private readonly object _sync = new();
    private readonly CancellationTokenSource _lifetime = new();
    private Saved _saved = new(1, null, 0, []);
    private Task _work = Task.CompletedTask;
    private string _connection = "Unchecked";
    private DateTimeOffset? _checked;
    private string? _error;
    private bool _stopping;

    public SecRandomService(string directory, RuntimeOperationGate gate) : this(directory, gate,
        new SecRandomIpcClient().SendAsync, WindowsSecRandomTarget.Validate, WindowsSecRandomTarget.Start) { }
    internal SecRandomService(string directory, RuntimeOperationGate gate,
        Func<string, SecRandomAction, CancellationToken, Task<SecRandomReply>> send,
        Func<string, string> validate, Action<string> start)
    {
        _file = Path.Combine(directory, "secrandom.json"); _gate = gate; _send = send; _validate = validate; _start = start;
        try
        {
            Directory.CreateDirectory(directory);
            if (File.Exists(_file))
            {
                if (new FileInfo(_file).Length > 128 * 1024) throw new InvalidDataException("SecRandom 配置过大。");
                var loaded = JsonSerializer.Deserialize<Saved>(File.ReadAllText(_file), Protocol.Json) ?? throw new InvalidDataException();
                if (loaded.Version != 1 || loaded.Revision is < 0 or long.MaxValue || loaded.Receipts is null || loaded.Receipts.Length > 32 ||
                    loaded.Receipts.Any(x => x is null || x.RequestId == Guid.Empty || x.State is not ("Running" or "Succeeded" or "Failed" or "Unknown" or "Acknowledged")) ||
                    loaded.Receipts.Select(x => x.RequestId).Distinct().Count() != loaded.Receipts.Length)
                    throw new InvalidDataException("SecRandom 配置无效。");
                _saved = loaded;
                // Host restart cannot tell whether the upstream finished a previously sent request.
                if (_saved.Receipts.Any(x => x.State == "Running"))
                    Save(_saved with { Receipts = _saved.Receipts.Select(x => x.State == "Running" ? x with
                    { State = "Unknown", ErrorCode = "host_restarted", Message = "后台重启，先核实 SecRandom 中的操作结果。", UpdatedAt = DateTimeOffset.UtcNow } : x).ToArray() });
            }
        }
        catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException or JsonException)
        { _error = "SecRandom 配置无法读取，未覆盖原文件。"; }
    }

    public SecRandomState Snapshot() { lock (_sync) return new(_saved.Path, _saved.Revision, _connection, _checked,
        _saved.Receipts.FirstOrDefault(x => x.State == "Running") ?? _saved.Receipts.FirstOrDefault(x => x.State == "Unknown") ?? _saved.Receipts.LastOrDefault(), _error); }
    public HostResponse Handle(HostRequest request)
    {
        lock (_sync)
        {
            HostResponse Reply(string outcome, string? code, string message) => new(Protocol.Version, request.RequestId, outcome, code, message, SecRandom: Snapshot());
            if (request.Capability == "secrandom.status") return Reply("Succeeded", null, "SecRandom 本机状态；接口状态以上次检查时间为准。");
            if (_stopping || _error is not null) return Reply("Rejected", "SecRandomUnavailable", _error ?? "后台正在停止。");
            if (request.ExpectedRevision != _saved.Revision) return Reply("Rejected", "RevisionConflict", "程序位置已变化，请刷新后再操作。");
            var existing = _saved.Receipts.FirstOrDefault(x => x.RequestId == request.RequestId);
            if (existing is not null) return existing.Action != request.SecRandom?.Action
                ? Reply("Rejected", "RequestIdReuse", "该请求编号已用于其他操作，请使用新的请求编号。")
                : Reply(existing.State is "Running" ? "Accepted" : existing.State, existing.ErrorCode, existing.Message);
            if (!_work.IsCompleted) return Reply("Rejected", "OperationBusy", "SecRandom 操作尚未结束，请稍候。");
            if (request.Capability == "secrandom.config.set")
            {
                if (_saved.Receipts.Any(x => x.State == "Unknown")) return Reply("Rejected", "ResultUncertain", "先核实上次操作结果，再更改程序位置。");
                try
                {
                    using var lease = _gate.TryEnterMutation();
                    if (lease is null) return Reply("Rejected", "OperationBusy", "正在切换课堂环境，请稍后设置。");
                    Save(_saved with { Path = _validate(request.ExecutablePath!), Revision = checked(_saved.Revision + 1) });
                    _connection = "Unchecked"; _checked = null;
                    return Reply("Succeeded", null, "已保存 SecRandom 程序位置，请检查接口。");
                }
                catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException or InvalidOperationException or System.ComponentModel.Win32Exception or ArgumentException)
                { return Reply("Rejected", "InvalidExecutable", "无法保存程序位置：" + e.Message); }
            }
            var command = request.SecRandom!;
            if (command.Action == "acknowledge")
            {
                var pending = _saved.Receipts.FirstOrDefault(x => x.RequestId == command.AcknowledgeOperation && x.State == "Unknown");
                if (pending is null) return Reply("Rejected", "OperationChanged", "待核实操作已变化，请刷新。");
                try
                {
                    Save(_saved with { Receipts = _saved.Receipts.Select(x => x == pending ? x with { State = "Acknowledged", Message = "已由本机用户核实；没有自动重发操作。", UpdatedAt = DateTimeOffset.UtcNow } : x).ToArray() });
                    return Reply("Succeeded", null, "已解除待核实状态；不会重新执行上次操作。");
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return Reply("Rejected", "StorageFailure", "无法保存核实结果。请检查磁盘。"); }
            }
            bool check = command.Action == "check";
            if (!check && _saved.Receipts.Any(x => x.State == "Unknown")) return Reply("Rejected", "ResultUncertain", "上次操作结果待核实，请先查看 SecRandom 窗口及历史，再确认解除。");
            if (_saved.Path is null) return Reply("Rejected", "PathMissing", "请先保存 SecRandom V3 程序位置。");
            var reservation = check ? null : _gate.TryEnterMutation();
            if (!check && reservation is null) return Reply("Rejected", "OperationBusy", "正在切换课堂环境，请稍后操作。");
            try
            {
                string path = _validate(_saved.Path);
                var op = new SecRandomOperation(request.RequestId, command.Action, "Running", "请求已受理，正在等待 SecRandom。", DateTimeOffset.UtcNow);
                // Read-only checks must not evict the unresolved mutation that blocks duplicate draws.
                var pending = _saved.Receipts.Where(x => x.State == "Unknown").ToArray();
                Save(_saved with { Receipts = pending.Concat(_saved.Receipts.Where(x => x.State != "Unknown").Append(op).TakeLast(32 - pending.Length)).ToArray() });
                _work = RunAsync(op, path, reservation); reservation = null;
                return Reply("Accepted", null, "请求已受理，实际结果以执行记录为准。");
            }
            catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException or InvalidOperationException or System.ComponentModel.Win32Exception or ArgumentException)
            { return Reply("Rejected", "SecRandomUnavailable", "请求未发送：" + e.Message); }
            finally { reservation?.Dispose(); }
        }
    }

    private async Task RunAsync(SecRandomOperation operation, string path, IDisposable? lease)
    {
        // Yield before starting so IPC acceptance and _work assignment precede any completion.
        await Task.Yield();
        var reply = new SecRandomReply(false, "internal_error", "SecRandom 操作未完成，请核实软件窗口。", true);
        bool mutationAttempted = false;
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
            deadline.CancelAfter(TimeSpan.FromSeconds(35));
            var action = operation.Action switch
            {
                "check" => SecRandomAction.Probe, "open" => SecRandomAction.ShowRollCall,
                "show-float" => SecRandomAction.ShowFloat, "hide-float" => SecRandomAction.HideFloat,
                "quick-draw" => SecRandomAction.QuickDraw, _ => throw new InvalidOperationException()
            };
            if (operation.Action is "open" or "show-float" or "quick-draw")
            {
                _start(path);
                // Only read-only probes may retry through startup. The mutation is sent exactly once.
                var readyUntil = DateTimeOffset.UtcNow.AddSeconds(12);
                do
                {
                    reply = await _send(path, SecRandomAction.Probe, deadline.Token);
                    if (reply.Succeeded || reply.Code is not ("pipe_unavailable" or "internal_error")) break;
                    await Task.Delay(250, deadline.Token);
                } while (DateTimeOffset.UtcNow < readyUntil);
                if (reply.Succeeded) { mutationAttempted = true; reply = await _send(path, action, deadline.Token); }
            }
            else { mutationAttempted = action != SecRandomAction.Probe; reply = await _send(path, action, deadline.Token); }
        }
        catch (OperationCanceledException) { reply = new(false, "timeout", "操作等待已结束，请核实 SecRandom 中的结果。", mutationAttempted); }
        catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException or InvalidOperationException or System.ComponentModel.Win32Exception)
        { reply = new(false, "operation_failed", "请求未完成，请核实软件窗口。", mutationAttempted); }
        finally
        {
            lock (_sync)
            {
                _connection = reply.Succeeded || reply.Code is "authorization_denied" or "invalid_state" or "feature_disabled" ? "Ready" : "Unavailable";
                _checked = DateTimeOffset.UtcNow;
                var done = operation with { State = reply.Succeeded ? "Succeeded" : reply.Uncertain ? "Unknown" : "Failed",
                    ErrorCode = reply.Succeeded ? null : reply.Code, Message = reply.Message, UpdatedAt = DateTimeOffset.UtcNow,
                    Winner = reply.Winner is { } w ? new(w.Id, w.Name, w.Gender) : null };
                try { Save(_saved with { Receipts = _saved.Receipts.Select(x => x.RequestId == operation.RequestId ? done : x).ToArray() }); }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                { _error = "操作可能已完成，但结果无法写入磁盘；请核实 SecRandom。"; }
            }
            lease?.Dispose();
        }
    }

    private void Save(Saved saved)
    {
        string temporary = _file + ".tmp";
        try { File.WriteAllText(temporary, JsonSerializer.Serialize(saved, Protocol.Json)); File.Move(temporary, _file, true); _saved = saved; }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    internal Task WhenIdle => _work;
    public async ValueTask DisposeAsync()
    { lock (_sync) { if (_stopping) return; _stopping = true; } _lifetime.Cancel(); await _work; _lifetime.Dispose(); }
}

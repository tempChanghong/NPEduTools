using NPEduTools.Contracts;
using NPEduTools.Core;

namespace NPEduTools.Host;

public sealed class LaunchService : IAsyncDisposable
{
    private readonly LaunchStore? _store;
    private readonly IClassIslandLaunchTarget _target;
    private readonly ILessonStatusReader _reader;
    private readonly TimeSpan _readinessTimeout;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly CancellationTokenSource _shutdown = new();
    private Task? _active;
    private bool _storageFault;
    private readonly string? _loadError;

    public LaunchService(string directory, IClassIslandLaunchTarget target, ILessonStatusReader reader, TimeSpan? readinessTimeout = null)
    {
        _target = target;
        _reader = reader;
        _readinessTimeout = readinessTimeout ?? TimeSpan.FromSeconds(20);
        try
        {
            _store = new LaunchStore(directory);
            _storageFault = _store.Warning is not null;
            if (!_storageFault && _store.Document.Executions.Any(x => x.Outcome == "Running"))
                _store.Save(_store.Document with { Executions = _store.Document.Executions.Select(x => x.Outcome == "Running"
                    ? x with { Outcome = "Unknown", ErrorCode = "HostInterrupted", UpdatedAt = DateTimeOffset.UtcNow,
                        Message = "后台在启动验证完成前退出，结果待核实；不会自动重放启动。" } : x).ToList() });
        }
        catch (Exception ex) when (IsStorageError(ex))
        {
            _storageFault = true;
            _loadError = "配置或记录无法读取，启动功能已停用；只读课程监听仍可使用。";
        }
    }

    public async Task<HostResponse> HandleAsync(HostRequest request, CancellationToken token)
    {
        await _gate.WaitAsync(token);
        try
        {
            if (_store is null) return Response(request, "Failed", "StorageUnavailable", _loadError ?? "无法读取启动数据。");
            var document = _store.Document;
            if (request.Capability is "classisland.config.get" or "classisland.execution.get")
            {
                var execution = request.OperationId is { } id ? document.Executions.Find(x => x.RequestId == id) : document.Executions.LastOrDefault();
                return Response(request, execution is null && request.OperationId is not null ? "Rejected" : "Succeeded",
                    execution is null && request.OperationId is not null ? "ExecutionNotFound" : null, "已读取启动配置和记录。", execution);
            }
            if (_storageFault) return Response(request, "Failed", "StorageUnavailable",
                "记录不完整或写入失败，启动与配置修改已停用；请核实数据文件后重启后台。");
            if (_shutdown.IsCancellationRequested) return Response(request, "Rejected", "HostStopping", "后台正在停止。");
            if (request.Capability is "classisland.start" or "classisland.verify" && document.Executions.Find(x => x.RequestId == request.RequestId) is { } previous)
                return Response(request, previous.Outcome, previous.ErrorCode, previous.Message, previous);
            if (_active is { IsCompleted: false })
                return Response(request, "Rejected", "LaunchBusy", "已有启动操作正在验证，请等待其结果。", document.Executions.LastOrDefault());
            if (request.Capability == "classisland.config.set")
            {
                if (request.ExpectedRevision != document.Settings.Revision)
                    return Response(request, "Rejected", "ConfigurationConflict", "配置已被更新，请重新读取后再保存。");
                string path = _target.ValidateExecutable(request.ExecutablePath!);
                _store.Save(document with { Settings = new(document.Settings.Revision + 1, path) });
                return Response(request, "Succeeded", null, "ClassIsland 路径已保存。");
            }
            if (document.Settings.ExecutablePath is not { } configured)
                return Response(request, "Rejected", "ExecutableNotConfigured", "请先选择并保存 ClassIsland 路径。");
            if (request.Capability == "classisland.verify" && (request.ExpectedRevision != document.Settings.Revision ||
                !string.Equals(request.ExecutablePath, configured, StringComparison.OrdinalIgnoreCase)))
                return Response(request, "Rejected", "ConfigurationConflict", "启动路径配置已变化，未验证其他位置的实例。");
            string executable = _target.ValidateExecutable(configured);
            // Retain at least 30 days of deduplication; unknown outcomes are never pruned automatically.
            var retained = document.Executions.Where(x => x.Outcome == "Unknown" || x.UpdatedAt >= DateTimeOffset.UtcNow.AddDays(-30)).ToList();
            if (retained.Count >= 200) return Response(request, "Rejected", "ExecutionStoreFull", "启动记录已达到容量上限，请保留记录并联系维护人员处理。");
            var now = DateTimeOffset.UtcNow;
            var pending = new LaunchExecution(request.RequestId, executable, now, now, "Running", null,
                request.Capability == "classisland.verify" ? "正在验证已有 ClassIsland 的课程连接…" : "正在检查并启动 ClassIsland…");
            retained.Add(pending);
            _store.Save(document with { Executions = retained });
            _active = Task.Run(() => ExecuteAsync(pending, request.Capability == "classisland.verify"));
            return Response(request, "Running", null, pending.Message, pending);
        }
        catch (LaunchTargetException ex) { return Response(request, "Rejected", ex.Code, ex.Message); }
        catch (Exception ex) when (IsStorageError(ex))
        {
            _storageFault = true;
            return Response(request, "Failed", "StorageUnavailable", "配置或记录无法写入，尚未受理新的启动操作。");
        }
        finally { _gate.Release(); }
    }

    private HostResponse Response(HostRequest request, string outcome, string? code, string message, LaunchExecution? execution = null) =>
        new(Protocol.Version, request.RequestId, outcome, code, message, Launch: _store is null ? null :
            new(_store.Document.Settings, execution ?? _store.Document.Executions.LastOrDefault(),
                _storageFault ? _store.Warning ?? "启动记录不完整，启动功能已停用，请检查数据目录后重启后台。" : _store.Warning));

    private async Task ExecuteAsync(LaunchExecution execution, bool verifyOnly)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token);
        deadline.CancelAfter(_readinessTimeout);
        var result = execution;
        try
        {
            deadline.Token.ThrowIfCancellationRequested();
            _target.ValidateExecutable(execution.ExecutablePath);
            bool existing = _target.IsRunning(execution.ExecutablePath);
            if (verifyOnly && !existing)
                throw new LaunchTargetException("ClassIslandExited", "ClassIsland 已不在运行，连接验证已停止；不会重新启动。");
            if (!existing)
            {
                int pid = _target.Start(execution.ExecutablePath);
                result = result with { ProcessId = pid, Message = "进程已启动，正在等待课程接口就绪…" };
                await SaveResultAsync(result);
            }
            while (true)
            {
                deadline.Token.ThrowIfCancellationRequested();
                var status = await _reader.ReadAsync(new(TimeSpan.FromSeconds(3), TimeSpan.Zero), deadline.Token);
                if (status.Outcome == "Succeeded" && status.Status is not null && _target.IsRunning(execution.ExecutablePath))
                {
                    result = result with { Outcome = "Succeeded", Message = existing ? "ClassIsland 已运行，课程接口已就绪。" : "ClassIsland 启动成功，课程接口已就绪。" };
                    break;
                }
                if (!existing && !_target.IsRunning(execution.ExecutablePath))
                {
                    result = result with { Outcome = "Failed", ErrorCode = "ClassIslandExited", Message = "ClassIsland 在就绪前退出，请检查本体运行环境。" };
                    break;
                }
                await Task.Delay(300, deadline.Token);
            }
        }
        catch (OperationCanceledException)
        {
            result = result with { Outcome = _shutdown.IsCancellationRequested ? "Unknown" : "TimedOut",
                ErrorCode = _shutdown.IsCancellationRequested ? "HostStopping" : "ClassIslandNotReady",
                Message = _shutdown.IsCancellationRequested ? "后台停止，启动结果待核实；不会关闭 ClassIsland 或重放启动。" : "未在期限内确认课程接口就绪；ClassIsland 可能仍在运行，不会自动重复启动。" };
        }
        catch (LaunchTargetException ex) { result = result with { Outcome = "Failed", ErrorCode = ex.Code, Message = ex.Message }; }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            result = result with { Outcome = "Unknown", ErrorCode = "LaunchVerificationFailed", Message = "启动或验证未能完成，结果待核实；不会自动重复启动。" };
        }
        await SaveResultAsync(result with { UpdatedAt = DateTimeOffset.UtcNow });
    }

    private async Task SaveResultAsync(LaunchExecution result)
    {
        await _gate.WaitAsync();
        try
        {
            if (_storageFault || _store is null) return;
            _store.Save(_store.Document with { Executions = _store.Document.Executions.Select(x => x.RequestId == result.RequestId ? result : x).ToList() });
        }
        catch (Exception ex) when (IsStorageError(ex)) { _storageFault = true; }
        finally { _gate.Release(); }
    }

    private static bool IsStorageError(Exception ex) => ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or InvalidDataException;

    public async Task StopAsync()
    {
        _shutdown.Cancel();
        if (_active is not null) await _active;
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        _store?.Dispose();
        _shutdown.Dispose();
        _gate.Dispose();
    }
}

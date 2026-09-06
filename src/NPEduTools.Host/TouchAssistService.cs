using System.Diagnostics;
using System.Runtime.Versioning;
using NPEduTools.Contracts;
using NPEduTools.PowerPoint.Diagnostics;

namespace NPEduTools.Host;

[SupportedOSPlatform("windows")]
public sealed class TouchAssistService(Func<ProcessStartInfo> workerStart) : IAsyncDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private PowerPointTouchAssist? _engine;
    private CancellationTokenSource? _stop;
    private Task? _run;
    private bool _shutdown;
    private TouchAssistState _state = new(false, false, false, "已关闭");
    public TouchAssistState State => Volatile.Read(ref _state);

    public async Task<HostResponse> HandleAsync(HostRequest request, CancellationToken token)
    {
        await _gate.WaitAsync(token);
        try
        {
            if (_shutdown) return Reply(request, "Rejected", "后台正在退出。", "Stopping");
            switch (request.Capability)
            {
                case "presentation.touch.enable":
                    if (_run is { IsCompleted: false }) break;
                    _stop?.Dispose(); _stop = new();
                    var engine = new PowerPointTouchAssist { AllowUnmarkedMouse = State.AllowUnmarkedMouse };
                    _engine = engine;
                    var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    _state = State with { State = "开启中", Error = null };
                    engine.StatusChanged += status =>
                    {
                        Volatile.Write(ref _state, new(true, !engine.Enabled, engine.AllowUnmarkedMouse, status.State, status.Error));
                        ready.TrySetResult();
                    };
                    var lifetime = _stop.Token;
                    _run = Task.Run(async () =>
                    {
                        try { await engine.RunAsync(workerStart, lifetime); }
                        catch (Exception error) when (error is not OutOfMemoryException)
                        {
                            Volatile.Write(ref _state, new(false, false, engine.AllowUnmarkedMouse, "无法启用辅助",
                                error is InvalidOperationException ? error.Message : $"辅助已停止：{error.GetType().Name}"));
                        }
                        finally { ready.TrySetResult(); }
                    });
                    await ready.Task.WaitAsync(TimeSpan.FromSeconds(4), token);
                    break;
                case "presentation.touch.disable": await DisableAsync(); break;
                case "presentation.touch.pause":
                case "presentation.touch.resume":
                    if (_run is not { IsCompleted: false } || _engine is null)
                        return Reply(request, "Rejected", "请先开启辅助。", "NotRunning");
                    _engine.Enabled = request.Capability.EndsWith("resume", StringComparison.Ordinal);
                    _state = State with { Paused = !_engine.Enabled, State = _engine.Enabled ? "正在恢复辅助…" : "正在暂停辅助…" };
                    break;
                case "presentation.touch.compat.on":
                case "presentation.touch.compat.off":
                    bool allow = request.Capability.EndsWith(".on", StringComparison.Ordinal);
                    if (_engine is not null) _engine.AllowUnmarkedMouse = allow;
                    _state = State with { AllowUnmarkedMouse = allow };
                    break;
            }
            return Reply(request, State.Error is null ? "Succeeded" : "Failed", State.Error ?? State.State,
                State.Error is null ? null : "TouchAssistUnavailable");
        }
        catch (TimeoutException)
        {
            await DisableAsync();
            _state = State with { State = "开启超时", Error = "辅助未能及时启动，请重试。" };
            return Reply(request, "Failed", "辅助未能及时启动，请重试。", "TouchAssistTimeout");
        }
        finally { _gate.Release(); }
    }

    private HostResponse Reply(HostRequest request, string outcome, string message, string? error = null) =>
        new(Protocol.Version, request.RequestId, outcome, error, message, TouchAssist: State);

    private async Task DisableAsync()
    {
        if (_engine is not null) _engine.Enabled = false;
        _stop?.Cancel();
        if (_run is not null) await _run;
        _stop?.Dispose(); _stop = null; _run = null; _engine = null;
        _state = new(false, false, State.AllowUnmarkedMouse, "已关闭");
    }

    public async Task StopAsync()
    {
        await _gate.WaitAsync();
        try { _shutdown = true; await DisableAsync(); }
        finally { _gate.Release(); }
    }
    public async ValueTask DisposeAsync() { await StopAsync(); }
}

using System.Diagnostics;
using System.Text.Json;
using NPEduTools.Contracts;

namespace NPEduTools.Host;

public sealed class RecorderProcess(string executable, string? fixture = null) : IAsyncDisposable
{
    private readonly SemaphoreSlim _write = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly TaskCompletionSource _hello = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Process? _process;
    private Task? _read, _lease, _errors;
    private RecordingState _state = new("Idle", "准备录制");
    private RecorderControl? _control;
    public RecordingState State => Volatile.Read(ref _state) with { Control = Volatile.Read(ref _control) };
    private bool _disposed;
    public bool Alive { get { try { return _process is { HasExited: false }; } catch (InvalidOperationException) { return false; } } }
    private ProcessStartInfo Info(params string[] args)
    {
        var info = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (string arg in args) info.ArgumentList.Add(arg);
        return info;
    }
    public async Task<RecordingEnvironment> ProbeAsync()
    {
        using var process = Process.Start(Info("--probe")) ?? throw new IOException("录制组件无法启动。");
        var output = process.StandardOutput.ReadToEndAsync(); var errors = process.StandardError.ReadToEndAsync();
        try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(12)); }
        catch { if (!process.HasExited) process.Kill(true); throw; }
        await errors;
        if (process.ExitCode != 0) throw new IOException("录制设备检测失败。");
        return JsonSerializer.Deserialize<RecordingEnvironment>(await output, RecordingContract.Json) ?? throw new InvalidDataException("录制环境无效。");
    }
    public async Task StartAsync(RecordingOptions options, RecorderControl control)
    {
        _control = control;
        _state = new("Starting", "后台正在准备录制", Control: control);
        _process = Process.Start(Info(fixture is null ? [] : ["--fixture-window", fixture])) ?? throw new IOException("无法启动录制组件。");
        _read = ReadAsync(_process);
        _errors = DrainAsync(_process);
        await _hello.Task.WaitAsync(TimeSpan.FromSeconds(8));
        if (State.Phase == "Failed" || !Alive) throw new IOException(State.Error ?? "录制组件提前退出。");
        // Absolute deadlines include startup time; never grant a fresh duration after the handshake.
        if (control.Owner == "Automatic" && RecorderDeadline.SecondsUntil(control.HardDeadline) <= 0) throw new IOException("准备期间已超过截止时间。");
        _control = control with { LeaseDeadline = RecorderDeadline.After(TimeSpan.FromSeconds(8)) };
        await WriteAsync(new("start", options, _control));
        _lease = LeaseAsync();
    }
    private async Task ReadAsync(Process process)
    {
        try
        {
            while (await process.StandardOutput.ReadLineAsync() is { } line)
            {
                if (line.Length > 16384) throw new InvalidDataException("录制状态过大。");
                var state = JsonSerializer.Deserialize<RecordingState>(line, RecordingContract.Json) ?? throw new InvalidDataException();
                if (state.Phase != "Idle" && (state.Control is null && state.Phase == "Failed" || _control?.Matches(state.Control) == true))
                    Volatile.Write(ref _state, state);
                _hello.TrySetResult();
            }
            await process.WaitForExitAsync();
            if (State.Active) Fail("录制进程意外退出；已写入的片段保留。");
        }
        catch (Exception error) when (error is IOException or JsonException or InvalidOperationException)
        { Fail("录制状态连接中断，录制器将按截止或租约结束。"); }
        finally { _hello.TrySetResult(); }
    }
    private static async Task DrainAsync(Process process)
    { try { while (await process.StandardError.ReadLineAsync() is not null) { } } catch (IOException) { } }
    private async Task LeaseAsync()
    {
        try
        {
            while (!_lifetime.IsCancellationRequested && Alive && State.Active)
            {
                await Task.Delay(2000, _lifetime.Token);
                if (_control is not { } c || !Alive || !State.Active) break;
                await WriteAsync(new("lease", Control: c with { LeaseDeadline = RecorderDeadline.After(TimeSpan.FromSeconds(8)) }));
            }
        }
        catch (Exception error) when (error is IOException or OperationCanceledException or InvalidOperationException or TimeoutException) { }
    }
    public async Task CommandAsync(string action, RecorderControl expected)
    {
        if (_control?.Matches(expected) != true) throw new InvalidOperationException("录制会话已变化，旧操作已拒绝。");
        if (action == "shorten" && _control.Owner == "Automatic")
            _control = _control with { HardDeadline = Math.Min(_control.HardDeadline, expected.HardDeadline) };
        await WriteAsync(new(action, Control: _control with { LeaseDeadline = RecorderDeadline.After(TimeSpan.FromSeconds(8)) }));
    }
    private async Task WriteAsync(RecorderCommand command)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        await _write.WaitAsync(deadline.Token);
        try
        {
            if (_process is null || _process.HasExited) throw new IOException("录制进程已结束。");
            await _process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(command, RecordingContract.Json).AsMemory(), deadline.Token);
            await _process.StandardInput.FlushAsync(deadline.Token);
        }
        finally { _write.Release(); }
    }
    public void Fail(string message) => Volatile.Write(ref _state, State with { Phase = "Failed", Message = "录制未完成", Error = message });
    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        _lifetime.Cancel();
        if (_lease is not null) await _lease;
        if (_process is not null)
        {
            try { _process.StandardInput.Close(); } catch (IOException) { }
            try { await _process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)); }
            catch (TimeoutException)
            {
                // Do not kill a finalizing recorder; EOF and its independent watchdog bound its lifetime.
                return;
            }
            if (_read is not null) await _read; if (_errors is not null) await _errors;
            _process.Dispose();
        }
        _lifetime.Dispose();
    }
}

using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows.Threading;
using NPEduTools.Contracts;

namespace NPEduTools.App;

internal sealed class RecordingClient(Dispatcher dispatcher, string? fixtureWindow = null) : IAsyncDisposable
{
    private Process? _worker;
    private Task? _reader, _errors;
    private bool _closing;
    private string? _pending;
    private TaskCompletionSource? _hello;
    public RecordingState State { get; private set; } = new("Idle", "准备录制");
    public event Action<RecordingState>? Changed;
    private static string Executable => Path.Combine(AppContext.BaseDirectory, "Recorder", "NPEduTools.Recorder.exe");
    private static ProcessStartInfo StartInfo(params string[] arguments)
    {
        if (!File.Exists(Executable)) throw new FileNotFoundException("录制组件缺失，请补齐完整程序目录。");
        var info = new ProcessStartInfo(Executable) { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (string argument in arguments) info.ArgumentList.Add(argument);
        return info;
    }
    public static async Task<RecordingEnvironment> ProbeAsync()
    {
        using var process = Process.Start(StartInfo("--probe")) ?? throw new IOException("无法检测录制环境。");
        var output = process.StandardOutput.ReadToEndAsync();
        var errors = process.StandardError.ReadToEndAsync();
        try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(12)); }
        catch { if (!process.HasExited) process.Kill(true); throw; }
        await errors;
        if (process.ExitCode != 0) throw new IOException("无法检测录制设备，请稍后重试。");
        return JsonSerializer.Deserialize<RecordingEnvironment>(await output, RecordingContract.Json) ?? throw new InvalidDataException("录制环境返回无效。");
    }
    public async Task SendAsync(string action, RecordingOptions? options = null)
    {
        if (_closing || _pending is not null || (State.Busy && action != "stop")) return;
        _pending = action;
        Apply(State with { Phase = action switch { "start" or "resume" => "Starting", "pause" => "Pausing", _ => "Saving" },
            Message = action == "stop" ? "正在保存录制…" : "正在应用录制操作…", Error = null, OutputFile = action == "start" ? null : State.OutputFile });
        try
        {
            if (_worker is null || _worker.HasExited)
            {
                if (action != "start") throw new IOException("录制进程已退出，片段保留在原目录。");
                if (_reader is not null) await _reader;
                _worker?.Dispose();
                _hello = new(TaskCreationOptions.RunContinuationsAsynchronously);
                var process = Process.Start(StartInfo(fixtureWindow is null ? [] : ["--fixture-window", fixtureWindow])) ?? throw new IOException("无法启动录制进程。");
                _worker = process;
                _reader = ReadAsync(process);
                _errors = DrainErrorsAsync(process);
                await _hello.Task.WaitAsync(TimeSpan.FromSeconds(8));
                if (process.HasExited) throw new IOException("录制进程未能启动。");
            }
            await _worker.StandardInput.WriteLineAsync(JsonSerializer.Serialize(new RecorderCommand(action, options), RecordingContract.Json));
            await _worker.StandardInput.FlushAsync();
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        { _pending = null; Apply(State with { Phase = "Failed", Message = "无法完成录制操作", Error = error.Message }); }
    }
    private async Task DrainErrorsAsync(Process process)
    {
        // Drain continuously so a diagnostic pipe cannot stall the worker.
        try { while (await process.StandardError.ReadLineAsync() is not null) { } }
        catch (IOException) { }
    }
    private async Task ReadAsync(Process process)
    {
        try
        {
            while (await process.StandardOutput.ReadLineAsync() is { } line)
            {
                var state = JsonSerializer.Deserialize<RecordingState>(line, RecordingContract.Json);
                if (state is null) continue;
                _hello?.TrySetResult();
                await dispatcher.InvokeAsync(() =>
                {
                    if (_closing) return;
                    // A heartbeat queued before the command must not re-enable a button mid-transition.
                    bool expected = state.Phase == "Failed" || _pending switch
                    {
                        "start" or "resume" => state.Phase == "Starting",
                        "pause" => state.Phase == "Pausing",
                        "stop" => state.Phase is "Saving" or "Saved",
                        _ => true
                    };
                    if (!expected) return;
                    _pending = null; Apply(state);
                });
            }
            await process.WaitForExitAsync();
        }
        catch (Exception error) when (error is IOException or JsonException or InvalidOperationException) { }
        finally
        {
            _hello?.TrySetResult();
            await dispatcher.InvokeAsync(() =>
            {
                _pending = null;
                if (!_closing && State.Active) Apply(State with { Phase = "Failed", Message = "录制进程已退出", Error = "未完成片段已保留，请打开片段目录检查。" });
            });
        }
    }
    private void Apply(RecordingState state) { State = state; Changed?.Invoke(state); }
    public void Detach()
    {
        _closing = true;
        try { _worker?.StandardInput.Close(); }
        catch (Exception error) when (error is IOException or InvalidOperationException or ObjectDisposedException) { }
    }
    public async Task<bool> StopAndSaveAsync()
    {
        if (!State.Active) return true;
        // Let an already accepted start/pause finish before sending stop.
        var deadline = Stopwatch.StartNew();
        while (_pending is not null || State.Phase is "Starting" or "Pausing")
        {
            if (deadline.Elapsed > TimeSpan.FromSeconds(30)) return false;
            await Task.Delay(100);
        }
        if (!State.Active) return State.Phase == "Saved";
        if (State.Phase != "Saving") await SendAsync("stop");
        while (State.Active)
        {
            if (deadline.Elapsed > TimeSpan.FromMinutes(3)) return false;
            await Task.Delay(100);
        }
        return State.Phase == "Saved";
    }
    public async ValueTask DisposeAsync()
    {
        if (State.Active) await StopAndSaveAsync();
        _closing = true;
        if (_worker is not null)
        {
            if (!_worker.HasExited)
            {
                try
                {
                    await _worker.StandardInput.WriteLineAsync("{\"action\":\"exit\"}");
                    _worker.StandardInput.Close();
                    await _worker.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
                }
                catch (Exception error) when (error is IOException or TimeoutException or InvalidOperationException)
                { if (!_worker.HasExited) _worker.Kill(true); }
            }
            if (_reader is not null) await _reader;
            if (_errors is not null) await _errors;
            _worker.Dispose();
        }
    }
}

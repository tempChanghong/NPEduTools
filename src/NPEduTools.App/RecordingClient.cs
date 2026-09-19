using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows.Threading;
using NPEduTools.Contracts;

namespace NPEduTools.App;

/// <summary>UI client only. Host owns the recorder; no capture process is owned by the WPF dispatcher.</summary>
internal sealed class RecordingClient : IAsyncDisposable
{
    private readonly Dispatcher _dispatcher;
    private readonly string _pipe;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Task _poll;
    private readonly Guid _client = Guid.NewGuid();
    private string? _pending;
    private bool _closing, _sending;
    public RecordingState State { get; private set; } = new("Idle", "准备录制");
    public AutomaticRecordingState Automatic { get; private set; } = new(false, Guid.Empty, "自动录制未启用", null, []);
    public event Action<RecordingState>? Changed;
    public event Action<AutomaticRecordingState>? AutomaticChanged;
    public RecordingClient(Dispatcher dispatcher, string pipe)
    { _dispatcher = dispatcher; _pipe = pipe; _poll = PollAsync(); }
    public static async Task<RecordingEnvironment> ProbeAsync()
    {
        string executable = Path.Combine(AppContext.BaseDirectory, "Recorder", "NPEduTools.Recorder.exe");
        using var process = Process.Start(new ProcessStartInfo(executable, "--probe") { UseShellExecute = false, CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden, RedirectStandardOutput = true, RedirectStandardError = true }) ?? throw new IOException("无法检测录制组件。");
        var output = process.StandardOutput.ReadToEndAsync(); var errors = process.StandardError.ReadToEndAsync();
        try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(12)); }
        catch { if (!process.HasExited) process.Kill(true); throw; }
        await errors;
        if (process.ExitCode != 0) throw new IOException("无法检测录制设备。");
        return JsonSerializer.Deserialize<RecordingEnvironment>(await output, RecordingContract.Json) ?? throw new InvalidDataException();
    }
    private async Task<HostResponse> RequestAsync(string capability, RecorderCommand? command = null, AutomaticRecordingCommand? automatic = null)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token); timeout.CancelAfter(TimeSpan.FromSeconds(18));
        return await HostClient.RequestAsync(_pipe, new HostRequest(Protocol.Version, Guid.NewGuid(), capability, 15000, Recording: command, Automatic: automatic), timeout.Token);
    }
    private async Task PollAsync()
    {
        long leased = 0;
        try
        {
            while (!_lifetime.IsCancellationRequested)
            {
                try
                {
                    var response = await RequestAsync("recording.status");
                    await _dispatcher.InvokeAsync(() => { if (!_closing && !_sending) Apply(response); });
                    // UI synchronization context: a frozen/dead App cannot renew control indefinitely.
                    if (Stopwatch.GetElapsedTime(leased) >= TimeSpan.FromSeconds(2))
                    {
                        await RequestAsync("recording.automatic", automatic: new("lease", _client)); leased = Stopwatch.GetTimestamp();
                    }
                }
                catch (Exception error) when (error is IOException or OperationCanceledException or TimeoutException or JsonException or UnauthorizedAccessException)
                {
                    if (!_closing) await _dispatcher.InvokeAsync(() =>
                        AutomaticChanged?.Invoke(Automatic with { Message = "录制后台连接中断；等待重新连接", Error = "失联期间录制器仍受截止及租约保护。" }));
                }
                await Task.Delay(500, _lifetime.Token);
            }
        }
        catch (OperationCanceledException) { }
    }
    private void Apply(HostResponse response)
    {
        if (response.Automatic is { } auto) { Automatic = auto; AutomaticChanged?.Invoke(auto); }
        if (response.Recording is not { } state) return;
        bool expected = state.Phase is "Failed" or "Saved" || _pending switch
        {
            "start" or "resume" => state.Phase is "Starting" or "Recording",
            "pause" => state.Phase is "Pausing" or "Paused",
            "stop" => state.Phase is "Saving" or "Saved",
            _ => true
        };
        if (!expected) return;
        _pending = null; State = state; Changed?.Invoke(state);
    }
    public async Task SendAsync(string action, RecordingOptions? options = null)
    {
        if (_closing || _sending || _pending is not null || State.Busy && action != "stop") return;
        var expected = State.Control;
        _sending = true; _pending = action;
        State = State with { Phase = action switch { "start" or "resume" => "Starting", "pause" => "Pausing", _ => "Saving" }, Message = "后台正在处理录制操作…" };
        Changed?.Invoke(State);
        try
        {
            var response = await RequestAsync("recording.command", new(action, options, action == "start" ? null : expected, _client));
            if (response.Outcome != "Succeeded") { _pending = null; Apply(response); throw new InvalidOperationException(response.Message); }
            Apply(response);
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        { _pending = null; State = State with { Message = "录制操作未确认，请查看后台状态", Error = error.Message }; Changed?.Invoke(State); }
        finally { _sending = false; }
    }
    public async Task SetAutomaticAsync(string action, RecordingOptions? options = null)
    {
        var response = await RequestAsync("recording.automatic", automatic: new(action, _client, options));
        Apply(response);
        if (response.Outcome != "Succeeded") throw new InvalidOperationException(response.Message);
    }
    public async Task<bool> StopAndSaveAsync()
    {
        await SetAutomaticAsync("disable");
        var deadline = Stopwatch.StartNew();
        while (_pending is not null || State.Phase is "Starting" or "Pausing")
        { if (deadline.Elapsed > TimeSpan.FromSeconds(30)) return false; await Task.Delay(100); }
        if (!State.Active) return State.Phase != "Failed";
        if (State.Phase != "Saving") await SendAsync("stop");
        while (State.Active) { if (deadline.Elapsed > TimeSpan.FromMinutes(3)) return false; await Task.Delay(100); }
        return State.Phase == "Saved";
    }
    public void Detach() { if (_closing) return; _closing = true; _lifetime.Cancel(); }
    public async ValueTask DisposeAsync() { Detach(); await _poll; _lifetime.Dispose(); }
}

using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;
using NPEduTools.Contracts;

namespace NPEduTools.Recorder;

internal sealed class RecordingSession(Action<RecordingState> publish, string? fixtureWindow = null) : IAsyncDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly ProcessJob _job = new();
    private readonly List<string> _parts = [];
    private RecordingState _state = new("Idle", "准备录制");
    private RecordingOptions? _options;
    private RecordingDisplay? _display;
    private string? _directory, _output;
    private int _width, _height;
    private Process? _encoder;
    private AudioInput? _audio;
    private Task? _progress, _errors, _monitor;
    private TaskCompletionSource? _ready;
    private long _frames, _dropped, _completedFrames, _completedDropped, _overruns, _lastProgress;
    private double _seconds, _completedSeconds;
    private string _lastError = "";
    public RecordingState State => Volatile.Read(ref _state);
    public void StartMonitor() => _monitor = MonitorAsync();

    public async Task CommandAsync(RecorderCommand command)
    {
        await _gate.WaitAsync();
        try
        {
            switch (command.Action)
            {
                case "start" when !State.Active:
                    if (command.Options is null) throw new InvalidDataException("缺少录制选项。");
                    await StartAsync(command.Options); break;
                case "pause" when State.Phase == "Recording":
                    SetState("Pausing", "正在保存当前片段…");
                    await EndPartAsync(); SetState("Paused", "已暂停，点击继续录制"); break;
                case "resume" when State.Phase == "Paused":
                    SetState("Starting", "正在恢复录制…");
                    await StartPartAsync(); SetState("Recording", "正在录制"); break;
                case "stop" when State.Active:
                    await FinishAsync(); break;
                case "stop": break;
                default: publish(State with { Message = "当前状态不能执行该操作。" }); break;
            }
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            await AbortPartAsync();
            SetState("Failed", "录制已停止，未完成片段已保留", error.Message);
        }
        finally { _gate.Release(); }
    }
    private async Task StartAsync(RecordingOptions options)
    {
        _directory = _output = null; _parts.Clear();
        _completedFrames = _completedDropped = _overruns = _frames = _dropped = 0; _seconds = _completedSeconds = 0;
        if (RecordingContract.Validate(options) is { } invalid) throw new InvalidDataException(invalid);
        var environment = MediaTools.Probe();
        if (!environment.Ready) throw new FileNotFoundException(environment.Error);
        _display = environment.Displays.FirstOrDefault(d => d.Id == options.Display) ?? throw new InvalidOperationException("所选屏幕已断开，请重新选择。");
        if (fixtureWindow is not null)
        {
            nint window = FindWindow(null, fixtureWindow);
            if (window == 0 || !GetClientRect(window, out var rect)) throw new InvalidOperationException("测试窗口不存在。");
            _display = _display with { Left = 0, Top = 0, Width = rect.Right, Height = rect.Bottom };
        }
        _options = options;
        (_width, _height) = RecordingContract.OutputSize(_display.Width, _display.Height, options.MaximumHeight);
        Directory.CreateDirectory(options.OutputDirectory);
        if (MediaTools.FreeSpace(options.OutputDirectory) < 512L * 1024 * 1024) throw new IOException("可用空间不足 512 MB，请选择其他保存位置。");
        string name = "微课-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N")[..8];
        _output = Path.Combine(Path.GetFullPath(options.OutputDirectory), name + ".mp4");
        _directory = Path.Combine(Path.GetFullPath(options.OutputDirectory), ".npeedutools-sessions", name);
        Directory.CreateDirectory(_directory);
        _parts.Clear(); _completedFrames = _overruns = _frames = _dropped = 0; _seconds = _completedSeconds = 0;
        SetState("Starting", "正在准备屏幕与音频…");
        await StartPartAsync();
        SetState("Recording", "正在录制");
    }
    private async Task StartPartAsync()
    {
        var options = _options!;
        _frames = _dropped = 0; _seconds = 0; _lastError = "";
        _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
        _lastProgress = Stopwatch.GetTimestamp();
        string part = $"part-{_parts.Count + 1:D4}.mkv";
        if (options.SystemAudio || options.Microphone)
        {
            _audio = new(options);
            await _audio.PrepareAsync();
        }
        List<string> arguments = ["-hide_banner", "-loglevel", "warning", "-n", "-stats_period", "0.5", "-progress", "pipe:1", "-filter_threads", "1",
            "-thread_queue_size", "2", "-f", "gdigrab", "-framerate", options.FramesPerSecond.ToString(CultureInfo.InvariantCulture), "-draw_mouse", "1"];
        if (fixtureWindow is null)
            arguments.AddRange(["-offset_x", _display!.Left.ToString(CultureInfo.InvariantCulture), "-offset_y", _display.Top.ToString(CultureInfo.InvariantCulture),
                "-video_size", $"{_display.Width}x{_display.Height}", "-i", "desktop"]);
        else arguments.AddRange(["-i", "title=" + fixtureWindow]);
        if (_audio is not null) arguments.AddRange(["-thread_queue_size", "16", "-f", "f32le", "-ar", "48000", "-ac", "2", "-i", _audio.PipePath]);
        arguments.AddRange(["-map", "0:v:0"]);
        if (_audio is not null) arguments.AddRange(["-map", "1:a:0", "-c:a", "aac", "-b:a", "128k", "-ar", "48000", "-ac", "2"]);
        arguments.AddRange(["-vf", $"scale={_width}:{_height}:flags=fast_bilinear,setsar=1", "-c:v", "libx264", "-preset", "ultrafast", "-tune", "zerolatency",
            "-crf", "23", "-pix_fmt", "yuv420p", "-threads", "2", "-g", (options.FramesPerSecond * 5).ToString(CultureInfo.InvariantCulture),
            "-r", options.FramesPerSecond.ToString(CultureInfo.InvariantCulture), "-fps_mode", "cfr", "-max_muxing_queue_size", "64",
            "-flush_packets", "1", "-cluster_time_limit", "1000", "-f", "matroska", Path.Combine(_directory!, part)]);
        _encoder = Process.Start(MediaTools.StartInfo("ffmpeg", arguments, _directory)) ?? throw new IOException("无法启动录制进程。");
        _job.Add(_encoder);
        _parts.Add(part);
        Persist();
        _progress = ReadProgressAsync(_encoder.StandardOutput);
        _errors = ReadErrorsAsync(_encoder.StandardError, part);
        await _ready.Task.WaitAsync(TimeSpan.FromSeconds(15));
        if (_encoder.HasExited) throw new IOException("编码器提前退出：" + _lastError);
        Persist();
    }
    private async Task ReadProgressAsync(StreamReader reader)
    {
        while (await reader.ReadLineAsync() is { } line)
        {
            int equal = line.IndexOf('=');
            if (equal < 0 || !long.TryParse(line[(equal + 1)..], CultureInfo.InvariantCulture, out long value)) continue;
            switch (line[..equal])
            {
                case "frame":
                    if (value > Interlocked.Read(ref _frames)) Interlocked.Exchange(ref _lastProgress, Stopwatch.GetTimestamp());
                    Interlocked.Exchange(ref _frames, value);
                    if (value > 0) _ready?.TrySetResult();
                    break;
                case "out_time_us": Volatile.Write(ref _seconds, Math.Max(0, value / 1000000.0)); break;
                case "drop_frames": Interlocked.Exchange(ref _dropped, value); break;
            }
        }
        if (_frames == 0) _ready?.TrySetException(new IOException("编码器没有生成画面，请查看片段目录中的诊断日志。"));
    }
    private async Task ReadErrorsAsync(StreamReader reader, string part)
    {
        await using var log = new StreamWriter(Path.Combine(_directory!, part + ".log"), false);
        long characters = 0;
        while (await reader.ReadLineAsync() is { } line)
        {
            _lastError = line[..Math.Min(line.Length, 500)];
            if (characters < 1024 * 1024) { await log.WriteLineAsync(line); characters += line.Length; }
        }
    }
    private async Task EndPartAsync()
    {
        if (_encoder is null) return;
        var encoder = _encoder;
        if (!encoder.HasExited)
        {
            await encoder.StandardInput.WriteLineAsync("q");
            await encoder.StandardInput.FlushAsync();
            await encoder.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));
        }
        if (_progress is not null) await _progress;
        if (_errors is not null) await _errors;
        if (_audio is not null) { _overruns += _audio.Overruns; await _audio.DisposeAsync(); _audio = null; }
        int exit = encoder.ExitCode;
        encoder.Dispose(); _encoder = null;
        if (exit != 0) throw new IOException("编码器异常退出：" + _lastError);
        double duration = await MediaTools.VerifyAsync(Path.Combine(_directory!, _parts[^1]), _options!.SystemAudio || _options.Microphone, _width, _height);
        _completedSeconds += duration; _completedFrames += _frames; _completedDropped += _dropped; _dropped = 0; _frames = 0; _seconds = 0;
        Persist();
    }
    private async Task FinishAsync()
    {
        SetState("Saving", "正在结束录制并保存 MP4，请稍候…");
        await EndPartAsync();
        if (_parts.Count == 0) throw new InvalidDataException("没有可保存的录制片段。");
        if (MediaTools.FreeSpace(_directory!) < _parts.Sum(p => new FileInfo(Path.Combine(_directory!, p)).Length) + 64L * 1024 * 1024)
            throw new IOException("剩余空间不足以合成 MP4，片段已保留，可释放空间后恢复。");
        string list = Path.Combine(_directory!, "parts.txt"), temporary = Path.Combine(_directory!, "final.partial.mp4");
        await File.WriteAllLinesAsync(list, _parts.Select(p => $"file '{p}'"));
        await MediaTools.RunAsync("ffmpeg", ["-hide_banner", "-loglevel", "warning", "-n", "-f", "concat", "-safe", "1", "-i", list,
            "-map", "0", "-c", "copy", "-movflags", "+faststart", temporary], _directory!, TimeSpan.FromMinutes(2));
        _completedSeconds = await MediaTools.VerifyAsync(temporary, _options!.SystemAudio || _options.Microphone, _width, _height);
        File.Move(temporary, _output!);
        SetState("Saved", "已保存，可以打开视频或所在文件夹");
        foreach (var part in _parts)
        {
            try { File.Delete(Path.Combine(_directory!, part)); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        }
    }
    private async Task AbortPartAsync()
    {
        if (_encoder is not null)
        {
            try
            {
                if (!_encoder.HasExited)
                {
                    await _encoder.StandardInput.WriteLineAsync("q");
                    await _encoder.StandardInput.FlushAsync();
                    await _encoder.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
                }
            }
            catch (Exception error) when (error is IOException or InvalidOperationException or TimeoutException)
            { if (!_encoder.HasExited) _encoder.Kill(true); }
            await _encoder.WaitForExitAsync();
            try { if (_progress is not null) await _progress; }
            catch (Exception error) when (error is IOException or InvalidOperationException) { }
            try { if (_errors is not null) await _errors; }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException) { }
            _encoder.Dispose(); _encoder = null;
        }
        if (_audio is not null) { _overruns += _audio.Overruns; await _audio.DisposeAsync(); _audio = null; }
    }
    private async Task MonitorAsync()
    {
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
            while (await timer.WaitForNextTickAsync(_lifetime.Token))
            {
                if (!await _gate.WaitAsync(0)) { publish(State); continue; }
                try
                {
                    if (State.Phase == "Recording")
                    {
                        string? failure = _encoder?.HasExited == true ? "编码进程已退出。" :
                            _audio?.Error is not null ? "音频采集已中断：" + _audio.Error.Message :
                            Stopwatch.GetElapsedTime(Interlocked.Read(ref _lastProgress)) > TimeSpan.FromSeconds(15) ? "连续 15 秒没有新的编码画面。" :
                            MediaTools.FreeSpace(_directory!) < 256L * 1024 * 1024 ? "磁盘剩余空间不足 256 MB。" : null;
                        if (failure is not null) { await AbortPartAsync(); SetState("Failed", "录制已中断，未完成片段已保留", failure); }
                        else SetState("Recording", "正在录制");
                    }
                    else publish(State);
                }
                finally { _gate.Release(); }
            }
        }
        catch (OperationCanceledException) { }
    }
    private void SetState(string phase, string message, string? error = null)
    {
        long bytes = 0;
        try
        {
            bytes = phase == "Saved" ? new FileInfo(_output!).Length : _directory is null ? 0 :
                _parts.Sum(p => File.Exists(Path.Combine(_directory, p)) ? new FileInfo(Path.Combine(_directory, p)).Length : 0);
        }
        catch (IOException) { }
        var state = new RecordingState(phase, message, _completedSeconds + Volatile.Read(ref _seconds), _completedFrames + Interlocked.Read(ref _frames),
            bytes, _completedDropped + Interlocked.Read(ref _dropped), _overruns + (_audio?.Overruns ?? 0), phase == "Saved" ? _output : null, _directory,
            error is null ? null : error[..Math.Min(error.Length, 1000)]);
        Volatile.Write(ref _state, state);
        Persist(); publish(state);
    }
    private void Persist()
    {
        if (_directory is null) return;
        try
        {
            string path = Path.Combine(_directory, "session.json");
            File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(new { version = 1, options = _options, width = _width, height = _height, output = _output, parts = _parts, state = State }, RecordingContract.Json));
            File.Move(path + ".tmp", path, true);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
    }
    public async ValueTask DisposeAsync()
    {
        _lifetime.Cancel();
        if (_monitor is not null) await _monitor;
        if (State.Active) await CommandAsync(new("stop"));
        await AbortPartAsync();
        _job.Dispose(); _lifetime.Dispose(); _gate.Dispose();
    }
    [StructLayout(LayoutKind.Sequential)] private struct Rect { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern nint FindWindow(string? className, string name);
    [DllImport("user32.dll")] private static extern bool GetClientRect(nint window, out Rect rectangle);
}

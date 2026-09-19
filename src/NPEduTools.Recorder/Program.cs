using System.Text.Json;
using System.Diagnostics;
using NPEduTools.Contracts;
using NPEduTools.Recorder;

if (!OperatingSystem.IsWindows()) return 1;
if (args.SequenceEqual(["--probe"]))
{
    Console.WriteLine(JsonSerializer.Serialize(MediaTools.Probe(), RecordingContract.Json));
    return 0;
}
string? fixture = args.Length == 2 && args[0] == "--fixture-window" ? args[1] : null;
string? recovery = args.Length == 2 && args[0] == "--recover" ? args[1] : null;
if (args.Length > 0 && fixture is null && recovery is null) return 2;
using var instance = new Mutex(false, @"Local\NPEduTools.MicroLessonRecorder", out bool first);
if (!first)
{
    Console.WriteLine(JsonSerializer.Serialize(new RecordingState("Failed", "已有微课录制进程", Error: "请先停止已有录制。"), RecordingContract.Json));
    return 3;
}
if (recovery is not null)
{
    try
    {
        string path = await RecordingRecovery.RecoverAsync(recovery);
        Console.WriteLine(JsonSerializer.Serialize(new { outputFile = path }, RecordingContract.Json));
        return 0;
    }
    catch (Exception error) when (error is not OutOfMemoryException)
    {
        Console.Error.WriteLine("恢复失败，原始片段已保留：" + error.Message);
        return 4;
    }
}
object outputGate = new();
var control = new RecorderDeadline();
void Publish(RecordingState state)
{
    lock (outputGate)
    {
        try { Console.WriteLine(JsonSerializer.Serialize(state with { Control = control.Current }, RecordingContract.Json)); }
        catch (IOException) { /* stdin EOF below triggers graceful finalization after the UI disappears. */ }
    }
}
var session = new RecordingSession(Publish, fixture, () => control.Expired(Stopwatch.GetTimestamp()) ? 0 :
    control.Current is { HardDeadline: > 0 } c ? RecorderDeadline.SecondsUntil(c.HardDeadline) : null, () => control.Current);
using var guardLifetime = new CancellationTokenSource();
var guard = Task.Run(async () =>
{
    long expiredAt = 0; Task? finish = null;
    try
    {
        while (!guardLifetime.IsCancellationRequested)
        {
            if (control.Expired(Stopwatch.GetTimestamp()))
            {
                if (expiredAt == 0) expiredAt = Stopwatch.GetTimestamp();
                finish ??= Task.Run(() => session.CommandAsync(new("stop")));
                if (Stopwatch.GetElapsedTime(expiredAt) >= TimeSpan.FromSeconds(2)) session.KillCapture();
                if (Stopwatch.GetElapsedTime(expiredAt) >= TimeSpan.FromMinutes(3)) Environment.Exit(5);
            }
            await Task.Delay(100, guardLifetime.Token);
        }
    }
    catch (OperationCanceledException) { }
});
session.StartMonitor();
Publish(session.State);
try
{
    while (await Console.In.ReadLineAsync() is { } line)
    {
        if (line.Length > 16384) break;
        var command = JsonSerializer.Deserialize<RecorderCommand>(line, RecordingContract.Json);
        if (command is null) continue;
        if (command.Control is { } provided)
        {
            if (command.Action == "start")
            {
                if (session.State.Active || !control.Accept(provided, true, Stopwatch.GetTimestamp())) continue;
            }
            else if (control.Current?.Matches(provided) != true) continue;
            if (command.Action is "lease" or "shorten") { control.Accept(provided, false, Stopwatch.GetTimestamp()); continue; }
        }
        else if (control.Current is not null) continue;
        if (command.Action is "start" or "resume" && control.Expired(Stopwatch.GetTimestamp())) continue;
        if (command.Action == "exit") break;
        await session.CommandAsync(command).WaitAsync(TimeSpan.FromSeconds(command.Action == "stop" ? 180 : 30));
    }
}
catch (Exception error) when (error is IOException or JsonException)
{ Publish(session.State with { Message = "控制连接已断开，正在结束录制。" }); }
catch (TimeoutException) { StopUnresponsiveWorker(); }
// A native audio driver can ignore cancellation. Bound finalization even after
// owner loss; process exit closes the encoder Job and preserves existing fragments.
try { await session.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromMinutes(3)); }
catch (TimeoutException) { StopUnresponsiveWorker(); }
guardLifetime.Cancel(); await guard;
return 0;

void StopUnresponsiveWorker()
{
    Publish(session.State with { Phase = "Failed", Message = "录制操作超时，片段已保留", OutputFile = null,
        Error = "设备或媒体处理长时间没有响应，录制进程已结束。请检查保留片段。" });
    Environment.Exit(5);
}

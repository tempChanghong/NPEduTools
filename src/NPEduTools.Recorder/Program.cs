using System.Text.Json;
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
void Publish(RecordingState state)
{
    lock (outputGate)
    {
        try { Console.WriteLine(JsonSerializer.Serialize(state, RecordingContract.Json)); }
        catch (IOException) { /* stdin EOF below triggers graceful finalization after the UI disappears. */ }
    }
}
var session = new RecordingSession(Publish, fixture);
session.StartMonitor();
Publish(session.State);
try
{
    while (await Console.In.ReadLineAsync() is { } line)
    {
        if (line.Length > 16384) break;
        var command = JsonSerializer.Deserialize<RecorderCommand>(line, RecordingContract.Json);
        if (command is null) continue;
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
return 0;

void StopUnresponsiveWorker()
{
    Publish(session.State with { Phase = "Failed", Message = "录制操作超时，片段已保留", OutputFile = null,
        Error = "设备或媒体处理长时间没有响应，录制进程已结束。请检查保留片段。" });
    Environment.Exit(5);
}

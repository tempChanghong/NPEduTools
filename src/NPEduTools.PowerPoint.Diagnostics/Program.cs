using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;

namespace NPEduTools.PowerPoint.Diagnostics;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        if (!OperatingSystem.IsWindows()) { Console.Error.WriteLine("Windows is required."); return 2; }
        Native.SetProcessDpiAwarenessContext(-4);
        if (args.SequenceEqual(["--com-worker"])) return ProbeSession.Worker();
        if (args.SequenceEqual(["--help"]))
        {
            Console.WriteLine("PowerPoint diagnostic (read-only)\n  --probe\n  --seconds 120 --output FILE.jsonl\nCtrl+C stops observation. No clicks are intercepted and no keys are sent.");
            return 0;
        }
        bool probe = args.SequenceEqual(["--probe"]);
        int seconds = 120;
        string output = Path.Combine(AppContext.BaseDirectory, "diagnostics", $"powerpoint-{DateTime.Now:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.jsonl");
        if (!probe)
        {
            var seen = new HashSet<string>();
            for (int i = 0; i < args.Length; i += 2)
            {
                if (i + 1 >= args.Length || !seen.Add(args[i])) return InvalidArguments();
                if (args[i] == "--seconds" && int.TryParse(args[i + 1], out int value) && value is >= 1 and <= 1800) seconds = value;
                else if (args[i] == "--output" && !string.IsNullOrWhiteSpace(args[i + 1])) output = args[i + 1];
                else return InvalidArguments();
            }
        }
        try { return Run(probe, seconds, Path.GetFullPath(output)); }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            Console.Error.WriteLine($"诊断失败：{error.GetType().Name}。检查输出路径、磁盘和当前用户会话。");
            return 1;
        }
    }

    private static int InvalidArguments() { Console.Error.WriteLine("Invalid arguments. Use --help."); return 2; }

    [SupportedOSPlatform("windows")]
    private static int Run(bool probe, int seconds, string output)
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(probe ? 7 : seconds));
        ConsoleCancelEventHandler cancel = (_, e) => { e.Cancel = true; cancellation.Cancel(); };
        Console.CancelKeyPress += cancel;
        var snapshots = Channel.CreateBounded<ShowSnapshot>(256);
        long droppedSnapshots = 0;
        var session = new ProbeSession();
        Task? monitor = null;
        try
        {
            if (probe)
            {
                monitor = session.RunAsync(s => snapshots.Writer.TryWrite(s), cancellation.Token);
                try
                {
                    var snapshot = snapshots.Reader.ReadAsync(cancellation.Token).AsTask().GetAwaiter().GetResult();
                    Console.WriteLine(JsonSerializer.Serialize(snapshot));
                    return snapshot.Status is "Showing" or "NoSlideShow" or "NotRunning" ? 0 : 3;
                }
                catch (OperationCanceledException) { Console.Error.WriteLine("Probe timed out."); return 3; }
            }
            Directory.CreateDirectory(Path.GetDirectoryName(output)!);
            using var stream = new FileStream(output, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
            using var writer = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true };
            long sequence = 0;
            void Log(string type, object data)
            {
                writer.WriteLine(JsonSerializer.Serialize(new { schemaVersion = 1, sequence = ++sequence,
                    at = DateTimeOffset.UtcNow, monotonicMs = Environment.TickCount64, type, data }));
            }
            Log("environment", new { os = Environment.OSVersion.VersionString, runtime = Environment.Version.ToString(),
                architecture = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString(),
                digitizerFlags = Native.GetSystemMetrics(94), maximumTouches = Native.GetSystemMetrics(95),
                readOnly = true, targetEnvironment = "Office 2024 touch device (user-specified; not auto-verified)",
                limitation = "Compatibility mouse events only; no complete multitouch or interactive-object recognition." });
            using var observer = new InputObserver(session);
            monitor = session.RunAsync(s => { if (!snapshots.Writer.TryWrite(s)) Interlocked.Increment(ref droppedSnapshots); }, cancellation.Token);
            Console.WriteLine($"只读诊断已开始，最多 {seconds} 秒。请在 PowerPoint 中放映并测试轻点、鼠标、拖动和菜单。\n不会辅助翻页。Ctrl+C 结束。\n记录：{output}");
            var tracker = new TapTracker();
            long dropped = 0, callbackErrors = 0, inputCount = 0, candidates = 0;
            string? lastStatus = null;
            bool limitReached = false;
            while (!cancellation.IsCancellationRequested)
            {
                while (snapshots.Reader.TryRead(out var snapshot))
                {
                    Log("show", snapshot);
                    if (snapshot.Status != lastStatus)
                    { Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] {snapshot.Status}，放映窗口 {snapshot.Windows.Length}"); lastStatus = snapshot.Status; }
                }
                if (observer.Dropped != dropped || observer.CallbackErrors != callbackErrors)
                {
                    dropped = observer.Dropped; callbackErrors = observer.CallbackErrors;
                    Log("inputGap", new { dropped, callbackErrors });
                    if (tracker.Cancel("InputGap") is { } cancelled) Log("gesture", cancelled);
                    // Discard the remaining partial sequence after a gap.
                    while (observer.TryRead(out _)) { }
                }
                int batch = 0;
                while (batch++ < 2048 && observer.TryRead(out var input))
                {
                    inputCount++;
                    Log("input", new { sample = input, source = InputSource.Classify(input!.ExtraInfo, input.Flags) });
                    if (tracker.Accept(input) is { } result)
                    {
                        Log("gesture", result);
                        if (result.Outcome == "TouchTapCandidate") candidates++;
                    }
                }
                if (Stopwatch.GetElapsedTime(session.Latest.ReceivedAt).TotalMilliseconds > 750 || session.Latest.Snapshot.Status != "Showing")
                    if (tracker.Cancel("SnapshotUnavailable") is { } cancelled) Log("gesture", cancelled);
                if (stream.Position >= 32 * 1024 * 1024) { limitReached = true; break; }
                Thread.Sleep(10);
            }
            if (tracker.Cancel("SessionEnded") is { } unfinished) Log("gesture", unfinished);
            Log("summary", new { inputCount, touchTapCandidates = candidates, droppedInputs = observer.Dropped,
                callbackErrors = observer.CallbackErrors, droppedSnapshots = Interlocked.Read(ref droppedSnapshots),
                limitReached, pendingInputTailMayBeOmitted = true, targetTouchValidationPassed = false });
            Console.WriteLine($"诊断结束：{inputCount} 个输入事件，{candidates} 个触摸轻点候选。候选不代表可以安全翻页。");
            return 0;
        }
        finally
        {
            cancellation.Cancel();
            if (monitor is not null) monitor.GetAwaiter().GetResult();
            Console.CancelKeyPress -= cancel;
        }
    }
}

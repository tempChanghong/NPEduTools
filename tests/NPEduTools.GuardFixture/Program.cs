using NPEduTools.Core;
using NPEduTools.Contracts;
using System.Text.Json;

// Synthetic recorder output: exercise Host framing without recording or using devices.
if (args.Length == 2 && args[0] == "--fixture-window" && args[1] is
    "recorder-null" or "recorder-oversized" or "recorder-malformed" or "recorder-empty" or
    "recorder-unknown" or "recorder-missing-message" or "recorder-owned-idle" or "recorder-saved" or
    "recorder-ready" or "recorder-failed" or "recorder-finalizing" or "recorder-active-eof" or "recorder-slow-saving" or
    "recorder-invalid-phase" or "recorder-invalid-seconds" or "recorder-invalid-counter")
{
    string? outputDirectory = null;
    Console.WriteLine(args[1] switch
    {
        "recorder-null" => "null",
        "recorder-oversized" => new string('x', 16385),
        "recorder-malformed" => "{broken",
        "recorder-empty" => "{}",
        "recorder-unknown" => "{\"phase\":\"Ready\",\"message\":\"unknown phase\"}",
        "recorder-missing-message" => "{\"phase\":\"Idle\"}",
        "recorder-owned-idle" => JsonSerializer.Serialize(new RecordingState("Idle", "已有其他会话",
            Control: new("Manual", Guid.NewGuid(), "", 0, 0)), RecordingContract.Json),
        "recorder-saved" => JsonSerializer.Serialize(new RecordingState("Saved", "旧会话已保存"), RecordingContract.Json),
        "recorder-failed" => JsonSerializer.Serialize(new RecordingState("Failed", "组件不可用", Error: "fixture-startup-failed"), RecordingContract.Json),
        _ => JsonSerializer.Serialize(new RecordingState("Idle", "准备录制"), RecordingContract.Json)
    });
    while (await Console.In.ReadLineAsync() is { } line)
    {
        if (args[1] is not ("recorder-ready" or "recorder-finalizing" or "recorder-active-eof" or "recorder-slow-saving" or
            "recorder-invalid-phase" or "recorder-invalid-seconds" or "recorder-invalid-counter")) continue;
        var command = JsonSerializer.Deserialize<RecorderCommand>(line, RecordingContract.Json)!;
        if (command.Options is { } options) outputDirectory = options.OutputDirectory;
        if (args[1] == "recorder-active-eof" && command.Action == "lease" && outputDirectory is not null)
            await File.AppendAllTextAsync(Path.Combine(outputDirectory, "leases-after-eof.txt"), "lease\n");
        if (command.Action is "start" or "stop")
        {
            if (args[1] == "recorder-slow-saving" && command.Action == "stop" && outputDirectory is not null)
            {
                await File.AppendAllTextAsync(Path.Combine(outputDirectory, "stop-commands.txt"), "stop\n");
                Console.WriteLine(JsonSerializer.Serialize(new RecordingState("Saving", "synthetic saving", Control: command.Control), RecordingContract.Json));
                var saving = System.Diagnostics.Stopwatch.StartNew();
                while (!File.Exists(Path.Combine(outputDirectory, "release-saving")) && saving.Elapsed < TimeSpan.FromSeconds(30))
                    await Task.Delay(20);
            }
            var state = new RecordingState(command.Action == "start" ? "Recording" : "Saved", command.Action, Control: command.Control);
            if (command.Action == "start") state = args[1] switch
            {
                "recorder-invalid-phase" => state with { Phase = "Unknown" },
                "recorder-invalid-seconds" => state with { Seconds = double.MaxValue },
                "recorder-invalid-counter" => state with { Frames = -1 },
                _ => state
            };
            Console.WriteLine(JsonSerializer.Serialize(state, RecordingContract.Json));
        }
        if (args[1] == "recorder-active-eof" && command.Action == "start")
        {
            // Close only this synthetic worker's redirected stdout handle. Keep
            // stdin open so the test can detect continued leases after status EOF.
            Console.Out.Flush();
            if (!FixtureStandardOutput.CloseHandle(FixtureStandardOutput.GetStdHandle(-11))) return 6;
            Console.SetOut(TextWriter.Null);
        }
    }
    if (args[1] == "recorder-finalizing" && outputDirectory is not null)
    {
        // Keep the synthetic worker alive after stdin EOF until the test releases it.
        // A bounded fallback prevents a failed test from leaving a permanent process.
        var watch = System.Diagnostics.Stopwatch.StartNew();
        while (!File.Exists(Path.Combine(outputDirectory, "release-worker")) && watch.Elapsed < TimeSpan.FromSeconds(30))
            await Task.Delay(20);
    }
    return 0;
}

if (!OperatingSystem.IsWindows() || args.Length < 2 || args[0] != "--pipe" || !args[1].StartsWith("NPEduTools.Test.Guard.", StringComparison.Ordinal)) return 2;
var files = GuardFiles.ForPipe(args[1]);
if (Path.GetFileName(Environment.ProcessPath)!.Equals("NPEduTools.App.exe", StringComparison.OrdinalIgnoreCase))
{
    var r = files.Read<GuardRegistration>("registration.json") ?? new(Guid.NewGuid(), GuardProcess.Current(), null, Path.GetDirectoryName(Environment.ProcessPath!));
    files.Write("registration.json", r with { App = GuardProcess.Current() });
    files.Write("fixture-app-args.json", args);
    var tasks = new GuardTasks(Path.GetDirectoryName(Environment.ProcessPath!)!, args[1], null);
    while (true)
    {
        await Task.Delay(1000);
        if (!files.Stopped(r.Generation) && !files.SessionEnding(r.Generation) && !tasks.GuardRunning())
            try { tasks.Run("Guard"); } catch (Exception e) when (GuardTasks.StartupError(e)) { }
    }
}
else
{
    var tasks = new GuardTasks(Path.GetFullPath(Path.Combine(Path.GetDirectoryName(Environment.ProcessPath!)!, "..")), args[1], null);
    await using var host = new GuardHostSession(files, () => files.Read<string>("fixture-mode.json") != "Standby", ensureGuard: () =>
    { if (OperatingSystem.IsWindows() && !tasks.GuardRunning()) tasks.Run("Guard"); }); // Synthetic scheduled lease; no microphone or network.
    await Task.Delay(Timeout.Infinite);
}
return 0;

internal static class FixtureStandardOutput
{
    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    internal static extern nint GetStdHandle(int handle);
    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    internal static extern bool CloseHandle(nint handle);
}

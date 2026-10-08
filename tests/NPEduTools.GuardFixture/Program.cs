using NPEduTools.Core;
using NPEduTools.Contracts;
using System.Text.Json;

// Synthetic recorder output: exercise Host framing without recording or using devices.
if (args.Length == 2 && args[0] == "--fixture-window" && args[1] is
    "recorder-null" or "recorder-oversized" or "recorder-malformed" or "recorder-empty" or
    "recorder-unknown" or "recorder-missing-message" or "recorder-owned-idle" or "recorder-saved" or
    "recorder-ready" or "recorder-failed" or "recorder-finalizing")
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
        if (args[1] is not ("recorder-ready" or "recorder-finalizing")) continue;
        var command = JsonSerializer.Deserialize<RecorderCommand>(line, RecordingContract.Json)!;
        if (command.Options is { } options) outputDirectory = options.OutputDirectory;
        if (command.Action is "start" or "stop")
            Console.WriteLine(JsonSerializer.Serialize(new RecordingState(
                command.Action == "start" ? "Recording" : "Saved", command.Action, Control: command.Control), RecordingContract.Json));
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
    await Task.Delay(Timeout.Infinite);
}
else
{
    await using var host = new GuardHostSession(files, () => true); // Synthetic scheduled lease; no microphone or network.
    await Task.Delay(Timeout.Infinite);
}
return 0;

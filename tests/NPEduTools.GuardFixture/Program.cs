using NPEduTools.Core;

// Synthetic recorder output: exercise Host framing without recording or using devices.
if (args.Length == 2 && args[0] == "--fixture-window" && args[1] is "recorder-null" or "recorder-oversized" or "recorder-malformed")
{
    Console.WriteLine(args[1] switch { "recorder-null" => "null", "recorder-oversized" => new string('x', 16385), _ => "{broken" });
    await Console.In.ReadLineAsync();
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

using System.Security.Principal;
using System.Text.Json;
using NPEduTools.Integrations.SecRandom;

if (!OperatingSystem.IsWindows() || args.Length is < 1 or > 2)
{
    Console.Error.WriteLine("NPEduTools.SecRandomProbe <SecRandom.Desktop.exe/Launcher.exe> [result.json] (read-only)");
    return 2;
}
string path = WindowsSecRandomTarget.Validate(args[0]);
using var identity = WindowsIdentity.GetCurrent();
using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(8));
var reply = await new SecRandomIpcClient().SendAsync(path, SecRandomAction.Probe, deadline.Token);
var report = new { at = DateTimeOffset.UtcNow, elevated = new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator),
    action = "ReadOnlyProbe", executable = path, reply };
string json = JsonSerializer.Serialize(report, new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true });
Console.WriteLine(json);
if (args.Length == 2) File.WriteAllText(Path.GetFullPath(args[1]), json);
return reply.Succeeded ? 0 : 1;

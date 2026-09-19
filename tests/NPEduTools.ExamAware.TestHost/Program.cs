using System.Runtime.Versioning;
using NPEduTools.Host;

[assembly: SupportedOSPlatform("windows")]
// Test-only adapter for the isolated, un-packaged Electron fixture. Never shipped with the app.
if (args.Length != 6 || args[0] != "--pipe" || args[2] != "--data-dir" || args[4] != "--executable") return 2;
string pipe = args[1], directory = Path.GetFullPath(args[3]), executable = Path.GetFullPath(args[5]);
if (!pipe.StartsWith("NPEduTools.Test.examaware.real.", StringComparison.Ordinal) ||
    !directory.StartsWith(Path.Combine(Path.GetTempPath(), "NPEduTools.ExamAware.Real."), StringComparison.OrdinalIgnoreCase) ||
    !Path.GetFileName(executable).Equals("electron.exe", StringComparison.OrdinalIgnoreCase) || !File.Exists(executable)) return 2;
await using var service = new ExamAwareService(directory, new SourceTarget(executable));
using var lifetime = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; lifetime.Cancel(); };
try { await new PipeServer(pipe, null!, _ => { }, examAware: service).RunAsync(lifetime.Token); }
catch (OperationCanceledException) { }
return 0;

sealed class SourceTarget(string executable) : IExamAwareTarget
{
    public string Validate(string path) => Path.GetFullPath(path) == executable ? executable : throw new ArgumentException("Fixture path mismatch");
    public void Open(string path, string? link) => throw new InvalidOperationException("Source fixture starts its own Electron process");
    public IExamAwareProcess Capture(int processId, string path) => new ExamAwareTarget().Capture(processId, path);
}

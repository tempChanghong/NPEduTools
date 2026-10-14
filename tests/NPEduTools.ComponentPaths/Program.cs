using System.Reflection;
using System.Runtime.Loader;
using System.Security.Cryptography;
using System.Text.Json;

if (args.Length is < 1 or > 2) throw new ArgumentException("Exact package result.json [optional local Host assembly]");
using var result = JsonDocument.Parse(File.ReadAllText(args[0]));
string root = result.RootElement.GetProperty("packageRoot").GetString()!;
using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "package-manifest.json")));
string[] roles = ["app/NPEduTools.App.exe", "app/Host/NPEduTools.Host.exe", "app/Admin/NPEduTools.ClassIsland.Admin.exe",
    "app/Guard/svchost.exe", "app/Recorder/NPEduTools.Recorder.exe", "app/Host/Recorder/NPEduTools.Recorder.exe",
    "app/Host/Camera/NPEduTools.Camera.exe", "app/Recorder/Tools/ffmpeg.exe", "app/Recorder/Tools/ffprobe.exe",
    "app/Host/Recorder/Tools/ffmpeg.exe", "app/Host/Recorder/Tools/ffprobe.exe",
    "app/Assets/Study/Audio/exam-alert.mp3", "app/Assets/Study/Audio/exam-start.mp3", "app/Assets/Study/Audio/exam-end.mp3",
    "app/Host/NPEduTools.Host.dll"];
foreach (string relative in roles)
{
    var entry = manifest.RootElement.GetProperty("files").EnumerateArray().Single(item =>
        item.GetProperty("path").GetString()!.Replace('\\', '/') == relative);
    using var file = File.OpenRead(Path.Combine(root, relative));
    string hash = Convert.ToHexString(SHA256.HashData(file));
    if (!hash.Equals(entry.GetProperty("sha256").GetString(), StringComparison.OrdinalIgnoreCase))
        throw new InvalidDataException("Component missing or hash mismatch: " + relative);
}
// This executable is built into a fresh app/Host fixture. Only marker files are
// written there. No published candidate is modified and no application is started.
string host = Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory);
if (Path.GetFileName(host) != "Host") throw new Exception("Probe must run in isolated app/Host layout");
string app = Path.GetDirectoryName(host)!;
string hostMarker = Path.Combine(host, "NPEduTools.Host.exe");
string appMarker = Path.Combine(app, "NPEduTools.App.exe");
if (File.Exists(hostMarker) || File.Exists(appMarker))
    throw new InvalidOperationException("Probe refuses to overwrite existing application files; use a fresh fixture");
File.WriteAllBytes(hostMarker, []);
File.WriteAllBytes(appMarker, []);
string assemblyPath = args.Length == 2 ? Path.GetFullPath(args[1]) : Path.Combine(root, "app", "Host", "NPEduTools.Host.dll");
var context = new ComponentContext(assemblyPath);
string? helper;
try
{
    // The constructor only stores injected dependencies and computes the helper
    // path. Never call Observe/Set/Run or instantiate a running Host/recorder.
    var type = context.LoadFromAssemblyPath(assemblyPath).GetType("NPEduTools.Host.ClassroomModeEffects", true)!;
    object instance = type.GetConstructors().Single().Invoke([null, null, null, null]);
    helper = (string?)type.GetField("_helper", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(instance);
}
finally { context.Unload(); }
string expected = Path.Combine(app, "Admin", "NPEduTools.ClassIsland.Admin.exe");
bool correct = string.Equals(helper, expected, StringComparison.OrdinalIgnoreCase);
string output = Path.Combine(Path.GetDirectoryName(app)!, "result.json");
File.WriteAllText(output, JsonSerializer.Serialize(new {
    status = correct ? "PASSED" : "FAILED", packageResult = Path.GetFullPath(args[0]),
    packageSource = manifest.RootElement.GetProperty("sourceCommit").GetString(),
    componentsChecked = roles.Length, consumerAssembly = assemblyPath, packagedConsumer = args.Length == 1,
    helper, expected, applicationInstalled = false, capturesStarted = false, taskChanges = false,
    error = correct ? null : "PACKAGED_HOST_ADMIN_PATH_MISMATCH"
}, new JsonSerializerOptions { WriteIndented = true }));
Console.WriteLine("COMPONENT_PATH_TEST_RESULT: " + output);
return correct ? 0 : 1;

sealed class ComponentContext(string path) : AssemblyLoadContext(isCollectible: true)
{
    private readonly AssemblyDependencyResolver _resolver = new(path);
    protected override Assembly? Load(AssemblyName name) => _resolver.ResolveAssemblyToPath(name) is { } resolved
        ? LoadFromAssemblyPath(resolved) : null;
}

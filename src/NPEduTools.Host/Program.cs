using System.Diagnostics;
using System.Reflection;
using NPEduTools.Contracts;
using NPEduTools.Host;
using NPEduTools.Integrations.ClassIsland;

if (!OperatingSystem.IsWindows())
{
    Console.Error.WriteLine("NPEduTools Host requires Windows.");
    return 1;
}

if (args.Contains("--help"))
{
    Console.WriteLine("NPEduTools.Host [--pipe NAME] [--classisland-pipe NAME]\nCtrl+C stops the host. Pipe overrides are for development/testing.");
    return 0;
}

if (args.SequenceEqual(["--powerpoint-worker"]))
{
    var thread = new Thread(() => { if (OperatingSystem.IsWindows()) NPEduTools.PowerPoint.Diagnostics.PowerPointTouchAssist.RunProbeWorker(); });
    thread.SetApartmentState(ApartmentState.STA);
    thread.Start(); thread.Join();
    return 0;
}

bool workerMode = args.Length > 0 && args[0] == "--ipc-worker";
bool scheduleMode = args.Length > 0 && args[0] == "--schedule-worker";
bool monitorMode = args.Length > 0 && args[0] == "--monitor-worker";
var options = new Dictionary<string, string>();
for (int i = workerMode || monitorMode || scheduleMode ? 1 : 0; i < args.Length; i += 2)
{
    if (i + 1 >= args.Length || args[i] is not ("--pipe" or "--classisland-pipe" or "--observe-ms" or "--data-dir") ||
        !options.TryAdd(args[i], args[i + 1]) || args[i + 1].Length is 0 or > 2048)
    {
        Console.Error.WriteLine("Invalid arguments. Use --help.");
        return 2;
    }
}
string pipeName = options.GetValueOrDefault("--pipe", PipeEndpoint.DefaultName);
string classIslandPipe = options.GetValueOrDefault("--classisland-pipe", ClassIslandProbe.DefaultPipeName);
if (pipeName.Length > 200 || classIslandPipe.Length > 200 || pipeName.IndexOfAny(['/', '\\', ':']) >= 0 || classIslandPipe.IndexOfAny(['/', '\\', ':']) >= 0)
{
    Console.Error.WriteLine("Invalid pipe name.");
    return 2;
}

if (monitorMode)
{
    // A renewable stdin lease bounds orphan lifetime without periodically dropping a healthy IPC connection.
    long lastLease = Stopwatch.GetTimestamp();
    using var leaseTimer = new Timer(_ =>
    {
        if (Stopwatch.GetElapsedTime(Interlocked.Read(ref lastLease)) > TimeSpan.FromSeconds(8))
            Environment.Exit(124);
    }, null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
    var leaseReader = new Thread(() =>
    {
        try
        {
            var input = Console.OpenStandardInput();
            while (input.ReadByte() != -1) Interlocked.Exchange(ref lastLease, Stopwatch.GetTimestamp());
        }
        catch (IOException) { }
        Environment.Exit(124);
    }) { IsBackground = true, Name = "Host lease reader" };
    leaseReader.Start();
    var output = Console.OpenStandardOutput();
    Console.SetOut(Console.Error);
    await ClassIslandProbe.MonitorAsync(classIslandPipe,
        result => Protocol.WriteAsync(output, result, CancellationToken.None), CancellationToken.None);
    return 0;
}

if (workerMode || scheduleMode)
{
    if (!int.TryParse(options.GetValueOrDefault("--observe-ms", "0"), out int observeMs) || observeMs is < 0 or > 5000)
        return 2;
    // Bound orphan lifetime even if the Host itself crashes. The worker owns no external mutations.
    _ = Task.Run(async () => { await Task.Delay(TimeSpan.FromSeconds(20)); Environment.Exit(124); });
    var output = Console.OpenStandardOutput();
    Console.SetOut(Console.Error); // Third-party diagnostic output must not corrupt the binary frame.
    var result = scheduleMode ? await ClassIslandScheduleProbe.ReadAsync(classIslandPipe) : await ClassIslandProbe.ReadAsync(classIslandPipe, observeMs);
    await Protocol.WriteAsync(output, result, CancellationToken.None);
    return 0;
}

if (options.ContainsKey("--observe-ms")) return 2;
using var instance = new Mutex(false, $@"Local\{pipeName}.Host", out bool createdNew);
if (!createdNew)
{
    Console.Error.WriteLine("Host is already running for this pipe.");
    return 3;
}

using var shutdown = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; shutdown.Cancel(); };
ProcessStartInfo WorkerStart(bool monitor, int observeMs = 0, bool schedule = false)
{
    var start = new ProcessStartInfo(Environment.ProcessPath!)
    {
        UseShellExecute = false, CreateNoWindow = true,
        RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = monitor
    };
    if (string.Equals(Path.GetFileNameWithoutExtension(Environment.ProcessPath), "dotnet", StringComparison.OrdinalIgnoreCase))
        start.ArgumentList.Add(Assembly.GetExecutingAssembly().Location);
    start.ArgumentList.Add(monitor ? "--monitor-worker" : schedule ? "--schedule-worker" : "--ipc-worker");
    start.ArgumentList.Add("--classisland-pipe");
    start.ArgumentList.Add(classIslandPipe);
    start.ArgumentList.Add("--observe-ms");
    start.ArgumentList.Add(observeMs.ToString(System.Globalization.CultureInfo.InvariantCulture));
    return start;
}
using var reader = new IsolatedStatusReader(query => WorkerStart(false, (int)query.ObservationWindow.TotalMilliseconds, query.IncludeSchedule));
await using var monitor = new StatusMonitor(() => WorkerStart(true), Console.Error.WriteLine);
string dataDirectory = options.GetValueOrDefault("--data-dir", pipeName == PipeEndpoint.DefaultName
    ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NPEduTools", "config")
    : Path.Combine(Path.GetTempPath(), "NPEduTools", "instances",
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(pipeName)))[..24]));
await using var launch = new LaunchService(dataDirectory, new ClassIslandLaunchTarget(), reader);
await using var touch = new TouchAssistService(() =>
{
    var start = new ProcessStartInfo(Environment.ProcessPath!)
    { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true };
    if (Path.GetFileNameWithoutExtension(start.FileName).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
        start.ArgumentList.Add(Assembly.GetExecutingAssembly().Location);
    start.ArgumentList.Add("--powerpoint-worker");
    return start;
});
Console.WriteLine($"Host ready: {pipeName}");
try
{
    await new PipeServer(pipeName, reader, Console.Error.WriteLine, monitor, shutdown.Cancel, launch, touch).RunAsync(shutdown.Token);
    return 0;
}
catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
{
    Console.Error.WriteLine($"Host transport failed: {ex.GetType().Name}");
    return 1;
}

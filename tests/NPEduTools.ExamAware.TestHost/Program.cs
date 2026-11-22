using System.ComponentModel;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Win32.SafeHandles;
using NPEduTools.Contracts;
using NPEduTools.Host;

[assembly: SupportedOSPlatform("windows")]
// Test-only adapter for the isolated, un-packaged Electron fixture. Never shipped with the app.
if (args.Length is not (6 or 8) || args[0] != "--pipe" || args[2] != "--data-dir" || args[4] != "--executable") return 2;
string pipe = args[1], directory = Path.GetFullPath(args[3]), executable = Path.GetFullPath(args[5]);
bool disposable = directory.StartsWith(Path.Combine(Path.GetTempPath(), "NPEduTools.ExamAware.Real."), StringComparison.OrdinalIgnoreCase) ||
    Regex.IsMatch(directory, @"\\\.artifacts\\(?:exam-workbench\\native-fixture-[\w-]+|examaware-real-host)\\NPEduTools\.ExamAware\.Real\.[\w-]+\\host$", RegexOptions.IgnoreCase);
if (!pipe.StartsWith("NPEduTools.Test.examaware.real.", StringComparison.Ordinal) || !disposable ||
    !Path.GetFileName(executable).Equals("electron.exe", StringComparison.OrdinalIgnoreCase) || !File.Exists(executable)) return 2;
string fixtureRoot = Path.GetDirectoryName(directory)!;
string? ownedPlayer = args.Length == 8 ? Path.Combine(fixtureRoot, "player-pid") : null;
await using var service = new ExamAwareService(directory, new SourceTarget(executable, ownedPlayer));
using var lifetime = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; lifetime.Cancel(); };
try
{
    if (args.Length == 6) await new PipeServer(pipe, null!, _ => { }, examAware: service).RunAsync(lifetime.Token);
    else
    {
        string endpoint = Path.GetFullPath(args[7]);
        if (args[6] != "--fixture-tcp" || endpoint != Path.Combine(fixtureRoot, "host-endpoint.json")) return 2;
        await RunFixtureAsync(endpoint, service, lifetime.Token);
    }
}
catch (OperationCanceledException) { }
return 0;

// The loopback fixture has its own secret and supports only the ExamAware test profile.
// It lets the JS renderer harness run on hosts without working Node named-pipe framing.
static async Task RunFixtureAsync(string endpoint, ExamAwareService service, CancellationToken token)
{
    using var listener = new TcpListener(IPAddress.Loopback, 0);
    listener.Start();
    byte[] key = RandomNumberGenerator.GetBytes(32);
    File.WriteAllText(endpoint, JsonSerializer.Serialize(new { port = ((IPEndPoint)listener.LocalEndpoint).Port, key = Convert.ToHexString(key) }));
    while (!token.IsCancellationRequested)
    {
        using var client = await listener.AcceptTcpClientAsync(token);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(4));
        var stream = client.GetStream();
        byte[] supplied = new byte[32];
        try
        {
            await stream.ReadExactlyAsync(supplied, deadline.Token);
            if (!CryptographicOperations.FixedTimeEquals(key, supplied)) continue;
            var request = await Protocol.ReadAsync<HostRequest>(stream, deadline.Token);
            var response = Protocol.Validate(request) is null ? request.Capability switch
            {
                "host.ping" => new HostResponse(1, request.RequestId, "Succeeded", null, "Isolated test Host"),
                "examaware.status" or "examaware.pairing.get" or "examaware.config.set" or "examaware.plan" =>
                    await service.HandleAsync(request, deadline.Token),
                _ => new HostResponse(1, request.RequestId, "Rejected", "FixtureCapability", "Test operation unavailable")
            } : new HostResponse(1, request.RequestId, "Rejected", "FixtureInvalidRequest", "Malformed test request");
            await Protocol.WriteAsync(stream, response, deadline.Token);
        }
        catch (Exception error) when (error is IOException or JsonException or OperationCanceledException) { }
    }
}

sealed class SourceTarget(string executable, string? ownedPlayer) : IExamAwareTarget
{
    public string Validate(string path) => Path.GetFullPath(path) == executable ? executable : throw new ArgumentException("Fixture path mismatch");
    public void Open(string path, string? link) => throw new InvalidOperationException("Source fixture starts its own Electron process");
    public IExamAwareProcess Capture(int processId, string path)
    {
        if (ownedPlayer is null) return new ExamAwareTarget().Capture(processId, path);
        // The Playwright child may deny an all-access handle. This fixture only observes its own process.
        if (!int.TryParse(File.ReadAllText(ownedPlayer), out int expected) || expected != processId)
            throw new InvalidOperationException("The bridge is not the Electron process started by this fixture.");
        var handle = OpenProcess(0x00101000, false, processId); // SYNCHRONIZE | PROCESS_QUERY_LIMITED_INFORMATION
        try
        {
            if (handle.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());
            var image = new StringBuilder(32768);
            int length = image.Capacity;
            if (!QueryFullProcessImageName(handle, 0, image, ref length)) throw new Win32Exception(Marshal.GetLastWin32Error());
            if (!ProcessIdToSessionId(processId, out uint session) || !ProcessIdToSessionId(Environment.ProcessId, out uint localSession))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            if (!string.Equals(image.ToString(), Validate(path), StringComparison.OrdinalIgnoreCase) || session != localSession)
                throw new InvalidOperationException("Unexpected Electron path or session.");
            return new FixtureProcess(handle);
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error);
            handle.Dispose();
            throw;
        }
    }

    private sealed class FixtureProcess(SafeProcessHandle handle) : IExamAwareProcess
    {
        public bool HasExited => WaitForSingleObject(handle, 0) switch
        {
            0 => true,
            258 => false,
            _ => throw new Win32Exception(Marshal.GetLastWin32Error())
        };

        public void Dispose() => handle.Dispose();
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeProcessHandle OpenProcess(uint access, bool inherit, int pid);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool QueryFullProcessImageName(SafeProcessHandle handle, uint flags, StringBuilder path, ref int length);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WaitForSingleObject(SafeProcessHandle handle, uint milliseconds);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool ProcessIdToSessionId(int processId, out uint sessionId);
}

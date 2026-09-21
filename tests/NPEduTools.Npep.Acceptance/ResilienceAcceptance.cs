using System.Diagnostics;
using System.Net.Security;
using System.Reflection;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json.Nodes;
using NPEduTools.Contracts;
using NPEduTools.Integrations.Npep;

// Test-only faults wrap real HTTPS requests. No credentials or request bodies are logged.
// This does not disable the operating system network or emulate Windows sleep/reboot.
internal static class ResilienceAcceptance
{
    private sealed class Faults
    {
        public volatile bool Offline;
        public int DropNextReceipt;
        public long LostSequence;
        public string? LostRequestId;
        public string? LostSessionId;
        public long LastSequence;
        public long LastEpoch;
        public string? LastRequestId;
        public string? LastSessionId;
    }

    private sealed class FaultHandler(HttpMessageHandler inner, Faults faults) : DelegatingHandler(inner)
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            if (faults.Offline) throw new HttpRequestException(HttpRequestError.ConnectionError, "Isolated network fault");
            bool status = request.RequestUri!.AbsolutePath.EndsWith("/device/status", StringComparison.Ordinal);
            var body = status ? NpepProtocol.Parse(await request.Content!.ReadAsByteArrayAsync(token)) : null;
            var response = await base.SendAsync(request, token);
            if (body is not null && response.IsSuccessStatusCode)
            {
                faults.LastSequence = body.Number("sequence");
                faults.LastEpoch = body.Number("statusEpoch");
                faults.LastRequestId = body.Text("requestId");
                faults.LastSessionId = body.Text("sessionId");
                if (Interlocked.Exchange(ref faults.DropNextReceipt, 0) == 1)
                {
                    faults.LostSequence = faults.LastSequence;
                    faults.LostRequestId = faults.LastRequestId;
                    faults.LostSessionId = faults.LastSessionId;
                    response.Dispose();
                    throw new HttpRequestException(HttpRequestError.ConnectionError, "Isolated lost response after server commit");
                }
            }
            return response;
        }
    }

    private static HostResponse Sample(long revision) => new(1, Guid.NewGuid(), "Succeeded", null,
        "isolated resilience fixture", ClassroomMode: new(revision, "Unconfigured"), Recording: new("Idle", "test"));

    private static async Task Wait(Func<bool> condition, int seconds = 45)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(seconds));
        while (!condition()) await Task.Delay(100, deadline.Token);
    }

    public static async Task RunAsync(string directory, string origin, JsonObject fixture,
        Func<HttpMessageHandler> handler, string fixturePath,
        Func<string, string, JsonObject?, Task<JsonObject>> admin, Action<bool, string> check)
    {
        string school = "schools/" + Uri.EscapeDataString(fixture.Text("schoolId"));
        async Task<JsonObject> Entry()
        {
            var list = await admin(school + "/devices", "deviceListResponse", null);
            return ((JsonArray)list["items"]!).OfType<JsonObject>().Single(x =>
                x.Text("state") == "ACTIVE" && x.Text("screenBindingId") == fixture.Text("screenBindingId"));
        }
        var original = await Entry();
        var faults = new Faults { Offline = true };
        long revision = 17;
        await using (var runtime = new NpepRuntime(() => new(directory,
            target => new(target, new FaultHandler(handler(), faults))), "NPEP N1 resilience", () => Sample(Interlocked.Read(ref revision))))
        {
            await Wait(() => runtime.Snapshot().Error == "NETWORK_UNAVAILABLE");
            check(runtime.Snapshot().State == "ACTIVE" && runtime.Snapshot().Connection == "OFFLINE" &&
                runtime.Snapshot().LastReceivedAt is null, "Offline startup retains pairing without claiming an old receipt is current");
            // Let the real server's 60-second online window expire, without changing any clock.
            await Task.Delay(TimeSpan.FromSeconds(65));
            var offline = await Entry();
            check(offline.Text("connectivity") == "OFFLINE" && NpepProtocol.Equal(offline["lastSeenAt"], original["lastSeenAt"]),
                "School marks prolonged outage offline without refreshing lastSeenAt");
            Interlocked.Exchange(ref revision, 18);
            faults.Offline = false;
            await Wait(() => runtime.Snapshot().Connection == "ONLINE", 80);
            var recovered = await Entry();
            check(recovered.Text("deviceId") == original.Text("deviceId") &&
                ((JsonObject)recovered["status"]!).Number("modeRevision") == 18 && recovered.Text("connectivity") == "ONLINE",
                "Network recovery automatically uses the same device and a fresh sample");
            check(faults.LastSequence == 1 && faults.LastEpoch > 1, "Restart begins a newer session with sequence one");
            Interlocked.Exchange(ref revision, 19);
            Interlocked.Exchange(ref faults.DropNextReceipt, 1);
            await Wait(() => runtime.Snapshot().Error == "NETWORK_UNAVAILABLE");
            var committed = await Entry();
            check(((JsonObject)committed["status"]!).Number("modeRevision") == 19,
                "Lost response occurs after the real server accepted the sample");
            Interlocked.Exchange(ref revision, 20);
            await Wait(() => runtime.Snapshot().Connection == "ONLINE");
            var next = await Entry();
            check(faults.LastSequence > faults.LostSequence && faults.LastRequestId != faults.LostRequestId &&
                faults.LastSessionId == faults.LostSessionId && ((JsonObject)next["status"]!).Number("modeRevision") == 20 &&
                DateTimeOffset.Parse(next.Text("lastSeenAt")) > DateTimeOffset.Parse(committed.Text("lastSeenAt")),
                "Lost status receipt retries with a fresh sample, sequence and request ID in the same session");
        }

        // Only this harness's child processes are terminated; no user Host or classroom software is touched.
        JsonObject before = await Entry();
        long previousEpoch = faults.LastEpoch;
        string[] labels = ["Independent process restores the existing pairing", "New process recovers after graceful exit", "New process recovers after abrupt termination"];
        for (int step = 0; step < labels.Length; step++)
        {
            await RunWorker(fixturePath, directory, original.Text("deviceId"), step == 1, async epoch =>
            {
                var after = await Entry();
                check(after.Text("deviceId") == before.Text("deviceId") && epoch > previousEpoch &&
                    after.Text("connectivity") == "ONLINE" && DateTimeOffset.Parse(after.Text("lastSeenAt")) > DateTimeOffset.Parse(before.Text("lastSeenAt")), labels[step]);
                before = after; previousEpoch = epoch;
            });
        }
    }

    private static async Task RunWorker(string fixture, string directory, string deviceId, bool abrupt, Func<long, Task> verify)
    {
        string executable = Environment.ProcessPath ?? throw new InvalidOperationException("Missing process executable");
        var start = new ProcessStartInfo(executable)
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
        if (Path.GetFileNameWithoutExtension(executable).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            start.ArgumentList.Add(Assembly.GetExecutingAssembly().Location);
        foreach (string arg in new[] { "--runtime-worker", Path.GetFullPath(fixture), Path.GetFullPath(directory), deviceId, "isolated-n1" }) start.ArgumentList.Add(arg);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Worker did not start");
        try
        {
            string? line = await process.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(45));
            if (line is null || !line.StartsWith("ONLINE ", StringComparison.Ordinal) || !long.TryParse(line[7..], out long epoch))
                throw new NpepException("WORKER_NOT_ONLINE");
            await verify(epoch);
            if (abrupt) process.Kill();
            else await process.StandardInput.WriteLineAsync("stop");
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));
            if (!abrupt && process.ExitCode != 0) throw new NpepException("WORKER_EXIT_FAILED");
        }
        finally
        {
            if (!process.HasExited) { process.Kill(); await process.WaitForExitAsync(); }
        }
    }

    public static async Task<int> WorkerAsync(string fixturePath, string directory, string expectedDevice, string marker)
    {
        try
        {
            if (marker != "isolated-n1" || !Directory.Exists(directory)) throw new NpepException("ISOLATED_FIXTURE_REQUIRED");
            var fixture = NpepProtocol.Parse(await File.ReadAllBytesAsync(fixturePath));
            string origin = NpepApi.ValidateOrigin(fixture.Text("origin"));
            if (!new Uri(origin).IsLoopback || fixture["enabled"]?.GetValue<bool>() != true) throw new NpepException("ISOLATED_FIXTURE_REQUIRED");
            using var root = X509CertificateLoader.LoadCertificateFromFile(fixture.Text("certificateFile"));
            var observed = new Faults();
            NpepApi Api(string target)
            {
                if (target != origin) throw new NpepException("FIXTURE_ORIGIN_MISMATCH");
                return new(target, new FaultHandler(new SocketsHttpHandler
                {
                    AllowAutoRedirect = false, UseCookies = false,
                    SslOptions = new() { RemoteCertificateValidationCallback = (_, certificate, _, errors) =>
                    {
                        if (certificate is null || (errors & (SslPolicyErrors.RemoteCertificateNameMismatch | SslPolicyErrors.RemoteCertificateNotAvailable)) != 0) return false;
                        using var leaf = new X509Certificate2(certificate); using var chain = new X509Chain();
                        chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
                        chain.ChainPolicy.CustomTrustStore.Add(root); chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
                        return chain.Build(leaf);
                    } }
                }, observed));
            }
            NpepDevice Device()
            {
                var device = new NpepDevice(directory, Api);
                var view = device.View();
                if (view.Text("origin") != origin || view["registration"] is not JsonObject registration || registration.Text("deviceId") != expectedDevice)
                { device.Dispose(); throw new NpepException("ISOLATED_DEVICE_MISMATCH"); }
                return device;
            }
            await using var runtime = new NpepRuntime(Device, "NPEP N1 process acceptance", () => Sample(30));
            await Wait(() => runtime.Snapshot().Connection == "ONLINE");
            Console.WriteLine("ONLINE " + observed.LastEpoch);
            if (await Console.In.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(60)) != "stop") return 2;
            return 0;
        }
        catch { Console.Error.WriteLine("Isolated runtime worker failed; details suppressed."); return 1; }
    }
}

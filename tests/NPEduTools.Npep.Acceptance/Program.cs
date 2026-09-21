using System.Net.Http.Headers;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json.Nodes;
using NPEduTools.Integrations.Npep;

// Explicit opt-in, loopback-only acceptance harness. Never installed with the application.
// A dedicated test CA is trusted ONLY by these handlers; machine/user trust stores are untouched.
if (args.Length == 5 && args[0] == "--runtime-worker")
    return await ResilienceAcceptance.WorkerAsync(args[1], args[2], args[3], args[4]);

if (args.Length is not (4 or 6) || args[0] != "--fixture" || args[2] != "--data-dir" || args.Length == 6 && args[4] != "--pipe")
{
    Console.Error.WriteLine("--fixture <ignored local fixture.json> --data-dir <new isolated directory> [--pipe <isolated Host pipe>]");
    return 2;
}
var checks = new List<string>();
try
{
    var fixture = NpepProtocol.Parse(await File.ReadAllBytesAsync(args[1]));
    string origin = NpepApi.ValidateOrigin(fixture.Text("origin"));
    if (!new Uri(origin).IsLoopback || fixture["enabled"]?.GetValue<bool>() != true) throw new NpepException("ISOLATED_FIXTURE_REQUIRED");
    if (args.Length == 6 && !args[5].StartsWith("NPEduTools.Test.", StringComparison.Ordinal)) throw new NpepException("ISOLATED_HOST_REQUIRED");
    if (Directory.Exists(args[3]) || Directory.Exists(args[3] + "-runtime")) throw new NpepException("FRESH_TEST_DIRECTORY_REQUIRED");
    using var root = X509CertificateLoader.LoadCertificateFromFile(fixture.Text("certificateFile"));
    SocketsHttpHandler Handler() => new()
    {
        AllowAutoRedirect = false, UseCookies = false,
        SslOptions = new()
        {
            RemoteCertificateValidationCallback = (_, certificate, _, errors) =>
            {
                if (certificate is null || (errors & (SslPolicyErrors.RemoteCertificateNameMismatch | SslPolicyErrors.RemoteCertificateNotAvailable)) != 0) return false;
                using var leaf = new X509Certificate2(certificate);
                using var chain = new X509Chain();
                chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
                chain.ChainPolicy.CustomTrustStore.Add(root);
                chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck; // Offline, short-lived isolated CA only.
                return chain.Build(leaf);
            }
        }
    };
    NpepApi Api(string target)
    {
        if (target != origin) throw new NpepException("FIXTURE_ORIGIN_MISMATCH");
        return new(target, Handler());
    }
    using var admin = new HttpClient(Handler()) { BaseAddress = new Uri(origin + "/api/v2/npep/"), Timeout = TimeSpan.FromSeconds(10) };
    admin.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", fixture.Text("adminToken"));
    async Task<JsonObject> Admin(string path, string responseDefinition, JsonObject? body = null)
    {
        string requestId = body?.Text("requestId") ?? NpepProtocol.Id();
        using var request = new HttpRequestMessage(body is null ? HttpMethod.Get : HttpMethod.Post, path);
        request.Headers.Add("X-NPEP-Version", "0.1"); request.Headers.Add("X-Request-Id", requestId);
        if (body is not null) request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        using var reply = await admin.SendAsync(request);
        var envelope = NpepProtocol.Parse(await reply.Content.ReadAsByteArrayAsync());
        if (envelope.Text("requestId") != requestId) throw new NpepException("INVALID_RESPONSE");
        NpepProtocol.Validate(reply.IsSuccessStatusCode ? responseDefinition : "error", envelope);
        if (!reply.IsSuccessStatusCode) throw new NpepException(((JsonObject)envelope["error"]!).Text("code"), (int)reply.StatusCode);
        return (JsonObject)envelope["data"]!;
    }
    void Check(bool success, string label)
    {
        if (!success) throw new NpepException("ACCEPTANCE_CHECK_FAILED:" + label);
        checks.Add(label); Console.WriteLine("PASS " + label);
    }
    string schoolPath = "schools/" + Uri.EscapeDataString(fixture.Text("schoolId"));
    using (var device = new NpepDevice(args[3], Api))
    {
        var info = await device.InspectServerAsync(origin);
        Check(info.Text("serverInstanceId") == fixture.Text("serverInstanceId") && info.Text("deploymentEpoch") == fixture.Text("deploymentEpoch"), "HTTPS identity matches fixture");
        var view = await device.BeginAsync(origin, info, "NPEduTools isolated N1 acceptance", "NPEP N1 development probe");
        var pairing = (JsonObject)view["pairing"]!;
        var resolve = NpepProtocol.Body(); resolve["userCode"] = pairing["userCode"]!.DeepClone();
        var resolved = await Admin(schoolPath + "/pairings/resolve", "resolvedPairingResponse", resolve);
        Check(resolved.Text("pairingId") == pairing.Text("pairingId"), "Admin resolves exact pairing");
        var approve = NpepProtocol.Body(); approve["screenBindingId"] = fixture["screenBindingId"]!.DeepClone(); approve["capabilities"] = new JsonArray("device.status");
        await Admin(schoolPath + "/pairings/" + pairing.Text("pairingId") + "/approve", "approvedPairingResponse", approve);
        view = await device.PollApprovalAsync();
        var approval = (JsonObject)view["approval"]!;
        Check(approval.Text("schoolId") == fixture.Text("schoolId") && approval.Text("administrativeClassId") == fixture.Text("administrativeClassId") && approval.Text("screenBindingId") == fixture.Text("screenBindingId"), "Local approval identity matches isolated binding");
        await device.ConfirmAsync(approval.Text("approvalId"));
        Check(device.View().Text("state") == "ACTIVE", "Device activation succeeds");
    }
    using (var restarted = new NpepDevice(args[3], Api))
    {
        Check(restarted.View().Text("state") == "ACTIVE", "DPAPI credentials survive client restart");
        await restarted.OpenSessionAsync();
        var sample = await NpepStatusReader.ReadAsync(args.Length == 6 ? args[5] : null, "NPEP N1 development probe");
        if (args.Length == 6) Check(sample.Status.Text("mode") != "UNKNOWN" && sample.Status.Text("recording") != "UNKNOWN", "Running isolated Host supplies cached business state");
        var receipt = await restarted.ReportAsync(sample);
        Check(receipt.Number("acceptedSequence") == 1, "Fresh status accepted");
        string deviceId = ((JsonObject)restarted.View()["registration"]!).Text("deviceId");
        var list = await Admin(schoolPath + "/devices", "deviceListResponse");
        var entry = ((JsonArray)list["items"]!).OfType<JsonObject>().Single(d => d.Text("deviceId") == deviceId);
        Check(entry.Text("connectivity") == "ONLINE" && NpepProtocol.Equal(entry["status"], sample.Status), "School sees exact minimal status and ONLINE");
        var revoke = NpepProtocol.Body(); revoke["expectedBindingRevision"] = entry["bindingRevision"]!.DeepClone();
        await Admin(schoolPath + "/devices/" + deviceId + "/revoke", "revokedDeviceResponse", revoke);
        bool denied = false;
        try { await restarted.ReportAsync(sample.Status, 0); }
        catch (NpepException e) when (e.Status is 401 or 403) { denied = true; }
        Check(denied && restarted.View().Text("state") == "SUSPENDED", "Admin revoke invalidates device and suspends client");
        await restarted.UnpairAsync();
        Check(!File.Exists(Path.Combine(args[3], "npep.credentials.dpapi")), "Local credential cleanup completes");
    }
    await RuntimeAcceptance.RunAsync(args[3] + "-runtime", origin, fixture, Api, Handler, args[1], Admin, Check);
    Console.WriteLine($"N1 real HTTPS/PostgreSQL acceptance: {checks.Count} checks passed.");
    return 0;
}
catch (NpepException e) { Console.Error.WriteLine($"FAIL {e.Code} HTTP {e.Status}; completed {checks.Count} checks. Local test state is retained for diagnosis."); return 1; }
catch (Exception e) { Console.Error.WriteLine($"FAIL {e.GetType().Name}; completed {checks.Count} checks. No exception content printed to avoid leaking fixture credentials."); return 1; }

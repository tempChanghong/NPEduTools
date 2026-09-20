using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using NPEduTools.Integrations.Npep;

namespace NPEduTools.Npep.Tests;

internal sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> send) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => send(request);
}
internal sealed class TestDirectory : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "Npep.Tests", Guid.NewGuid().ToString("N"));
    public void Dispose() { if (Directory.Exists(Path)) Directory.Delete(Path, true); }
}
internal static class Fixtures
{
    public static JsonArray Cases => (JsonArray)JsonNode.Parse(File.ReadAllText(System.IO.Path.Combine(AppContext.BaseDirectory, "n1-examples.json")))!["cases"]!;
    public static JsonObject Value(string definition) => (JsonObject)Cases.First(c => c!["valid"]!.GetValue<bool>() && c["definition"]!.GetValue<string>() == definition)!["value"]!.DeepClone();
    public static JsonObject Data(string definition) => (JsonObject)Value(definition)["data"]!.DeepClone();
    public static HttpResponseMessage Reply(HttpRequestMessage request, JsonObject data, string? error = null, int status = 200)
    {
        JsonObject envelope = new() { ["protocolVersion"] = "0.1", ["requestId"] = request.Headers.GetValues("X-Request-Id").Single(), ["serverTime"] = "2026-09-20T12:00:00.000Z" };
        if (error is null) envelope["data"] = data;
        else envelope["error"] = new JsonObject { ["code"] = error, ["message"] = "test", ["retryAfterSeconds"] = null };
        return new((HttpStatusCode)status) { Content = new StringContent(envelope.ToJsonString(), Encoding.UTF8, "application/json") };
    }
}

// Deterministic transport fault injector, NOT a substitute for PostgreSQL transaction acceptance.
internal sealed class FakeServer
{
    public readonly List<(string Path, JsonObject? Body)> Requests = [];
    public JsonObject Info { get; } = Fixtures.Data("infoResponse");
    public JsonObject Approval { get; } = Fixtures.Data("approvedPairingResponse");
    public JsonObject? Create, Confirm, SessionRequest;
    public bool Active, Revoked, LoseConfirmBeforeCommit, LoseConfirmAfterCommit, LoseSession, LoseStatus, FailRevoke, SessionConflict, ChangeEpoch;
    public long Epoch;
    public NpepApi Api(string origin) => new(origin, new Handler(Send));
    public JsonObject Registration()
    {
        var value = Fixtures.Data("registrationResponse");
        value["installationId"] = Create!["installationId"]!.DeepClone();
        value["statusEpoch"] = Epoch;
        if (ChangeEpoch) value["deploymentEpoch"] = NpepProtocol.Id();
        return value;
    }
    public async Task<HttpResponseMessage> Send(HttpRequestMessage request)
    {
        string path = request.RequestUri!.AbsolutePath["/api/v2/npep/".Length..];
        var body = request.Content is null ? null : JsonNode.Parse(await request.Content.ReadAsStringAsync()) as JsonObject;
        Requests.Add((path, body?.Copy()));
        Assert.Equal("0.1", request.Headers.GetValues("X-NPEP-Version").Single());
        if (body is not null) Assert.Equal(body.Text("requestId"), request.Headers.GetValues("X-Request-Id").Single());
        if (path == "info") return Fixtures.Reply(request, Info.Copy());
        if (path == "pairings") { Create = body; return Fixtures.Reply(request, Fixtures.Data("createdPairingResponse")); }
        if (path.StartsWith("pairings/", StringComparison.Ordinal))
        {
            Assert.Equal("npepp1." + Approval.Text("pairingId") + "." + Create!.Text("pairingSecret"), request.Headers.Authorization?.Parameter);
            if (path.EndsWith("/confirm", StringComparison.Ordinal))
            {
                if (Confirm is not null) Assert.True(NpepProtocol.Equal(Confirm, body));
                Confirm = body;
                if (Revoked) return Fixtures.Reply(request, new(), "AUTH_INVALID", 401);
                if (LoseConfirmBeforeCommit) { LoseConfirmBeforeCommit = false; throw new HttpRequestException("test fault"); }
                Active = true;
                if (LoseConfirmAfterCommit) { LoseConfirmAfterCommit = false; throw new HttpRequestException("test fault"); }
                return Fixtures.Reply(request, Registration());
            }
            if (path.EndsWith("/cancel", StringComparison.Ordinal))
            {
                if (Active) return Fixtures.Reply(request, new(), "PAIRING_STATE_CONFLICT", 409);
                return Fixtures.Reply(request, new() { ["pairingId"] = Approval["pairingId"]!.DeepClone(), ["state"] = "CANCELLED" });
            }
            return Fixtures.Reply(request, Approval.Copy());
        }
        if (!Active || Revoked) return Fixtures.Reply(request, new(), "AUTH_INVALID", 401);
        Assert.Equal("npep1." + Confirm!.Text("credentialId") + "." + Confirm!.Text("deviceSecret"), request.Headers.Authorization?.Parameter);
        if (path == "device/me") return Fixtures.Reply(request, Registration());
        if (path == "device/sessions")
        {
            if (SessionConflict) return Fixtures.Reply(request, new(), "SESSION_SUPERSEDED", 409);
            if (SessionRequest is null) { SessionRequest = body; Epoch++; }
            else Assert.True(NpepProtocol.Equal(SessionRequest, body));
            if (LoseSession) { LoseSession = false; throw new HttpRequestException("test fault"); }
            var session = Fixtures.Data("sessionResponse"); session["runId"] = body!["runId"]!.DeepClone(); session["statusEpoch"] = Epoch;
            return Fixtures.Reply(request, session);
        }
        if (path == "device/status")
        {
            if (LoseStatus) { LoseStatus = false; throw new HttpRequestException("test fault"); }
            var receipt = Fixtures.Data("statusReceiptResponse"); receipt["acceptedSequence"] = body!["sequence"]!.DeepClone();
            return Fixtures.Reply(request, receipt);
        }
        if (path == "device/revoke")
        {
            if (FailRevoke) throw new HttpRequestException("test fault");
            Revoked = true; return Fixtures.Reply(request, Fixtures.Data("revokedDeviceResponse"));
        }
        throw new InvalidOperationException("Unexpected route in test");
    }
    public async Task PrepareAsync(NpepDevice device)
    {
        await device.BeginAsync("https://npep.test", Info, "隔离测试屏", "test");
        await device.PollApprovalAsync();
    }
    public async Task ActivateAsync(NpepDevice device)
    {
        await PrepareAsync(device);
        await device.ConfirmAsync(Approval.Text("approvalId"));
    }
}

using System.Security.Cryptography;
using System.Text.Json.Nodes;

namespace NPEduTools.Integrations.Npep;

public sealed class NpepDevice : IDisposable
{
    private readonly NpepVault _vault;
    private readonly Func<string, NpepApi> _factory;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private JsonObject? _saved;
    private JsonObject? _sessionRequest, _session;
    private readonly string _runId = NpepProtocol.Id();
    private long _sequence;
    private bool _suspended;
    public NpepDevice(string directory) : this(directory, origin => new(origin)) { }
    internal NpepDevice(string directory, Func<string, NpepApi> factory)
    {
        _vault = new(directory);
        try { _saved = _vault.Load(); _suspended = _saved?.Text("stage") is "SUSPENDED" or "UNPAIRING"; }
        catch { _vault.Dispose(); throw; }
        _factory = factory;
    }

    // Only this explicitly redacted view can be printed by a caller.
    public JsonObject View() => new()
    {
        ["state"] = _saved?["stage"]?.DeepClone() ?? JsonValue.Create("UNPAIRED"),
        ["origin"] = _saved?["origin"]?.DeepClone(),
        ["pairing"] = _saved?["pairing"]?.DeepClone(),
        ["approval"] = _saved?["approval"]?.DeepClone(),
        ["registration"] = _saved?["registration"]?.DeepClone(),
        ["suspended"] = _suspended
    };
    private void Save(JsonObject next) { _vault.Save(next); _saved = next; }
    private JsonObject Required() => _saved ?? throw new NpepException("NOT_PAIRED");
    private string PairBearer => "npepp1." + ((JsonObject)Required()["pairing"]!).Text("pairingId") + "." + Required().Text("pairingSecret");
    private string DeviceBearer => "npep1." + Required().Text("credentialId") + "." + Required().Text("deviceSecret");
    private NpepApi Api() => _factory(Required().Text("origin"));
    private JsonObject IdentityBody()
    {
        var body = NpepProtocol.Body();
        var info = (JsonObject)Required()["info"]!;
        body["serverInstanceId"] = info["serverInstanceId"]!.DeepClone();
        body["deploymentEpoch"] = info["deploymentEpoch"]!.DeepClone();
        return body;
    }
    private static string Secret() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public async Task<JsonObject> InspectServerAsync(string origin, CancellationToken token = default)
    {
        using var api = _factory(origin);
        return await api.SendAsync("info", "infoResponse", token: token);
    }

    public async Task<JsonObject> BeginAsync(string origin, JsonObject confirmedInfo, string deviceName, string appVersion, CancellationToken token = default)
    {
        await _gate.WaitAsync(token);
        try
        {
            if (_saved is not null) throw new NpepException("PAIRING_ALREADY_EXISTS");
            _suspended = false; _sessionRequest = null; _session = null; _sequence = 0;
            using var api = _factory(origin);
            NpepProtocol.Validate("info", confirmedInfo);
            var actual = await api.SendAsync("info", "infoResponse", token: token);
            if (!NpepProtocol.Equal(actual, confirmedInfo)) throw new NpepException("INSTANCE_MISMATCH");
            string secret = Secret();
            var request = NpepProtocol.Body();
            request["serverInstanceId"] = actual["serverInstanceId"]!.DeepClone();
            request["deploymentEpoch"] = actual["deploymentEpoch"]!.DeepClone();
            request["installationId"] = NpepProtocol.Id();
            request["deviceName"] = deviceName; request["appVersion"] = appVersion;
            request["pairingSecret"] = secret;
            request["requestedCapabilities"] = new JsonArray("device.status");
            NpepProtocol.Validate("createPairing", request);
            Save(new() { ["stage"] = "CREATING", ["origin"] = api.Origin, ["info"] = actual.Copy(),
                ["create"] = request, ["pairingSecret"] = secret });
            return await ResumeCreateCoreAsync(token);
        }
        finally { _gate.Release(); }
    }
    public async Task<JsonObject> ResumeCreateAsync(CancellationToken token = default)
    {
        await _gate.WaitAsync(token);
        try { return await ResumeCreateCoreAsync(token); }
        finally { _gate.Release(); }
    }
    private async Task<JsonObject> ResumeCreateCoreAsync(CancellationToken token)
    {
        if (Required().Text("stage") != "CREATING") throw new NpepException("PAIRING_STATE_CONFLICT");
        using var api = Api();
        var pairing = await api.SendAsync("pairings", "createdPairingResponse", (JsonObject)Required()["create"]!, "createPairing", token: token);
        var next = Required().Copy(); next["pairing"] = pairing; next["stage"] = "PENDING"; Save(next);
        return View();
    }
    public async Task<JsonObject> PollApprovalAsync(CancellationToken token = default)
    {
        await _gate.WaitAsync(token);
        try
        {
            if (Required().Text("stage") is not ("PENDING" or "APPROVED")) throw new NpepException("PAIRING_STATE_CONFLICT");
            using var api = Api();
            string pairingId = ((JsonObject)Required()["pairing"]!).Text("pairingId");
            var response = await api.SendAsync("pairings/" + pairingId, "pairing", bearer: PairBearer, token: token);
            if (response.Text("pairingId") != pairingId) throw new NpepException("INVALID_RESPONSE");
            if (response.Text("state") == "ACTIVATED") throw new NpepException("PAIRING_STATE_CONFLICT");
            if (response.Text("state") == "APPROVED")
            {
                if (_saved?["approval"] is JsonObject approved && !NpepProtocol.Equal(approved, response)) throw new NpepException("BINDING_CHANGED");
                var next = Required().Copy(); next["approval"] = response; next["stage"] = "APPROVED"; Save(next);
            }
            return View();
        }
        finally { _gate.Release(); }
    }
    public async Task<JsonObject> ConfirmAsync(string locallyConfirmedApprovalId, CancellationToken token = default)
    {
        await _gate.WaitAsync(token);
        try
        {
            if (Required().Text("stage") != "APPROVED" || ((JsonObject)Required()["approval"]!).Text("approvalId") != locallyConfirmedApprovalId)
                throw new NpepException("LOCAL_CONFIRMATION_REQUIRED");
            var body = IdentityBody(); body["approvalId"] = locallyConfirmedApprovalId;
            body["credentialId"] = NpepProtocol.Id(); body["deviceSecret"] = Secret();
            var next = Required().Copy(); next["confirm"] = body; next["credentialId"] = body["credentialId"]!.DeepClone();
            next["deviceSecret"] = body["deviceSecret"]!.DeepClone(); next["stage"] = "CONFIRMING";
            Save(next); // Durable candidate BEFORE the first network attempt.
            return await SubmitConfirmCoreAsync(token);
        }
        finally { _gate.Release(); }
    }
    private async Task<JsonObject> SubmitConfirmCoreAsync(CancellationToken token)
    {
        using var api = Api();
        var registered = await api.SendAsync("pairings/" + ((JsonObject)Required()["pairing"]!).Text("pairingId") + "/confirm",
            "registrationResponse", (JsonObject)Required()["confirm"]!, "confirmPairing", PairBearer, token);
        Activate(registered);
        return View();
    }
    public async Task<JsonObject> RecoverConfirmationAsync(CancellationToken token = default)
    {
        await _gate.WaitAsync(token);
        try
        {
            if (Required().Text("stage") != "CONFIRMING") throw new NpepException("PAIRING_STATE_CONFLICT");
            using var api = Api();
            try
            {
                var registered = await api.SendAsync("device/me", "registrationResponse", bearer: DeviceBearer, token: token);
                Activate(registered); return View();
            }
            catch (NpepException e) when (e.Status == 401)
            {
                // A first 401 may race the original transaction. Keep the candidate; only resend identical confirm.
                return await SubmitConfirmCoreAsync(token);
            }
        }
        finally { _gate.Release(); }
    }
    private void CheckRegistration(JsonObject registered)
    {
        var info = (JsonObject)Required()["info"]!;
        var approval = (JsonObject)Required()["approval"]!;
        if (registered.Text("serverInstanceId") != info.Text("serverInstanceId") || registered.Text("deploymentEpoch") != info.Text("deploymentEpoch")) throw new NpepException("INSTANCE_MISMATCH");
        if (registered.Text("installationId") != ((JsonObject)Required()["create"]!).Text("installationId")) throw new NpepException("BINDING_CHANGED");
        foreach (var field in new[] { "schoolId", "administrativeClassId", "screenBindingId", "bindingRevision", "capabilities" })
            if (!NpepProtocol.Equal(registered[field], approval[field])) throw new NpepException("BINDING_CHANGED");
        if (Required()["registration"] is JsonObject prior)
            foreach (var field in new[] { "deviceId", "credentialGeneration", "credentialExpiresAt" })
                if (!NpepProtocol.Equal(registered[field], prior[field])) throw new NpepException("BINDING_CHANGED");
    }
    private void Activate(JsonObject registered)
    {
        CheckRegistration(registered);
        var next = Required().Copy(); next["registration"] = registered; next["stage"] = "ACTIVE";
        next.Remove("pairingSecret"); next.Remove("confirm");
        ((JsonObject)next["create"]!).Remove("pairingSecret");
        Save(next);
    }
    public async Task<JsonObject> OpenSessionAsync(CancellationToken token = default)
    {
        await _gate.WaitAsync(token);
        try
        {
            if (_suspended || Required().Text("stage") != "ACTIVE") throw new NpepException("DEVICE_SUSPENDED");
            if (_session is not null) return _session.Copy();
            using var api = Api();
            if (_sessionRequest is null)
            {
                var registration = await api.SendAsync("device/me", "registrationResponse", bearer: DeviceBearer, token: token);
                CheckRegistration(registration);
                if (registration.Number("statusEpoch") == NpepProtocol.MaxInteger) throw new NpepException("SESSION_EPOCH_EXHAUSTED");
                var body = IdentityBody(); body["runId"] = _runId; body["expectedStatusEpoch"] = registration["statusEpoch"]!.DeepClone();
                _sessionRequest = body;
            }
            var session = await api.SendAsync("device/sessions", "sessionResponse", _sessionRequest, "openSession", DeviceBearer, token);
            if (session.Text("runId") != _runId || session.Number("statusEpoch") != _sessionRequest.Number("expectedStatusEpoch") + 1) throw new NpepException("INVALID_RESPONSE");
            _session = session; return session.Copy();
        }
        catch (NpepException e) { SuspendIfTerminal(e); throw; }
        finally { _gate.Release(); }
    }
    public Task<JsonObject> ReportAsync(NpepSample sample, CancellationToken token = default)
        => ReportCoreAsync(sample.Status.Copy(), () => sample.AgeMs, token);
    internal Task<JsonObject> ReportAsync(JsonObject sampledStatus, int sampleAgeMs, CancellationToken token = default)
        => ReportCoreAsync(sampledStatus.Copy(), () => sampleAgeMs, token);
    private async Task<JsonObject> ReportCoreAsync(JsonObject sampledStatus, Func<int> sampleAgeMs, CancellationToken token)
    {
        await _gate.WaitAsync(token);
        try
        {
            if (_suspended || _session is null || Required().Text("stage") != "ACTIVE") throw new NpepException("DEVICE_SUSPENDED");
            if (_sequence == NpepProtocol.MaxInteger) throw new NpepException("SEQUENCE_EXHAUSTED");
            var body = IdentityBody(); body["sessionId"] = _session["sessionId"]!.DeepClone(); body["statusEpoch"] = _session["statusEpoch"]!.DeepClone();
            int age = sampleAgeMs();
            if (age is < 0 or > 5000) throw new NpepException("SAMPLE_TOO_OLD");
            body["sequence"] = ++_sequence; body["sampleAgeMs"] = age; body["status"] = sampledStatus.Copy();
            using var api = Api();
            var receipt = await api.SendAsync("device/status", "statusReceiptResponse", body, "reportStatus", DeviceBearer, token);
            if (receipt.Number("acceptedSequence") != _sequence) throw new NpepException("INVALID_RESPONSE");
            return receipt;
        }
        catch (NpepException e) { SuspendIfTerminal(e); throw; }
        finally { _gate.Release(); }
    }
    private void SuspendIfTerminal(NpepException error)
    {
        if (error.Status is 401 or 403 or 426 || error.Code is "INSTANCE_MISMATCH" or "BINDING_CHANGED" or "SESSION_SUPERSEDED" or "REVISION_CONFLICT" or "SEQUENCE_CONFLICT" or "STALE_SEQUENCE" or "INVALID_RESPONSE" or "REDIRECT_REJECTED" or "SEQUENCE_EXHAUSTED" or "SESSION_EPOCH_EXHAUSTED")
        {
            _suspended = true;
            var next = Required().Copy(); next["stage"] = "SUSPENDED"; Save(next);
        }
    }
    public async Task<string> UnpairAsync(CancellationToken token = default)
    {
        await _gate.WaitAsync(token);
        try
        {
            _suspended = true; _session = null;
            string outcome = "LOCAL_ONLY";
            if (_saved is not null)
            {
                // A crash during remote cleanup must not resume reporting on the next launch.
                var stopping = Required().Copy(); stopping["stage"] = "UNPAIRING"; Save(stopping);
                try
                {
                    using var api = Api();
                    // Confirmation might have committed even if its response was lost. Revoke that
                    // candidate device rather than assuming cancellation still applies to it.
                    if (_saved["confirm"] is JsonObject)
                    {
                        try
                        {
                            var recovered = await api.SendAsync("device/me", "registrationResponse", bearer: DeviceBearer, token: token);
                            CheckRegistration(recovered);
                            var recoveredState = Required().Copy(); recoveredState["registration"] = recovered;
                            Save(recoveredState);
                        }
                        catch (NpepException e) when (e.Status == 401) { }
                    }
                    if (_saved["registration"] is JsonObject registered)
                    {
                        var body = NpepProtocol.Body(); body["expectedBindingRevision"] = registered["bindingRevision"]!.DeepClone();
                        var reply = await api.SendAsync("device/revoke", "revokedDeviceResponse", body, "revokeDevice", DeviceBearer, token);
                        if (reply.Text("deviceId") != registered.Text("deviceId")) throw new NpepException("INVALID_RESPONSE");
                        outcome = "REVOKED";
                    }
                    else if (_saved["pairing"] is JsonObject pairing)
                    {
                        await api.SendAsync("pairings/" + pairing.Text("pairingId") + "/cancel", "cancelledPairingResponse", NpepProtocol.Body(), "cancelPairing", PairBearer, token);
                        outcome = "CANCELLED";
                    }
                }
                catch (Exception error) when (error is NpepException or HttpRequestException or OperationCanceledException or IOException) { }
            }
            _vault.Clear(); _saved = null;
            return outcome;
        }
        finally { _gate.Release(); }
    }
    public void Dispose() { _vault.Dispose(); _gate.Dispose(); }
}

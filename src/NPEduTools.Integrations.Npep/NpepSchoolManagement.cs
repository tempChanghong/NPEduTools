using System.Text.Json.Nodes;

namespace NPEduTools.Integrations.Npep;

public interface INpepSchoolManagement
{
    void BindSchool(string? scope);
    // The durable pairing identity is available before the first online school poll.
    void ObserveActionBinding(string? scope) { }
    void ObserveWebsiteBinding(string? scope) { }
    void ConfirmSchoolBindingRevoked(string scope) { }
    bool SupportsWebsitePolicyAutomation => false;
    bool SupportsWebsitePolicyTiming => false;
    void ApplySchool(JsonObject policy);
    void ApplySchoolFrom(JsonObject policy, string origin) => ApplySchool(policy);
    JsonObject ObserveSchool();
}

public sealed partial class NpepApi
{
    internal async Task<JsonObject> SchoolManagementAsync(string? bearer, string path, JsonObject? body, CancellationToken token,
        bool websitePolicyAutomation = false, bool websitePolicyTiming = false)
    {
        bool replacement = path == "pairings/replacement";
        if(!replacement && path is not ("admission/catalog" or "admission/catalog/lookup" or "device/school-management" or "device/school-management/receipt")) throw new NpepException("INVALID_REQUEST");
        string requestId = body?.Text("requestId") ?? NpepProtocol.Id();
        using var request = new HttpRequestMessage(body is null ? HttpMethod.Get : HttpMethod.Post, path);
        request.Headers.Add("X-NPEP-Version", "0.16"); request.Headers.Add("X-Request-Id", requestId);
        if (path == "device/school-management" && body is null)
        {
            request.Headers.Add("X-NPEP-Classroom-Action-Policy", "2");
            request.Headers.Add("X-NPEP-Classroom-Action-Inheritance", "1");
            request.Headers.Add("X-NPEP-Manual-Study-Policy", "1");
            request.Headers.Add("X-NPEP-Manual-Recording-Policy", "1");
            request.Headers.Add("X-NPEP-Website-Policy", "1");
            if (websitePolicyAutomation) request.Headers.Add("X-NPEP-Website-Policy-Automation", "1");
            if (websitePolicyTiming) request.Headers.Add("X-NPEP-Website-Policy-Timing", "1");
        }
        if(bearer is not null) request.Headers.Authorization = new("Bearer", bearer);
        if(body is not null)
        {
            var bytes = System.Text.Encoding.UTF8.GetBytes(body.ToJsonString());
            if(bytes.Length > 65536) throw new NpepException("PAYLOAD_TOO_LARGE");
            request.Content = new ByteArrayContent(bytes); request.Content.Headers.ContentType = new("application/json");
        }
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token); deadline.CancelAfter(TimeSpan.FromSeconds(8));
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
        int code = (int)response.StatusCode;
        if(code is >= 300 and < 400) throw new NpepException("REDIRECT_REJECTED");
        if(replacement && code == 426) throw new NpepException("REPLACEMENT_UNSUPPORTED", code);
        if(!replacement && code is (404 or 426))throw new NpepException("SCHOOL_MANAGEMENT_UNSUPPORTED",code);
        // Older servers return up to 200 full rows. Trim that preview before sending it to the UI.
        JsonObject envelope;
        try { envelope = await ReadEnvelopeAsync(response, path == "admission/catalog" ? 2 * 1024 * 1024 : 65536, deadline.Token, request); }
        catch (NpepException e) when (replacement && code == 404 && e.Code == "INVALID_RESPONSE")
        { throw new NpepException("REPLACEMENT_UNSUPPORTED", code); }
        if(envelope.Text("protocolVersion") != "0.16" || envelope.Text("requestId") != requestId) throw new NpepException("INVALID_RESPONSE");
        if(!response.IsSuccessStatusCode)
        {
            var error = envelope["error"]?["code"]?.GetValue<string>() ?? "INVALID_RESPONSE";
            throw new NpepException(System.Text.RegularExpressions.Regex.IsMatch(error,"^[A-Z0-9_]{1,100}$") ? error : "INVALID_RESPONSE",code);
        }
        if(envelope.Count != 4 || envelope["data"] is not JsonObject data || !envelope.ContainsKey("serverTime")) throw new NpepException("INVALID_RESPONSE");
        return data.Copy();
    }
}

public sealed partial class NpepDevice
{
    public Task<JsonObject> BeginClassAsync(string origin,JsonObject info,string name,string version,string binding,string? secret,CancellationToken token=default)
        => BeginCoreAsync(origin,info,name,version,null,token,binding,secret);
    internal Task<JsonObject> BeginClassAsync(string origin, JsonObject info, string name, string version, string binding,
        string? secret, CancellationToken token, string? school, string? room, string? screenName = null) =>
        BeginCoreAsync(origin, info, name, version, null, token, binding, secret, school, room, screenName);
    internal async Task ConfirmRequestedClassAsync(CancellationToken token)
    {
        if(_saved?.Text("stage")!="APPROVED" || _saved["source"]?.GetValue<string>() is not ("CLASS_APPLY" or "CLASS_PASSWORD"))return;
        var approval=_saved["approval"]!.AsObject();
        await ConfirmAsync(approval.Text("approvalId"),token);
    }
    internal async Task<(JsonObject Data, string Origin)> SchoolManagementAsync(JsonObject context, JsonObject? receipt, CancellationToken token,
        bool websitePolicyAutomation = false, bool websitePolicyTiming = false)
    {
        JsonObject pairing, session;
        NpepApi api;
        string bearer;
        await _gate.WaitAsync(token);
        try
        {
            RequireNotificationConnection();
            pairing = Required();
            if (_session is not { } active || active.Text("sessionId") != context.Text("sessionId") ||
                !JsonNode.DeepEquals(RegistrationIdentity(pairing["registration"]!.AsObject()), context["identity"]))
                throw new NpepException("SESSION_SUPERSEDED");
            session = active;
            bearer = DeviceBearer;
            api = Api();
        }
        finally { _gate.Release(); }

        // This endpoint has its own polling lane. Slow school-management HTTP must not hold the device gate.
        using (api)
        {
            JsonObject result;
            try
            {
                result = await api.SchoolManagementAsync(bearer, receipt is null ? "device/school-management" : "device/school-management/receipt",
                    receipt, token, websitePolicyAutomation, websitePolicyTiming);
            }
            catch (NpepException error) when (error.Status == 401)
            {
                await _gate.WaitAsync(token);
                try
                {
                    // A late rejection for the previous connection cannot suspend the replacement.
                    if (ReferenceEquals(_saved, pairing) && ReferenceEquals(_session, session)) SuspendIfTerminal(error);
                }
                finally { _gate.Release(); }
                throw;
            }
            await _gate.WaitAsync(token);
            try
            {
                RequireNotificationConnection();
                if (!ReferenceEquals(_saved, pairing) || !ReferenceEquals(_session, session)) throw new NpepException("SESSION_SUPERSEDED");
                return (result, api.Origin);
            }
            finally { _gate.Release(); }
        }
    }
}

public sealed partial class NpepRuntime
{
    private Task _schoolManagementLoop = Task.CompletedTask;
    private INpepSchoolManagement? _schoolManagement;
    private long _schoolSequence;
    private readonly object _actionBindingSync = new();
    private string? _observedActionScope;
    private bool _observedActionBinding;
    private string? _observedWebsiteScope;
    private bool _observedWebsiteBinding;

    private void InitializeSchoolManagement()
    {
        if (_schoolManagement is null) return;
        _schoolManagementLoop = Task.Run(async () =>
        {
            try
            {
                while (!_lifetime.IsCancellationRequested)
                {
                    if (Snapshot() is { State: "ACTIVE", Connection: "ONLINE", ReportingPaused: false, Busy: false })
                    {
                        try { await SchoolManagementCycleAsync(_lifetime.Token); }
                        catch (Exception e) when (Handled(e) || e is InvalidDataException) { /* Keep the durable school credential on transport loss. */ }
                    }
                    await Task.Delay(TimeSpan.FromSeconds(3), _lifetime.Token);
                }
            }
            catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        });
    }

    private void SynchronizeActionBinding()
    {
        if (_schoolManagement is null) return;
        // Do not wait on the capture or persistence lock while holding the runtime lock.
        lock (_actionBindingSync)
        {
            var view = _device?.View();
            string? scope = view?["state"]?.GetValue<string>() == "ACTIVE" &&
                view["suspended"]?.GetValue<bool>() != true && view["registration"] is JsonObject registration
                ? NpepDevice.RegistrationIdentity(registration).ToJsonString() : null;
            // A damaged or temporarily locked credentials file cannot prove an unpair.
            if (view is not null && view["pairingError"] is null && (!_observedWebsiteBinding || _observedWebsiteScope != scope))
            {
                _schoolManagement.ObserveWebsiteBinding(scope);
                _observedWebsiteScope = scope;
                _observedWebsiteBinding = true;
            }
            if (_observedActionBinding && _observedActionScope == scope) return;
            _schoolManagement.ObserveActionBinding(scope);
            _observedActionScope = scope;
            _observedActionBinding = true;
        }
    }
    internal async Task SchoolManagementCycleAsync(CancellationToken token)
    {
        if(_schoolManagement is null) return;
        var context=await StudyContextAsync(token);
        string scope=context["identity"]!.ToJsonString(); _schoolManagement.BindSchool(scope);
        var (policy, origin)=await _device!.SchoolManagementAsync(context,null,token,_schoolManagement.SupportsWebsitePolicyAutomation,
            _schoolManagement.SupportsWebsitePolicyTiming);
        await RequireStudyContextAsync(context,token);
        if(!JsonNode.DeepEquals(context["identity"],policy["identity"]))throw new NpepException("SESSION_SUPERSEDED");
        _schoolManagement.ApplySchoolFrom(policy, origin);
        var body=_schoolManagement.ObserveSchool();body["requestId"]=NpepProtocol.Id();body["context"]=context.Copy();body["sequence"]=++_schoolSequence;
        var (reply, _)=await _device.SchoolManagementAsync(context,body,token);
        await RequireStudyContextAsync(context,token);
        if(reply["confirmed"]?.GetValue<bool>() != true)throw new NpepException("INVALID_RESPONSE");
    }
}

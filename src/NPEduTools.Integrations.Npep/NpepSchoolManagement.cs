using System.Text.Json.Nodes;

namespace NPEduTools.Integrations.Npep;

public interface INpepSchoolManagement
{
    void BindSchool(string? scope);
    void ApplySchool(JsonObject policy);
    JsonObject ObserveSchool();
}

public sealed partial class NpepApi
{
    internal async Task<JsonObject> SchoolManagementAsync(string? bearer, string path, JsonObject? body, CancellationToken token)
    {
        if(path is not ("admission/catalog" or "device/school-management" or "device/school-management/receipt")) throw new NpepException("INVALID_REQUEST");
        string requestId = body?.Text("requestId") ?? NpepProtocol.Id();
        using var request = new HttpRequestMessage(body is null ? HttpMethod.Get : HttpMethod.Post, path);
        request.Headers.Add("X-NPEP-Version", "0.16"); request.Headers.Add("X-Request-Id", requestId);
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
        if(code is 404 or 426)throw new NpepException("SCHOOL_MANAGEMENT_UNSUPPORTED",code);
        var envelope = await ReadEnvelopeAsync(response, 65536, deadline.Token, request);
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
    internal async Task ConfirmRequestedClassAsync(CancellationToken token)
    {
        if(_saved?.Text("stage")!="APPROVED" || _saved["source"]?.GetValue<string>() is not ("CLASS_APPLY" or "CLASS_PASSWORD"))return;
        var approval=_saved["approval"]!.AsObject();
        if(approval.Text("screenBindingId")!=_saved.Text("requestedBindingId"))throw new NpepException("BINDING_CHANGED");
        await ConfirmAsync(approval.Text("approvalId"),token);
    }
    public async Task<JsonObject> ClassCatalogueAsync(string origin,CancellationToken token=default)
    { using var api = Api(origin); return await api.SchoolManagementAsync(null,"admission/catalog",null,token); }
    internal async Task<JsonObject> SchoolManagementAsync(JsonObject? receipt,CancellationToken token)
    {
        await _gate.WaitAsync(token);
        try { RequireNotificationConnection(); using var api=Api();return await api.SchoolManagementAsync(DeviceBearer,receipt is null?"device/school-management":"device/school-management/receipt",receipt,token); }
        catch(NpepException error){if(error.Status==401)SuspendIfTerminal(error);throw;}
        finally{_gate.Release();}
    }
}

public sealed partial class NpepRuntime
{
    private INpepSchoolManagement? _schoolManagement;
    private long _schoolSequence;
    private async Task SchoolManagementCycleAsync(CancellationToken token)
    {
        if(_schoolManagement is null) return;
        var context=await StudyContextAsync(token);
        string scope=context["identity"]!.ToJsonString(); _schoolManagement.BindSchool(scope);
        var policy=await _device!.SchoolManagementAsync(null,token);
        await RequireStudyContextAsync(context,token);
        if(!JsonNode.DeepEquals(context["identity"],policy["identity"]))throw new NpepException("SESSION_SUPERSEDED");
        _schoolManagement.ApplySchool(policy);
        var body=_schoolManagement.ObserveSchool();body["requestId"]=NpepProtocol.Id();body["context"]=context.Copy();body["sequence"]=++_schoolSequence;
        var reply=await _device.SchoolManagementAsync(body,token);
        await RequireStudyContextAsync(context,token);
        if(reply["confirmed"]?.GetValue<bool>() != true)throw new NpepException("INVALID_RESPONSE");
    }
}

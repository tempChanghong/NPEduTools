using System.Text.Json.Nodes;
namespace NPEduTools.Integrations.Npep;

public sealed record NpepSeatingUploadScope(JsonObject Context, JsonObject State);
public sealed partial class NpepRuntime
{
    public async Task<NpepSeatingUploadScope> PrepareSeatingUploadAsync(string version, CancellationToken token)
    {
        if (Snapshot() is not { State: "ACTIVE", Connection: "ONLINE", ReportingPaused: false, Busy: false }) throw new NpepException("SESSION_SUPERSEDED");
        var context = await StudyContextAsync(token);
        var body = new JsonObject { ["requestId"] = NpepProtocol.Id(), ["context"] = context.Copy(), ["version"] = version };
        var state = await _device!.SeatingWriteAsync(body, false, token);
        await RequireStudyContextAsync(context, token);
        if (state.Text("version") != version) throw new NpepException("POLICY_CHANGED");
        return new(context.Copy(), state);
    }
    public async Task<JsonObject> PublishSeatingAsync(NpepSeatingUploadScope scope, Guid requestId, int revision, byte[] png, CancellationToken token)
    {
        await RequireStudyContextAsync(scope.Context, token);
        var body = new JsonObject { ["requestId"] = requestId.ToString(), ["context"] = scope.Context.Copy(),
            ["version"] = scope.State.Text("version"), ["expectedRevision"] = revision, ["image"] = Convert.ToBase64String(png) };
        var result = await _device!.SeatingWriteAsync(body, true, token);
        await RequireStudyContextAsync(scope.Context, token);
        if (result.Text("version") != scope.State.Text("version") || result.Text("classId") != scope.State.Text("classId")) throw new NpepException("POLICY_CHANGED");
        return result;
    }
}
public sealed partial class NpepApi
{
    internal Task<JsonObject> SeatingWriteAsync(string bearer, JsonObject body, bool publish, CancellationToken token)
    {
        NpepSeatingProtocol.Validate(publish ? "uploadRequest" : "uploadStatusRequest", body);
        // Only this explicit image endpoint admits larger requests. Old HTTP/Pipe limits stay bounded.
        return StudyAsync("device/study-settings/seating/" + (publish ? "upload" : "upload-status"), "uploadResult",
            bearer, body, token, "0.14", requestLimit: publish ? 2850000 : 65536);
    }
}
public sealed partial class NpepDevice
{
    internal async Task<JsonObject> SeatingWriteAsync(JsonObject body, bool publish, CancellationToken token)
    {
        NpepApi api; string bearer;
        await _gate.WaitAsync(token);
        try { RequireNotificationConnection(); api = Api(); bearer = DeviceBearer; }
        finally { _gate.Release(); }
        // An image transfer must not hold up the policy/execution/presentation polling gate.
        using (api)
        {
            try { return await api.SeatingWriteAsync(bearer, body, publish, token); }
            catch (NpepException e) when (e.Status == 401)
            {
                await _gate.WaitAsync(token);
                try { if (DeviceBearer == bearer) SuspendIfTerminal(e); }
                finally { _gate.Release(); }
                throw;
            }
        }
    }
}

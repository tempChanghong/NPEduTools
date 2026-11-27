using System.Text.Json.Nodes;
using NPEduTools.Contracts;
namespace NPEduTools.Integrations.Npep;
public interface INpepStudyExitEvents
{
    bool SupportsExitEvents=>true;
    void BindExit(string? scope);
    StudyExitPacket? PendingExit();
    void ExitStored(Guid eventId);
    void ExitFailed(string code,Guid? eventId=null);
}
public sealed partial class NpepRuntime
{
    private Task _studyExitLoop = Task.CompletedTask;
    private void InitializeStudyExit(INpepStudySettings study)
    {
        if (study is not INpepStudyExitEvents { SupportsExitEvents: true } events || _device is null) return;
        _studyExitLoop = Task.Run(async () =>
        {
            StudyRetry? retry = null;
            try
            {
                while (!_lifetime.IsCancellationRequested)
                {
                    var state = Snapshot();
                    Guid? eventId = null;
                    string? scope = null;
                    try
                    {
                        if (state is { State: "UNPAIRED" or "SUSPENDED" })
                        {
                            lock (_sync)
                                if (_state.Revision == state.Revision) events.ExitFailed("EXIT_NOT_ELIGIBLE");
                        }
                        else if (state is { State: "ACTIVE", Connection: "ONLINE", ReportingPaused: false, Busy: false } &&
                            events.PendingExit() is { } packet && !StudyRetryPending(retry, packet.Scope))
                        {
                            scope = packet.Scope;
                            eventId = Guid.Parse(packet.Metadata.Text("eventId"));
                            await SendStudyExitAsync(events, packet, eventId.Value, state.Revision, _lifetime.Token);
                            retry = null;
                        }
                    }
                    catch (Exception e) when (!_lifetime.IsCancellationRequested && (Handled(e) || e is InvalidDataException or FormatException))
                    {
                        if (scope is not null && e is NpepException error)
                        {
                            if (error.Code is "STUDY_PHOTOS_UNSUPPORTED" or "PROTOCOL_UNSUPPORTED" or "NOT_FOUND")
                                retry = new(scope, _receiptTime.GetTimestamp(), TimeSpan.FromSeconds(60));
                            else if (error.Status is 429 or >= 500) retry = NewStudyRetry(scope, error);
                        }
                        lock (_sync)
                            if (_state.Revision == state.Revision)
                                events.ExitFailed(e is NpepException n ? n.Code : "EXIT_EVENT_OFFLINE", eventId);
                        if (e is NpepException { Status: 401 } refusal && Snapshot().Revision == state.Revision)
                            Publish("STOPPED", "设备授权已失效，退出事件同步已停止。", refusal.Code, requireSuspended: true);
                    }
                    await Task.Delay(TimeSpan.FromSeconds(3), _lifetime.Token);
                }
            }
            catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        });
    }
    private async Task SendStudyExitAsync(INpepStudyExitEvents events, StudyExitPacket packet, Guid eventId, long revision, CancellationToken token)
    {
        var context = await StudyContextAsync(token);
        void RequireOriginalConnection()
        {
            token.ThrowIfCancellationRequested();
            if (Snapshot() is not { State: "ACTIVE", Connection: "ONLINE", ReportingPaused: false, Busy: false } state || state.Revision != revision)
                throw new NpepException("SESSION_SUPERSEDED");
        }
        RequireOriginalConnection();
        if (!JsonNode.DeepEquals(JsonNode.Parse(packet.Scope), context["identity"])) throw new NpepException("BINDING_CHANGED");
        var body = new JsonObject { ["requestId"] = NpepProtocol.Id(), ["context"] = context.Copy(), ["event"] = packet.Metadata.Copy() };
        var reply = await _device!.StudyExitAsync(body, RequireOriginalConnection, token);
        if (reply.Text("eventId") != eventId.ToString()) throw new NpepException("INVALID_RESPONSE");
        lock (_sync)
        {
            RequireOriginalConnection();
            events.ExitStored(eventId);
        }
    }
}
public sealed partial class NpepApi
{
    internal Task<JsonObject> ExitEventAsync(string bearer,JsonObject body,CancellationToken token)
    {NpepPhotoProtocol.Validate("exitRequest",body);return PhotoAsync("exit-events","exitReply",bearer,body.Text("requestId"),System.Text.Encoding.UTF8.GetBytes(body.ToJsonString()),null,token);}
}
public sealed partial class NpepDevice
{
    internal Task<JsonObject> StudyExitAsync(JsonObject body, Action requireOriginalConnection, CancellationToken token) =>
        WithBoundSchoolConnectionAsync(token, (api, bearer, _) =>
        {
            requireOriginalConnection();
            return api.ExitEventAsync(bearer, body, token);
        }, studyContext: body["context"]?.AsObject() ?? throw new NpepException("INVALID_REQUEST"));
}

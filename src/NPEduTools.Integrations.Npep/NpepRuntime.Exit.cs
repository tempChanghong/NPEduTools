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
    private Task _studyExitLoop=Task.CompletedTask;
    private void InitializeStudyExit(INpepStudySettings study)
    {
        if(study is not INpepStudyExitEvents {SupportsExitEvents:true} events || _device is null)return;
        _studyExitLoop=Task.Run(async()=>{
            long? unsupportedAt=null;
            try{while(!_lifetime.IsCancellationRequested){
                Guid? id=null;
                try {
                    var state=Snapshot();
                    if(state is {State:"UNPAIRED" or "SUSPENDED"} or {ReportingPaused:true} || state.Busy && !ContinuingStudyResume)events.ExitFailed("EXIT_NOT_ELIGIBLE");
                    else if((unsupportedAt is null || System.Diagnostics.Stopwatch.GetElapsedTime(unsupportedAt.Value).TotalSeconds>=60) && state is {State:"ACTIVE",Connection:"ONLINE",Busy:false} && events.PendingExit() is {} packet){
                        id=Guid.Parse(packet.Metadata.Text("eventId"));var context=await StudyContextAsync(_lifetime.Token);
                        if(!JsonNode.DeepEquals(JsonNode.Parse(packet.Scope),context["identity"]))throw new NpepException("BINDING_CHANGED");
                        var body=new JsonObject{["requestId"]=NpepProtocol.Id(),["context"]=context.Copy(),["event"]=packet.Metadata.Copy()};
                        var reply=await _device.StudyExitAsync(body,_lifetime.Token);await RequireStudyContextAsync(context,_lifetime.Token);
                        if(reply.Text("eventId")!=id.Value.ToString())throw new NpepException("INVALID_RESPONSE");unsupportedAt=null;events.ExitStored(id.Value);
                    }
                }catch(Exception e)when(Handled(e)||e is InvalidDataException){if(e is NpepException {Code:"STUDY_PHOTOS_UNSUPPORTED" or "PROTOCOL_UNSUPPORTED" or "NOT_FOUND"})unsupportedAt=System.Diagnostics.Stopwatch.GetTimestamp();events.ExitFailed(e is NpepException n?n.Code:"EXIT_EVENT_OFFLINE",id);}
                await Task.Delay(TimeSpan.FromSeconds(3),_lifetime.Token);
            }}catch(OperationCanceledException)when(_lifetime.IsCancellationRequested){}
        });
    }
}
public sealed partial class NpepApi
{
    internal Task<JsonObject> ExitEventAsync(string bearer,JsonObject body,CancellationToken token)
    {NpepPhotoProtocol.Validate("exitRequest",body);return PhotoAsync("exit-events","exitReply",bearer,body.Text("requestId"),System.Text.Encoding.UTF8.GetBytes(body.ToJsonString()),null,token);}
}
public sealed partial class NpepDevice
{
    internal async Task<JsonObject> StudyExitAsync(JsonObject body,CancellationToken token)
    {
        await _gate.WaitAsync(token);try{RequireNotificationConnection();using var api=Api();return await api.ExitEventAsync(DeviceBearer,body,token);}
        catch(NpepException e){if(e.Status==401)SuspendIfTerminal(e);throw;}finally{_gate.Release();}
    }
}

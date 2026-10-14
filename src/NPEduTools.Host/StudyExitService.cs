using System.Text.Json.Nodes;
using System.Security.Cryptography;
using NPEduTools.Contracts;
using NPEduTools.Integrations.Npep;
namespace NPEduTools.Host;
public sealed class StudyExitService : INpepStudyExitEvents,IAsyncDisposable
{
    public const int VerificationSeconds=30, WarningSeconds=30;
    private readonly object _sync=new();
    private readonly Func<(string? Scope,bool Selected,StudyHostState State)> _sample;
    private readonly Func<NoiseProtectionState> _protection;
    private readonly StudyPhotoService _photos;
    private readonly StudyExitJournal _journal;
    private readonly TimeProvider _time;
    private readonly Action<StudyHostState>? _resume;
    private readonly CancellationTokenSource _stop=new();private readonly Task _loop;
    private string? _scope,_key,_reason,_auditError;
    private string _state="NotRequired",_audit="NotRequired";
    private Guid _id;private long _started,_failed;private StudyHostState? _begin;
    private Guid? _instance,_session;
    public StudyExitService(Func<(string? Scope,bool Selected,StudyHostState State)> sample,Func<NoiseProtectionState> protection,
        StudyPhotoService photos,StudyExitJournal journal,TimeProvider? time=null,bool background=true,Action<StudyHostState>? resume=null)
    {
        _sample=sample;_protection=protection;_photos=photos;_journal=journal;_time=time??TimeProvider.System;_resume=resume;
        _loop=background?Task.Run(async()=>{try{while(!_stop.IsCancellationRequested){Tick();await Task.Delay(TimeSpan.FromMilliseconds(200),_time,_stop.Token);}}catch(OperationCanceledException)when(_stop.IsCancellationRequested){} }):Task.CompletedTask;
    }
    private (string Scope,StudyHostState State,NoiseProtectionState Protection)? Read()
    {
        var x=_sample();var p=_protection();
        if(_scope is null || x.Scope!=_scope || x.State is not {PolicyState:"APPLIED",Phase:"Active",ClockReady:true,Version:not null,Window:not null,SchoolNow:not null,
            Settings.Noise.Enabled:true,Execution:{Owner:"Schedule",State:"Starting" or "Active",SessionId:not null}} || !p.Protected || p.SessionId!=x.State.Execution.SessionId)return null;
        return(_scope,x.State,p);
    }
    private static string Key(string scope,StudyHostState s,NoiseProtectionState p)=>$"{scope}|{s.Version}|{s.Window}|{p.InstanceId}|{p.SessionId}";
    public void BindExit(string? scope){lock(_sync){if(_scope!=scope){_scope=scope;_state="NotRequired";_key=null;}}}
    public HostResponse Begin(HostRequest request)
    {
        lock(_sync) {
            Tick();var current=Read();
            if(current is null)return new(1,request.RequestId,"NotRequired",null,"当前没有有效自习退出验证。",StudyExit:Snapshot());
            if(_id==request.RequestId)return new(1,request.RequestId,"Succeeded",null,"本次退出验证状态。",StudyExit:Snapshot());
            if(_state=="Waiting" || _state=="Failed" && WarningRemaining>0)return new(1,request.RequestId,"Rejected","EXIT_VERIFICATION_BUSY","已有退出验证或提醒，未创建新尝试。",StudyExit:Snapshot());
            _id=request.RequestId;_begin=current.Value.State;_instance=current.Value.Protection.InstanceId;_session=current.Value.Protection.SessionId;
            _key=Key(current.Value.Scope,_begin,current.Value.Protection);_started=_time.GetTimestamp();_state="Waiting";_reason=null;_audit="NotRequired";_auditError=null;
            _photos.ExitIntent(request with{Capability="study.photos.exit-intent"});
            return new(1,request.RequestId,"Succeeded",null,"开始本次自习退出验证；后台监测继续。",StudyExit:Snapshot());
        }
    }
    private double VerificationRemaining=>Math.Max(0,VerificationSeconds-_time.GetElapsedTime(_started).TotalSeconds);
    private double WarningRemaining=>_state=="Failed"?Math.Max(0,WarningSeconds-_time.GetElapsedTime(_failed).TotalSeconds):0;
    public void Tick()
    {
        lock(_sync) {
            if(_state is not ("Waiting" or "Failed"))return;
            var x=Read();if(x is null || Key(x.Value.Scope,x.Value.State,x.Value.Protection)!=_key){_state="Invalidated";return;}
            if(_state=="Waiting" && VerificationRemaining<=0)Fail("Timeout");
            if(_state=="Failed" && WarningRemaining<=0){_state="Completed";_resume?.Invoke(x.Value.State);}
        }
    }
    public void ObserveAuthorization(HostRequest request,HostResponse response)
    {
        lock(_sync) {
            Tick();var c=request.NoiseManagement;
            if(_state!="Waiting" || c is not {Action:"authorize",Purpose:"maintenance"} || c.TargetRequestId!=_id || c.InstanceId!=_instance || c.SessionId!=_session)return;
            if(response.Outcome=="Succeeded")_state="Succeeded";
            else if(response.ErrorCode is "MANAGEMENT_SECRET_INVALID" or "InvalidNoiseManagementCommand")Fail("WrongPassword");
        }
    }
    public bool CanAuthorize(NoiseManagementCommand c)
    {
        lock(_sync){Tick();return c.TargetRequestId!=_id || c.Purpose!="maintenance" || _state=="Waiting";}
    }
    public bool CanComplete(Guid requestId)
    {lock(_sync){Tick();return requestId!=_id || _state is "Succeeded" or "NotRequired" or "Invalidated";}}
    private void Fail(string reason)
    {
        // School time, policy or capture ownership may have changed since Tick.
        // Never construct an accusation from an unvalidated second observation.
        var current=Read();
        if(current is null || Key(current.Value.Scope,current.Value.State,current.Value.Protection)!=_key)
        {_state="Invalidated";return;}
        _state="Failed";_reason=reason;_failed=_time.GetTimestamp();
        var s=current.Value.State;
        var meta=new JsonObject{["eventId"]=_id.ToString(),["version"]=_begin!.Version,["sessionId"]=_session!.Value.ToString(),
            ["reason"]=reason,["deviceTime"]=_time.GetUtcNow().ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'"),
            ["schoolNow"]=s.SchoolNow!.Value.DateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff"),
            ["photoEventId"]=_photos.ExitOutcome(_id).State is "Captured" or "Stored" or "Capturing"?_id.ToString():null};
        try{_journal.Save(new(_scope!,meta));_audit="SavedLocal";}
        catch(Exception e)when(e is IOException or UnauthorizedAccessException or InvalidDataException or NpepException or CryptographicException){_audit="Unavailable";_auditError="EXIT_JOURNAL_UNAVAILABLE";}
    }
    public StudyExitState Snapshot()
    {
        lock(_sync){Tick();return new(_id,_state,_reason, _state=="Waiting"?VerificationRemaining:0,WarningRemaining,_photos.ExitOutcome(_id).State,_audit,_auditError);}
    }
    public StudyExitPacket? PendingExit()
    {
        lock(_sync){if(_scope is null)return null;try{return _journal.Next(_scope);}catch(Exception e)when(e is IOException or UnauthorizedAccessException or InvalidDataException or NpepException or System.Text.Json.JsonException or CryptographicException){_auditError="EXIT_JOURNAL_UNAVAILABLE";return null;}}
    }
    public void ExitStored(Guid id){lock(_sync){if(id==_id){_audit="Stored";_auditError=null;}try{_journal.Remove(id);}catch(Exception e)when(e is IOException or UnauthorizedAccessException){_auditError="EXIT_JOURNAL_UNAVAILABLE";}}}
    public void ExitFailed(string code,Guid? id=null)
    {
        lock(_sync){_auditError=code;
            try{if(code is "AUTH_INVALID" or "BINDING_CHANGED" or "EXIT_NOT_ELIGIBLE"){_journal.Clear();_state="Invalidated";}
                else if(id is {} eventId && code is ("POLICY_CHANGED" or "EXIT_EVENT_EXPIRED" or "INVALID_REQUEST" or "IDEMPOTENCY_CONFLICT")){_journal.Remove(eventId);if(eventId==_id)_audit="Unavailable";}}
            catch(Exception e)when(e is IOException or UnauthorizedAccessException){_auditError="EXIT_JOURNAL_UNAVAILABLE";}
        }
    }
    public async ValueTask DisposeAsync(){_stop.Cancel();await _loop;_stop.Dispose();}
}

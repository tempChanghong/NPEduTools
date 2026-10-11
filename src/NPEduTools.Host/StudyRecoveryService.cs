using System.Text.Json.Nodes;
using NPEduTools.Contracts;
using NPEduTools.Core;
using NPEduTools.Integrations.Npep;
namespace NPEduTools.Host;

public sealed class StudyRecoveryService : INpepStudyExitEvents,IAsyncDisposable
{
    private readonly object _sync=new();
    private readonly GuardFiles _files;
    private readonly Func<(string? Scope,bool Selected,StudyHostState State)> _sample;
    private readonly StudyPhotoService _photos;
    private readonly StudyExitJournal _journal;
    private readonly TimeProvider _time;
    private readonly Action<StudyHostState>? _resume;
    private readonly CancellationTokenSource _stop=new();private readonly Task _loop;
    private Guid _id;private string? _scope,_version,_auditError;
    private SchoolNoiseWindow? _window;private DateTimeOffset _warningEnd;
    private string _audit="NotRequired";
    private bool _photoAttempted,_resumed;
    private long _warningAt;private double _remaining;
    private double WarningRemaining=>Math.Max(0,_remaining-_time.GetElapsedTime(_warningAt).TotalSeconds);
    public StudyRecoveryService(GuardFiles files,Func<(string? Scope,bool Selected,StudyHostState State)> sample,StudyPhotoService photos,
        StudyExitJournal journal,TimeProvider? time=null,bool background=true,Action<StudyHostState>? resume=null)
    {
        _files=files;_sample=sample;_photos=photos;_journal=journal;_time=time??TimeProvider.System;_resume=resume;
        _loop=background?Task.Run(async()=>{try{while(!_stop.IsCancellationRequested){Tick();await Task.Delay(TimeSpan.FromMilliseconds(200),_time,_stop.Token);}}catch(OperationCanceledException)when(_stop.IsCancellationRequested){} }):Task.CompletedTask;
    }
    public void BindExit(string? scope){lock(_sync){if(_scope!=scope){_scope=scope;_id=Guid.Empty;_window=null;}}}
    public void Tick()
    {
        lock(_sync)
        {
            var x=_sample();var s=x.State;var now=_time.GetUtcNow();
            if(_scope is null || x.Scope!=_scope || !x.Selected || s is not {PolicyState:"APPLIED",Phase:"Active",ClockReady:true,Version:not null,Window:not null,SchoolNow:not null,
                Execution:{Owner:"Schedule",State:"Active",SignalFresh:true,SessionId:not null}})return;
            if(_files.Read<GuardRecoveryEvent>("recovery.json") is not {} incident || !incident.Valid() || incident.ContextKey!=GuardRecoveryEvent.Key(_scope,s.Version) || incident.Generation!=_files.Read<GuardRegistration>("registration.json")?.Generation ||
                now<incident.DetectedUtc || now>=incident.WindowEndUtc || now-incident.DetectedUtc>TimeSpan.FromMinutes(10) ||
                Math.Abs((incident.WindowEndUtc-now-(s.Window.End-s.SchoolNow.Value)).TotalSeconds)>3 ||
                _files.Stopped(incident.Generation) || _files.SessionEnding(incident.Generation))return;
            var expected=_files.Read<GuardExpectedExit>("expected-exit.json");
            if(expected?.Generation==incident.Generation && expected.SkipUntilUtc is {} until && now<until)return;
            if(_id!=incident.Id)
            {
                _resumed=false;_warningAt=_time.GetTimestamp();
                var claimed=_files.Read<Claim>("recovery-claim.json");
                // An unreadable/corrupt receipt is not permission to repeat a camera capture.
                if(_files.Exists("recovery-claim.json") && (claimed is null || claimed.Id==Guid.Empty || claimed.WarningEnd.Offset!=TimeSpan.Zero ||
                    claimed.Id==incident.Id && (claimed.WarningEnd<incident.DetectedUtc || claimed.WarningEnd>now.AddSeconds(30))))
                {_auditError="RECOVERY_CLAIM_INVALID";return;}
                _audit="NotRequired";_auditError=null;
                if(claimed?.Id==incident.Id)
                { _id=incident.Id;_version=s.Version;_window=s.Window;_warningEnd=claimed.WarningEnd;_remaining=Math.Clamp((_warningEnd-now).TotalSeconds,0,30);_photoAttempted=claimed.PhotoAttempted;
                    try{_audit=_journal.Next(_scope)?.Metadata["eventId"]?.GetValue<string>()==_id.ToString()?"SavedLocal":"NotRequired";}
                    catch(Exception e)when(StorageError(e)){_audit="Unavailable";_auditError="RECOVERY_JOURNAL_UNAVAILABLE";}
                }
                else
                {
                    _id=incident.Id;_version=s.Version;_window=s.Window;_warningEnd=now.AddSeconds(30);_remaining=30;_photoAttempted=false;
                    try{_files.Write("recovery-claim.json",new Claim(_id,_warningEnd,false));}
                    catch(Exception e)when(e is IOException or UnauthorizedAccessException){_id=Guid.Empty;_auditError="RECOVERY_STORE_UNAVAILABLE";return;}
                    var meta=new JsonObject{["eventId"]=_id.ToString(),["version"]=s.Version,["sessionId"]=s.Execution.SessionId.Value.ToString(),
                        ["reason"]="ProcessRecovery",["deviceTime"]=now.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'"),
                        ["schoolNow"]=s.SchoolNow.Value.DateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff"),["photoEventId"]=_id.ToString()};
                    try{_journal.Save(new(_scope,meta));_audit="SavedLocal";}
                    catch(Exception e)when(e is IOException or UnauthorizedAccessException or InvalidDataException or NPEduTools.Integrations.Npep.NpepException or System.Security.Cryptography.CryptographicException){_audit="Unavailable";_auditError="RECOVERY_JOURNAL_UNAVAILABLE";}
                }
            }
            if(_window!=s.Window || _version!=s.Version)return;
            // Wait for a fresh photo lease; mark durably before triggering. Restart cannot duplicate a shot.
            if(!_photoAttempted && (WarningRemaining<=0 || _photos.Snapshot().State is "Ready" or "NotRequired" or "Faulted"))
            {
                try{_files.Write("recovery-claim.json",new Claim(_id,_warningEnd,true));_photoAttempted=true;_photos.RecoveryIntent(_id);}
                catch(Exception e)when(e is IOException or UnauthorizedAccessException){_auditError="RECOVERY_STORE_UNAVAILABLE";}
            }
            if(WarningRemaining<=0 && !_resumed){_resumed=true;_resume?.Invoke(s);}
        }
    }
    private sealed record Claim(Guid Id,DateTimeOffset WarningEnd,bool PhotoAttempted);
    public StudyExitState Snapshot()
    {
        lock(_sync){Tick();var s=_sample().State;bool current=s.Version==_version && s.Window==_window && s.Phase=="Active" && s.ClockReady && s.Execution is {Owner:"Schedule",State:"Active",SignalFresh:true};
            double remaining=current?Math.Clamp(WarningRemaining,0,30):0;
            return new(_id,remaining>0?"Failed":"Completed","ProcessRecovery",0,remaining,_photos.ExitOutcome(_id).State,_audit,_auditError);}
    }
    public StudyExitPacket? PendingExit(){lock(_sync){try{return _scope is null?null:_journal.Next(_scope);}catch(Exception e)when(e is IOException or UnauthorizedAccessException or InvalidDataException or System.Security.Cryptography.CryptographicException or NpepException){_auditError="RECOVERY_JOURNAL_UNAVAILABLE";return null;}}}
    private static bool StorageError(Exception e)=>e is IOException or UnauthorizedAccessException or InvalidDataException or System.Security.Cryptography.CryptographicException or NpepException;
    public void ExitStored(Guid id){lock(_sync){try{_journal.Remove(id);if(id==_id){_audit="Stored";_auditError=null;}}catch(Exception e)when(StorageError(e)){_auditError="RECOVERY_JOURNAL_UNAVAILABLE";}}}
    public void ExitFailed(string code,Guid? id=null){lock(_sync){_auditError=code;try{if(code is "AUTH_INVALID" or "BINDING_CHANGED" or "EXIT_NOT_ELIGIBLE")_journal.Clear();else if(id is {} key && code is "POLICY_CHANGED" or "EXIT_EVENT_EXPIRED" or "INVALID_REQUEST" or "IDEMPOTENCY_CONFLICT")_journal.Remove(key);}catch(Exception e)when(StorageError(e)){_auditError="RECOVERY_JOURNAL_UNAVAILABLE";}}}
    public async ValueTask DisposeAsync(){_stop.Cancel();await _loop;_stop.Dispose();}
}

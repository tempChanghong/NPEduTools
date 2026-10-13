using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using NPEduTools.Contracts;
using NPEduTools.Core;
namespace NPEduTools.Host;

// Reservations live only in this Host. A restart never resumes a manual microphone session.
public sealed class ManualStudyService : IAsyncDisposable
{
    private readonly NoiseService _noise;
    private readonly Func<(string? Scope,bool Selected,StudyHostState State)> _school;
    private readonly Func<bool> _blocked;
    private readonly TimeProvider _time;
    private readonly string _directory;
    private readonly object _sync=new();
    private readonly CancellationTokenSource _stop=new();
    private readonly Task _loop;
    private readonly Guid _instance=Guid.NewGuid();
    private long _revision,_started,_ended;
    private Guid? _session;
    private DateTimeOffset? _start,_end;
    private bool _hasEnd;
    private readonly Func<int> _lead;
    private int _displayLeadSeconds = 120;
    private int? _hide=600;
    private string? _device,_reason,_reportError;
    private string _phase="Idle";
    private bool _disposed;
    private StudyReportData? _report;
    private readonly Dictionary<Guid,(ManualStudyCommand? Command,HostResponse Response)> _requests=[];
    public StudyDisplayService Display {get;}
    public ManualStudyService(NoiseService noise,Func<(string? Scope,bool Selected,StudyHostState State)> school,
        Func<bool> blocked,string directory,TimeProvider? time=null,bool background=true,Func<bool>? projection=null, Func<int>? displayLeadSeconds=null,
        Func<TimeSpan?>? inactivity=null)
    {
        _lead = displayLeadSeconds ?? (() => 120);
        _noise=noise;_school=school;_blocked=blocked;_directory=directory;_time=time??TimeProvider.System;
        Display=new(Source,_time,noise.StudyThresholds,Path.Combine(directory,"reminders"),(scope,v,p)=>noise.ResolveCalibration(_school().Scope,_school().State.Version,p),noise.RecordStudyReminder,projection,inactivity);
        noise.Completed+=Completed;
        _loop=background?Task.Run(async()=>{try{while(!_stop.IsCancellationRequested){Tick();await Task.Delay(TimeSpan.FromMilliseconds(200),_time,_stop.Token);}}catch(OperationCanceledException)when(_stop.IsCancellationRequested){} }):Task.CompletedTask;
    }
    public ManualStudyState Status() {lock(_sync)return new(_instance,_revision,_session,_phase,_reportError??_reason,_start,_end,_hasEnd,_hide,_displayLeadSeconds);}
    private HostResponse Reply(HostRequest r,string outcome="Succeeded",string? error=null,string message="手动自习状态。")=>
        new(1,r.RequestId,outcome,error,message,Noise:_noise.Snapshot(),ManualStudy:Status());
    public HostResponse Handle(HostRequest r)
    {
        if(r.Capability=="manual.study.status"){Tick();return Reply(r) with {Study=Display.Snapshot()};}
        lock(_sync)
        {
            Tick();
            if(_requests.TryGetValue(r.RequestId,out var previous))return previous.Command==r.ManualStudy
                ?previous.Response with {Noise=_noise.Snapshot(),ManualStudy=Status()}
                :Reply(r,"Rejected","MANUAL_REQUEST_CONFLICT","同一请求标识不能执行不同操作。");
            if(r.ManualStudy is not {} c || c.InstanceId!=_instance || c.Revision!=_revision || (c.Action!="start" && (c.SessionId is null || c.SessionId!=_session) || c.Action=="start" && c.SessionId is not null))
                return Reply(r,"Rejected","MANUAL_STATE_CHANGED","手动会话已变化；没有停止其他会话。");
            if(c.Action=="start")
            {
                var now=_time.GetUtcNow();var start=c.StartUtc??now;var end=c.EndUtc??start.AddHours(3);
                if(!NoiseContract.ValidDeviceId(c.DeviceId) || start<now.AddSeconds(-5) || start>now.AddDays(366) || end<=start || end-start>TimeSpan.FromHours(3) ||
                    c.HideSeconds is <1 or >10800 || start.Offset!=TimeSpan.Zero || end.Offset!=TimeSpan.Zero)
                    return Reply(r,"Rejected","MANUAL_TIME_INVALID","结束须晚于开始，实际采集最多三小时；请检查日期、时间和返回设置。");
                if(_phase is "Waiting" or "Preparing" or "Active" or "Starting" or "Stopping" || _noise.CaptureBusy || _blocked())
                    return Reply(r,"Rejected","MANUAL_BUSY","当前设备忙碌或学校定时自习优先；没有创建手动会话。");
                int lead;
                try { lead = _lead(); }
                catch(Exception e) when(e is IOException or InvalidDataException or UnauthorizedAccessException or JsonException) {
                    return Reply(r,"Rejected","MANUAL_PREFERENCES_UNAVAILABLE","手动自习展示设置无法读取；原文件保留，请在设置页重新确认。");
                }
                if (lead is < 0 or > 1800) return Reply(r,"Rejected","MANUAL_PREFERENCES_UNAVAILABLE","展示提前时间无效。");
                _displayLeadSeconds = Math.Min(lead, (int)Math.Ceiling(Math.Max(0, (start - now).TotalSeconds)));
                _session=Guid.NewGuid();_start=start;_end=end;_hasEnd=c.EndUtc is not null;_hide=c.HideSeconds;_device=c.DeviceId;
                _reason=_reportError=null;_report=null;_phase="Waiting";_revision++;
                Tick();
            }
            else if(c.Action=="end")
            {
                if(_phase is "Waiting" or "Preparing") {_phase="Cancelled";_reason="CancelledBeforeStart";_revision++;}
                else if(_phase is "Active" or "Starting"){_reason="UserEnded";_noise.StopManualStudy(_session!.Value,_reason);_phase="Stopping";_revision++;}
            }
            else return Reply(r,"Rejected","MANUAL_COMMAND_INVALID");
            var reply=Reply(r,"Succeeded",null,"手动自习安排已受理；采集状态以实际执行为准。");
            if(_requests.Count>=128)_requests.Remove(_requests.Keys.First());_requests[r.RequestId]=(r.ManualStudy,reply);return reply;
        }
    }
    public void Tick()
    {
        lock(_sync)
        {
            var now=_time.GetUtcNow();
            if(_phase is "Waiting" or "Preparing")
            {
                if(_blocked()){_phase="Cancelled";_reason="ScheduledTakeover";_revision++;return;}
                if(now>=_end){_phase="Cancelled";_reason="MissedStart";_revision++;return;}
                _phase=now>=_start!.Value.AddSeconds(-_displayLeadSeconds)?"Preparing":"Waiting";
                if(now>=_start)
                {
                    var reply=_noise.StartManualStudy(_session!.Value,_device!,Math.Min(10800,(_end!.Value-now).TotalSeconds),_hasEnd);
                    if(reply.Outcome!="Accepted"){_phase="Cancelled";_reason=reply.ErrorCode;_revision++;return;}
                    _start=now;_started=_time.GetTimestamp();_phase="Starting";_revision++;
                }
            }
            if(_phase is "Starting" or "Active")
            {
                var noise=_noise.Snapshot();
                if(noise.SessionId!=_session){_phase="Interrupted";_reason="SESSION_CHANGED";_revision++;return;}
                if(_blocked()){_reason="ScheduledTakeover";_noise.StopManualStudy(_session!.Value,_reason);_phase="Stopping";_revision++;}
                else if(_time.GetElapsedTime(_started)>=_end!.Value-_start!.Value){_reason=_hasEnd?"PlannedEnd":"ThreeHourLimit";_noise.StopManualStudy(_session!.Value,_reason);_phase="Stopping";_revision++;}
                else if(noise.State=="Active")_phase="Active";
            }
        }
    }
    private void Completed(NoiseState noise)
    {
        lock(_sync)
        {
            if(noise.SessionId!=_session || !_noise.IsManualStudy(noise.SessionId) || _report is not null)return;
            var data=_noise.StudyReportSnapshot();if(data is null)return;
            _reason=_noise.ManualStudyEndReason(_session!.Value)??_reason??(noise.State=="Faulted"?"CaptureFault":"UserEnded");
            _end=_start!.Value.AddSeconds(Math.Clamp(data.ElapsedSeconds,.001,10800));_ended=_time.GetTimestamp();_phase="Ended";_revision++;
            _report=new(new(_start.Value.ToLocalTime(),_end.Value.ToLocalTime()),Math.Max(.001,data.ElapsedSeconds),data.Raw.SampledSeconds,
                Math.Clamp(data.Raw.SampledSeconds/Math.Max(.001,data.ElapsedSeconds),0,1),data.Raw.SampledSeconds>0?100*data.ClippedSeconds/data.Raw.SampledSeconds:0,
                data.Frames,data.DisciplineReminders,data.Raw,data.Estimated,data.Complete,["手动自习 · "+ReasonText(_reason),"dBFS 与估算 dB(A) 分开统计；缺样不记作安静。"],Mode:"Manual",ClockSource:"Windows");
            if (data.ReferenceNote is {} referenceNote) _report=_report with {Notes=_report.Notes.Append(referenceNote).ToArray()};
            try
            {
                Directory.CreateDirectory(_directory);string file=Path.Combine(_directory,_session.Value.ToString("N")+".json");
                string temp=file+".tmp";File.WriteAllText(temp,JsonSerializer.Serialize(new {Kind="Manual",SessionId=_session,Reason=_reason,Report=_report},Protocol.Json));File.Move(temp,file,true);
                foreach(var old in Directory.EnumerateFiles(_directory,"*.json").OrderByDescending(File.GetLastWriteTimeUtc).Where((p,i)=>i>=50 || File.GetLastWriteTimeUtc(p)<_time.GetUtcNow().UtcDateTime.AddDays(-14)))File.Delete(old);
            }
            catch(Exception e)when(e is IOException or UnauthorizedAccessException){_reportError="MANUAL_REPORT_STORE_UNAVAILABLE";}
        }
    }
    private static string ReasonText(string? reason)=>reason switch {"ScheduledTakeover"=>"被学校定时自习接管","PlannedEnd"=>"到达指定结束时间","ThreeHourLimit"=>"达到三小时上限","CaptureFault"=>"采集异常结束","BackgroundExit"=>"后台退出",_=>"主动结束"};
    public StudyReportState Report()
    {
        lock(_sync){double elapsed=_phase=="Ended"?_time.GetElapsedTime(_ended).TotalSeconds:double.MaxValue;
            return new(_instance,Version,_report is not null && _reason!="ScheduledTakeover" && elapsed>=5 && elapsed<305,"Manual",Math.Max(0,305-elapsed),_report);}
    }
    private string Version=>Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(_session?.ToString()??"idle")));
    private static DateTimeOffset Wall(DateTimeOffset utc)=>new(utc.ToLocalTime().DateTime,TimeSpan.Zero);
    public (string? Scope,bool Selected,StudyHostState State) Source()
    {
        lock(_sync)
        {
            var school=_school();var settings=school.State.Settings??new StudySettings(new("Asia/Shanghai","2000-01-01",null,[],[]),new("StatusTrend",600),new(false,"BoundClass",[],60),new(true,null,null,600,3),new(false,3,30,30,true,false));
            settings=settings with {Noise=settings.Noise with {Enabled=true},Presentation=settings.Presentation with {TemporaryHideSeconds=_hide??600}};
            var noise=_noise.Snapshot();
            var now=_phase is "Starting" or "Active" or "Stopping"?_start!.Value.Add(_time.GetElapsedTime(_started)).ToLocalTime():_phase=="Ended"?_end!.Value.Add(_time.GetElapsedTime(_ended)).ToLocalTime():_time.GetUtcNow().ToLocalTime();
            var phase=_phase is "Starting" or "Active" or "Stopping"?"Active":_phase=="Ended"?_time.GetElapsedTime(_ended).TotalSeconds<5?"Ended":"Outside":_phase;
            now=Wall(now);
            var window=_start is {} start && _end is {} end?new SchoolNoiseWindow(Wall(start),Wall(end)):null;
            bool fresh=noise.SessionId==_session && noise.State=="Active" && noise.Quality is "Good" or "Clipping" or "DigitalSilence";
            var execution=new StudyCaptureState("Manual",noise.SessionId==_session?noise.State:"Idle",_session,_reason??"MANUAL_ACTIVE",true,now.DateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff"),null,fresh);
            return (school.Scope??"manual-local",_session is not null,new StudyHostState(Version,"APPLIED","INTEGRATED",fresh?"ACTIVE":"WAITING_FRESH_SIGNAL",phase,_reason??"MANUAL_ACTIVE",true,now,window,null,settings,execution,Homework:school.State.PolicyState=="APPLIED" && school.State.Homework?.Date==now.DateTime.ToString("yyyy-MM-dd")?school.State.Homework:null,Manual:Status()));
        }
    }
    public (string? Scope,bool Selected,StudyHostState State) PhotoSource()
    {
        var school=_school();var manual=Source();
        if(school.Scope is null || school.State.PolicyState!="APPLIED" || school.State.Settings is null)return school;
        return (school.Scope,true,manual.State with {Version=school.State.Version,Settings=school.State.Settings,PolicyState=school.State.PolicyState});
    }
    public async ValueTask DisposeAsync()
    {
        if(_disposed)return;_disposed=true;_stop.Cancel();await _loop;
        Guid? id;lock(_sync){id=_session;}
        if(id is {} session)await _noise.EndManualStudyAsync(session,"BackgroundExit");
        _noise.Completed-=Completed;_stop.Dispose();
    }
}

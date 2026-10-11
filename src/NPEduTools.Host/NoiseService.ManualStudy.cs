using NPEduTools.Contracts;
namespace NPEduTools.Host;

public sealed partial class NoiseService
{
    private bool _scheduledPriority;
    internal bool IsManualStudy(Guid? id) { lock (_sync) return id is not null && _session is { ManualStudy: true } s && s.Id == id; }
    internal HostResponse StartManualStudy(Guid id, string device, double seconds)
    {
        lock (_sync)
        {
            if (_scheduledPriority || ScheduledLive) return new(1,id,"Rejected","SCHEDULE_PRIORITY","学校定时自习优先。");
            var s=Snapshot();
            var reply=Handle(new(1,id,"noise.command",Noise:new("start",s.InstanceId,s.Revision,device)),id,seconds,false);
            if(reply.Outcome=="Accepted" && _session is {} current)current.ManualStudy=true;
            return reply;
        }
    }
    internal void SetScheduledPriority(bool active)
    {
        lock (_sync)
        {
            _scheduledPriority=active;
            if(active && _session is {Scheduled:false, Work.IsCompleted:false} s && _state is "Starting" or "Active")
            { if(s.Calibration is not null || s.Reference is not null)s.CalibrationInterrupted=true; s.EndReason="ScheduledTakeover"; _revision++; _state="Stopping"; _message="学校定时自习接管，正在结束手动会话并释放设备…"; s.Stop.Cancel(); }
        }
    }
    internal string? ManualStudyEndReason(Guid id) {lock(_sync)return _session?.Id==id?_session.EndReason:null;}
    internal async Task EndManualStudyAsync(Guid id,string reason)
    {
        Task? work;lock(_sync){work=_session is {ManualStudy:true,Scheduled:false} s && s.Id==id?s.Work:null;StopManualStudy(id,reason);}
        if(work is not null)await work;
    }
    internal bool StopManualStudy(Guid id,string reason)
    {
        lock(_sync)
        {
            if(_session is not {ManualStudy:true,Scheduled:false} s || s.Id!=id)return false;
            if(_state is "Starting" or "Active") {s.EndReason??=reason;_revision++;_state="Stopping";s.Stop.Cancel();}
            return true;
        }
    }
}

using NPEduTools.Contracts;
namespace NPEduTools.Host;

public sealed partial class NoiseService
{
    private bool _scheduledPriority;
    // Lives only as long as this Host's accepted reservation. No secret or reusable ticket is retained.
    internal sealed record ManualStudyPermission(long PolicyEpoch, long? PasswordEpoch);

    internal string? AuthorizeManualStudyStart(HostRequest request, out ManualStudyPermission? permission)
    {
        permission = null;
        var usb = ReadActionUsb(request);
        lock (_sync)
        {
            long? passwordEpoch = null;
            var rule = ClassroomRule("start-manual-study");
            if (rule is not (null or "default"))
            {
                if (management?.RequiresClassroomPassword(request.NoiseAuthorization) == true)
                    passwordEpoch = management.ClassroomPasswordEpoch;
                if (CheckClassroomTicket(request, "start-manual-study", rule, usb) is { } error) return error;
            }
            else if (request.NoiseAuthorization is not null) return "CLASSROOM_POLICY_CHANGED";
            permission = new(_manualStudyPolicyEpoch, passwordEpoch);
            return null;
        }
    }

    internal string? ManualStudyPermissionError(ManualStudyPermission? permission)
    {
        lock (_sync)
        {
            if (permission is null) return ClassroomRule("start-manual-study") switch
            {
                null or "default" => null, "deny" => "CLASSROOM_ACTION_DENIED", _ => "MANAGEMENT_REQUIRED"
            };
            if (permission.PolicyEpoch != _manualStudyPolicyEpoch ||
                permission.PasswordEpoch is { } epoch && (!ClassroomPasswordReady || epoch != management?.ClassroomPasswordEpoch))
                return "CLASSROOM_POLICY_CHANGED";
            return null;
        }
    }
    internal bool IsManualStudy(Guid? id) { lock (_sync) return id is not null && _session is { ManualStudy: true } s && s.Id == id; }
    internal HostResponse StartManualStudy(Guid id, string device, double seconds, bool hasPlannedEnd = false,
        ManualStudyPermission? permission = null)
    {
        lock (_sync)
        {
            if (_scheduledPriority || ScheduledLive) return new(1,id,"Rejected","SCHEDULE_PRIORITY","学校定时自习优先。");
            if (ManualStudyPermissionError(permission) is { } denied)
                return new(1,id,"Rejected",denied,"本场手动自习的开始授权已变化。");
            var s=Snapshot();
            var reply=Handle(new(1,id,"noise.command",Noise:new("start",s.InstanceId,s.Revision,device)),id,seconds,false);
            if(reply.Outcome=="Accepted" && _session is {} current)
            { current.ManualStudy=true;current.TimeLimitReason=hasPlannedEnd?"PlannedEnd":"ThreeHourLimit"; }
            return reply;
        }
    }
    internal void SetScheduledPriority(bool active)
    {
        lock (_sync)
        {
            _scheduledPriority=active;
            // Reference capture intentionally appears Idle in legacy status. It still
            // owns the microphone and must be cancelled here, before the scheduler's
            // capture-busy gate waits for all device cleanup and completion callbacks.
            if(active && _session is {Scheduled:false, Work.IsCompleted:false} s && _state is "Starting" or "Active" or "ReferenceSampling")
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

using NPEduTools.Contracts;
using NPEduTools.Integrations.Npep;
namespace NPEduTools.Host;

// Both journals use the existing authenticated audit transport and retention policy.
public sealed class StudyExitEvents(INpepStudyExitEvents verification,INpepStudyExitEvents recovery):INpepStudyExitEvents
{
    public void BindExit(string? scope){verification.BindExit(scope);recovery.BindExit(scope);}
    public StudyExitPacket? PendingExit()=>verification.PendingExit()??recovery.PendingExit();
    public void ExitStored(Guid id){verification.ExitStored(id);recovery.ExitStored(id);}
    public void ExitFailed(string code,Guid? id=null){verification.ExitFailed(code,id);recovery.ExitFailed(code,id);}
}

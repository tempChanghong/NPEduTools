using System.Diagnostics;
using System.Windows;
using NPEduTools.Contracts;
using NPEduTools.Core;
namespace NPEduTools.App;
public partial class MainWindow
{
    private StudyExitWarningWindow? _exitWarning;
    private readonly HashSet<Guid> _shownExitWarnings=[];
    private readonly Queue<Guid> _exitWarningOrder=[];
    private void ShutdownExitWarning()=>_exitWarning?.Shutdown();
    private long? _exitUnsupportedAt;
    private async Task<StudyExitState?> ReadStudyExitAsync()
    {
        try{using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(1));
            var reply=await HostClient.RequestAsync(_pipe,new HostRequest(1,Guid.NewGuid(),"study.exit.status",TimeoutMs:1000),deadline.Token);
            if(reply.ErrorCode is "UnknownCapability" or "STUDY_EXIT_UNSUPPORTED")_exitUnsupportedAt=Stopwatch.GetTimestamp();
            return reply.Outcome=="Succeeded"?reply.StudyExit:null;
        }catch(Exception e)when(IsManagementError(e)){return null;}
    }
    private void ShowStudyExitWarning(StudyExitState state)
    {
        if(_lifetime.IsCancellationRequested || state.State!="Failed" || state.WarningRemainingSeconds<=0 || _exitWarning is not null || !_shownExitWarnings.Add(state.AttemptId))return;
        _exitWarningOrder.Enqueue(state.AttemptId);
        if(_exitWarningOrder.Count>64)_shownExitWarnings.Remove(_exitWarningOrder.Dequeue());
        _studyWindow?.HideDisplay();_exitWarning=new(this,state,ReadStudyExitAsync);
        _exitWarning.Closed+=(_,_)=>{_exitWarning=null;};_exitWarning.Show();
    }
    private async Task PollStudyExitAsync()
    {
        if(_exitUnsupportedAt is {} at && Stopwatch.GetElapsedTime(at).TotalSeconds<60)return;
        var state=await ReadStudyExitAsync();if(state is null)return;
        _exitUnsupportedAt=null;
        if(state.State=="Failed" && !RuntimeOperationFile.PriorityRequested && NotificationDesktopAvailable())ShowStudyExitWarning(state);
        else if(state.State!="Failed")_exitWarning?.Shutdown();
    }
}

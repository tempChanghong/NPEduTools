using System.IO.Pipes;
using NPEduTools.App;
using NPEduTools.Contracts;

internal static partial class Program
{
    private static void RunStudyPresentationPipeChecks()
    {
        string pipe="NPEduTools.Test.study-presentation."+Guid.NewGuid().ToString("N");
        var state=StudyFixture() with {Homework=null};var time=new StudyUiClock();
        using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var pulses=new List<StudyPresentationPulse>();
        var server=Task.Run(async()=>
        {
            for(int index=0;index<5;index++)
            {
                using var stream=new NamedPipeServerStream(pipe,PipeDirection.InOut,1,PipeTransmissionMode.Byte,PipeOptions.Asynchronous|PipeOptions.CurrentUserOnly);
                await stream.WaitForConnectionAsync(timeout.Token);var request=await Protocol.ReadAsync<HostRequest>(stream,timeout.Token);
                Assert(request.Capability=="study.presentation" && Protocol.Validate(request) is null,"invalid WPF presence frame");
                Assert(!Protocol.NoiseInterruption(request),"presence is a capture interruption");
                pulses.Add(request.StudyPresentation!);
                await Protocol.WriteAsync(stream,new HostResponse(1,request.RequestId,"Succeeded",null,"fixture"),timeout.Token);
            }
        });
        var window=new StudyWindow(pipe,()=>{},true,time);
        try
        {
            Exercise(window,_=>
            {
                void Send() {var report=window.ReportPresentationAsync();PumpUntil(()=>report.IsCompleted,"presence pipe request did not complete");report.GetAwaiter().GetResult();}
                window.Apply(state,null);Send();
                window.WindowState=System.Windows.WindowState.Minimized;Send();
                window.ShowDisplay();window.Apply(state,null);Send();
                time.Ms=1600;Send();
                window.HideDisplay();Send();
                PumpUntil(()=>server.IsCompleted,"presence server did not settle");server.GetAwaiter().GetResult();
                Assert(pulses.Select(p=>p.State).SequenceEqual(["Visible","Hidden","Visible","Stale","Hidden"]),"visibility or stale-render observation mislabeled");
                Assert(pulses[0].RenderAgeMs==0 && pulses[2].RenderAgeMs==0 && pulses[3].RenderAgeMs is null && pulses[4].RenderAgeMs is null,"render age fabricated");
                Assert(pulses.Select(p=>p.AppInstanceId).Distinct().Count()==1 && pulses[4].Sequence>pulses[3].Sequence,"App identity/sequence changed");
                Checks.Add("Study WPF actual Named Pipe: shown + fresh render, minimize/restore, stale school receipt, hidden; no capture command");
                window.Shutdown();
            });
        }
        finally {window.Shutdown();timeout.Cancel();}
    }
}

using System.IO.Pipes;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using NPEduTools.App;
using NPEduTools.Contracts;
internal static partial class Program
{
    private static void RunSeatingLifecycleChecks()
    {
        foreach(var (transition,busy) in new[]{("Hide",false),("Hide",true),("Ended",false),("Policy",true),("Shutdown",true),("Clock",false)})
            RunSeatingLifecycleCheck(transition,busy);
        Checks.Add("Seating upload lifecycle: hide/end/policy/clock/shutdown dismiss owned dialog; pending transfers cancel and clean own file; ordinary busy close remains blocked");
    }
    private static void RunSeatingLifecycleCheck(string transition,bool busy)
    {
        string pipe="NPEduTools.Test.seating-lifecycle."+Guid.NewGuid().ToString("N");
        using var lifetime=new CancellationTokenSource();
        var sent=new TaskCompletionSource<StudySeatingUploadCommand>(TaskCreationOptions.RunContinuationsAsynchronously);
        var server=Task.Run(async()=>{
            while(!lifetime.IsCancellationRequested) {
                using var stream=new NamedPipeServerStream(pipe,PipeDirection.InOut,1,PipeTransmissionMode.Byte,PipeOptions.Asynchronous|PipeOptions.CurrentUserOnly);
                await stream.WaitForConnectionAsync(lifetime.Token);
                var request=await Protocol.ReadAsync<HostRequest>(stream,lifetime.Token);
                if(request.Capability=="study.seating.upload") {
                    sent.TrySetResult(request.SeatingUpload!);await Task.Delay(Timeout.Infinite,lifetime.Token);continue;
                }
                await Protocol.WriteAsync(stream,new HostResponse(1,request.RequestId,"Succeeded",null,"synthetic",
                    SeatingUpload:new(Guid.NewGuid(),new string('a',64),"one","本班",0,null,"Ready")),lifetime.Token);
            }
        });
        var clock=new StudyUiClock();var state=StudyFixture();var owner=new StudyWindow(pipe,()=>{},true,clock){Width=1100,Height=700};
        var surface=(StudySurface)owner.Content;Exception? failure=null;
        try {
            owner.Show();owner.Apply(state,null);clock.Ms=6000;owner.Apply(state with {SchoolNow=state.SchoolNow!.Value.AddSeconds(6)},null);
            ((Button)surface.FindName("SeatingButton")).RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
            Application.Current.Dispatcher.BeginInvoke(()=>{
                var dialog=Application.Current.Windows.OfType<StudySeatingUploadWindow>().Single();
                try {
                    PumpUntil(()=>((TextBlock)dialog.FindName("ScopeText")).Text.Contains("本班") && ((Button)dialog.FindName("RefreshScope")).IsEnabled,"upload dialog did not load");
                    if(busy) {
                        string source=Path.Combine(_output,"lifecycle-source.png");File.WriteAllBytes(source,SeatImage(Colors.White));
                        var load=(Task)typeof(StudySeatingUploadWindow).GetMethod("LoadDraftAsync",BindingFlags.NonPublic|BindingFlags.Instance)!.Invoke(dialog,[source])!;
                        PumpUntil(()=>load.IsCompleted && ((Button)dialog.FindName("SaveImage")).IsEnabled,"lifecycle draft not loaded");load.GetAwaiter().GetResult();
                        ((Button)dialog.FindName("SaveImage")).RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
                        PumpUntil(()=>sent.Task.IsCompleted,"pending upload did not arrive");dialog.Close();Assert(dialog.IsVisible,"ordinary close abandoned an in-flight upload");
                    }
                    switch(transition) {
                        case "Hide": owner.HideDisplay();break;
                        case "Ended": owner.Apply(state with {Phase="Ended",SchoolNow=state.Window!.End},null);break;
                        case "Policy": owner.Apply(state with {Version=new string('b',64)},null);break;
                        case "Clock": owner.Apply(state with {ClockReady=false},null);break;
                        case "Shutdown": owner.Shutdown();break;
                    }
                    Assert(!dialog.IsVisible,$"seating upload dialog remained on top after {transition}; busy={busy}");
                    if(busy)Assert(!File.Exists(NPEduTools.Core.StudySeatingTransfer.PathFor(pipe,sent.Task.Result.UploadId)),"retired dialog retained transfer file");
                } catch(Exception e) { failure=e; }
                finally { dialog.Close(); }
            });
            ((Button)surface.FindName("UploadSeating")).RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
            if(failure is not null)throw failure;
        } finally {
            owner.Shutdown();lifetime.Cancel();try{server.GetAwaiter().GetResult();}catch(OperationCanceledException){}
        }
    }
}

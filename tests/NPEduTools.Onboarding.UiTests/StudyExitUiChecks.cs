using System.IO.Pipes;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using NPEduTools.App;
using NPEduTools.Contracts;
using NPEduTools.Core;
internal static partial class Program
{
    private static IEnumerable<DependencyObject> ExitVisuals(DependencyObject node)
    {yield return node;for(int i=0;i<VisualTreeHelper.GetChildrenCount(node);i++)foreach(var child in ExitVisuals(VisualTreeHelper.GetChild(node,i)))yield return child;}
    private static void RunStudyExitChecks()
    {
        var prior=SynchronizationContext.Current;SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Application.Current.Dispatcher));
        try {
            var owner=new Window{Left=5000,Top=5000,Width=600,Height=400,ShowActivated=false};owner.Show();
            var current=new StudyExitState(Guid.NewGuid(),"Failed","Timeout",0,30,"Stored","Stored");
            var warning=new StudyExitWarningWindow(owner,current,()=>Task.FromResult<StudyExitState?>(current)){Left=5000,Top=5000,Topmost=false,ShowActivated=false};
            try {
                warning.Show();warning.UpdateLayout();var visuals=ExitVisuals(warning).ToArray();
                Assert(visuals.OfType<UIElement>().All(v=>v.Effect is null),"warning contains a glow, blur or shadow effect");
                var text=visuals.OfType<TextBlock>().Single(v=>v.Text==StudyExitNotice.Translations);
                Assert(text.Foreground==Brushes.White && text.TextAlignment==TextAlignment.Left && text.FontSize>=49,"multilingual text lost its plain white, large left-aligned style");
                Assert(text.Text.Split('\n').Length==10 && text.Text.Contains("Die Aufsicht rechtswidrig umgehen"),"corrected ten translations were not applied");
                var content=(Grid)visuals.OfType<Viewbox>().Single().Child;
                Assert(content.ColumnDefinitions[0].Width.Value>content.ColumnDefinitions[1].Width.Value,"left area is not larger");
                foreach(var block in visuals.OfType<TextBlock>())Assert(block.DesiredSize.Height<=1000,"warning text overflows a 1080p design surface");
                Snapshot(warning,"study-exit-plain-white.png");warning.Close();Assert(warning.IsVisible,"unverified closing dismissed warning");
                current=current with{State="Completed",WarningRemainingSeconds=0};PumpUntil(()=>!warning.IsVisible,"completed Host warning did not return to study");
                Checks.Add("Exit warning: corrected ten languages, plain white glyphs, deep red, larger left area, 1080p fit, guarded close and acknowledged expiry; no sensors/audio");
            }finally{warning.Shutdown();owner.Close();}
            RunExitDialogCheck(false);RunExitDialogCheck(true);RunNoiseManagementChecks();
        }finally{SynchronizationContext.SetSynchronizationContext(prior);}
    }
    private static void RunExitDialogCheck(bool correct)
    {
        const BindingFlags flags=BindingFlags.Instance|BindingFlags.NonPublic;
        string pipe="NPEduTools.Test.exit-ui."+Guid.NewGuid().ToString("N");using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var server=new NamedPipeServerStream(pipe,PipeDirection.InOut,1,PipeTransmissionMode.Byte,PipeOptions.Asynchronous|PipeOptions.CurrentUserOnly);
        var owner=new Window{Left=5000,Top=5000,Width=600,Height=400,ShowActivated=false};owner.Show();
        var protection=new NoiseProtectionState(true,true,Guid.NewGuid(),Guid.NewGuid());var id=Guid.NewGuid();
        var state=new StudyExitState(id,"Waiting",null,30,0,"Suppressed","NotRequired");
        var target=new HostRequest(1,id,"host.stop");var type=typeof(NoiseWindow).Assembly.GetType("NPEduTools.App.NoiseManagementDialog")!;
        Func<Task<StudyExitState?>> read=()=>Task.FromResult<StudyExitState?>(state);
        var dialog=(Window)type.GetConstructors(flags).Single().Invoke([owner,pipe,protection,target,state,read]);
        dialog.WindowStartupLocation=WindowStartupLocation.Manual;dialog.Left=5000;dialog.Top=5000;dialog.ShowActivated=false;
        async Task<HostRequest> Incoming(){await server.WaitForConnectionAsync(deadline.Token);return await Protocol.ReadAsync<HostRequest>(server,deadline.Token);}
        var pending=Incoming();var ticket=Guid.NewGuid();
        try {Exercise(dialog,_=>{
            Assert(!ExitVisuals(dialog).OfType<Button>().Any(b=>b.IsCancel || Equals(b.Content,"取消")),"strict exit dialog exposes cancellation");
            dialog.Close();Assert(dialog.IsVisible,"Alt-F4/Close bypassed strict verification");
            var secret=(PasswordBox)type.GetField("_old",flags)!.GetValue(dialog)!;secret.Password=correct?"synthetic-teacher-secret":"bad";
            ((Button)type.GetField("_submit",flags)!.GetValue(dialog)!).RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
            PumpUntil(()=>pending.IsCompleted,"verification was not sent");var request=pending.GetAwaiter().GetResult();
            Assert(request.NoiseManagement is{Purpose:"maintenance"} c && c.TargetRequestId==id,"exit authorization lost its attempt/purpose binding");
            state=state with{State=correct?"Succeeded":"Failed",Reason=correct?null:"WrongPassword",VerificationRemainingSeconds=0,WarningRemainingSeconds=correct?0:30};
            if(correct){var elapsed=System.Diagnostics.Stopwatch.StartNew();PumpUntil(()=>elapsed.ElapsedMilliseconds>350,"timer did not run");Assert(dialog.IsVisible,"accepted status closed before scoped authorization ticket arrived");}
            var writing=Protocol.WriteAsync(server,new HostResponse(1,request.RequestId,correct?"Succeeded":"Rejected",correct?null:"InvalidNoiseManagementCommand","Synthetic result",NoiseProtection:protection with{Ticket=correct?ticket:null}),deadline.Token);
            PumpUntil(()=>writing.IsCompleted,"reply did not finish");writing.GetAwaiter().GetResult();PumpUntil(()=>!dialog.IsVisible,"verification result did not close dialog");
            Assert(secret.Password=="","closed verification retained a secret");
            if(correct)Assert(Equals(type.GetProperty("Ticket")!.GetValue(dialog),ticket),"accepted in-flight ticket was discarded");
            else Assert(((StudyExitState?)type.GetProperty("FailedExit")!.GetValue(dialog))?.Reason=="WrongPassword","first invalid password did not expose this attempt's failed state");
            Checks.Add(correct?"Strict exit: early accepted status waits for the scoped ticket":"Strict exit: no cancel/close, first invalid short password ends verification; no microphone or camera");
        });}finally{type.GetField("_completed",flags)!.SetValue(dialog,true);dialog.Close();owner.Close();}
    }
}

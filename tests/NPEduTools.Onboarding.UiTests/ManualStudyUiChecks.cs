using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using NPEduTools.App;
using NPEduTools.Contracts;
internal static partial class Program
{
    private static void RunManualStudyChecks()
    {
        var clock=new StudyUiClock();int ends=0;
        var surface=new StudySurface(()=>Task.CompletedTask,()=>{},true,clock,end:()=>{ends++;return Task.CompletedTask;});
        var owner=new Window{Content=surface,Width=1440,Height=810,WindowStyle=WindowStyle.None};
        T Element<T>(string name)where T:FrameworkElement=>(T)surface.FindName(name);
        var state=StudyFixture();state=state with{Execution=state.Execution! with{Owner="Manual"},Manual=new(Guid.NewGuid(),1,state.Execution!.SessionId,"Active",null,null,null,false,null)};
        var noise=new NoiseState(Guid.NewGuid(),1,"Active","synthetic","fixture","合成麦克风",null,-57,"Good",new(600,600,1,-60,-40,0,100),[],SessionId:state.Execution!.SessionId);
        try
        {
            Exercise(owner,w=>{
                surface.Apply(state,noise);
                Assert(Element<Button>("EndButton").Visibility==Visibility.Visible && Element<Button>("EndButton").IsEnabled,"manual long-end button missing");
                Assert(Element<Button>("SeatingButton").Visibility==Visibility.Collapsed,"manual study exposed seating control");
                Assert(Element<TextBlock>("ProgressPercent").Text=="" && Element<TextBlock>("RemainingLabel").Text=="已进行","three-hour ceiling was misrepresented as planned progress");
                Assert(Element<TextBlock>("HoldHint").Text.Contains("不自动返回"),"no-auto-return setting ignored");Snapshot(w,"manual-no-end.png");
                state=state with{Manual=state.Manual! with{HasPlannedEnd=true,HideSeconds=600}};surface.Apply(state,noise);
                Assert(Element<TextBlock>("ProgressPercent").Text.EndsWith("%") && Element<TextBlock>("HoldHint").Text.Contains("10 分钟"),"planned end/default hide ignored");Snapshot(w,"manual-planned-end.png");
                state=state with{Threshold=new(noise.SessionId!.Value,"High")};surface.Apply(state,noise);
                Assert(((FrameworkElement)surface.FindName("NoiseEdge")).Visibility==Visibility.Visible,"real manual study lost double-threshold visualization");
                var begin=typeof(StudySurface).GetMethod("BeginEndHold",BindingFlags.Instance|BindingFlags.NonPublic)!;
                begin.Invoke(surface,null);clock.Ms+=1900;surface.Apply(state,noise);Assert(ends==0,"short end press ended session");surface.CancelHold();
                begin.Invoke(surface,null);for(int i=0;i<4;i++){clock.Ms+=500;surface.Apply(state,noise);}PumpUntil(()=>ends==1,"long end press did not execute");
                Assert(ends==1,"long end repeated");Checks.Add("Manual display: no seating, long end even with planned end, optional progress, 10 min/no return; synthetic only");w.Close();
            });
        }
        finally{surface.Stop();owner.Close();}
        var settings=new NoiseWindow("NPEduTools.Test.manual-ui."+Guid.NewGuid().ToString("N"));
        try{Exercise(settings,w=>{
            Assert(((CheckBox)settings.FindName("UseStart")).IsChecked!=true && ((CheckBox)settings.FindName("UseEnd")).IsChecked!=true,"optional dates became mandatory defaults");
            Assert(((TextBox)settings.FindName("ManualHideMinutes")).Text=="10","manual hide default is not ten minutes");
            var date=(DatePicker)settings.FindName("ManualStartDate");date.ApplyTemplate();Assert(date.Template.FindName("InputSurface",date) is Border,"manual date lost common input style");
            Snapshot(w,"manual-settings.png");Checks.Add("Manual settings: optional date/time, ten-minute default and shared native styles; no Host/devices");settings.Shutdown();
        });}finally{settings.Shutdown();}
    }
}

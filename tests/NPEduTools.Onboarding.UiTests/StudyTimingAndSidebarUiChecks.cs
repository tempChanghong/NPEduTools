using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using NPEduTools.App;
using NPEduTools.Contracts;
using NPEduTools.Core;
internal static partial class Program
{
    private static void CheckCompactDragBounds(QuickAccessWindow quick)
    {
        const BindingFlags flags=BindingFlags.NonPublic|BindingFlags.Instance;
        var screen=typeof(QuickAccessWindow).GetProperty("Screen",flags)!.GetValue(quick)!;
        var area=(System.Drawing.Rectangle)screen.GetType().GetProperty("WorkingArea")!.GetValue(screen)!;
        void Invoke(string name,params object[] args)=>typeof(QuickAccessWindow).GetMethod(name,flags)!.Invoke(quick,args);
        var start=quick.PointToScreen(new Point(40,20));var top=quick.PointToScreen(new Point(0,0));
        double height=quick.PointToScreen(new Point(0,quick.ActualHeight)).Y-top.Y;Invoke("BeginDrag",start);
        Invoke("MoveHandle",new Point(start.X,area.Bottom-height+start.Y-top.Y));quick.UpdateLayout();
        double bottom=quick.PointToScreen(new Point(0,quick.ActualHeight)).Y;
        Assert(Math.Abs(bottom-area.Bottom)<=2,$"compact handle could not reach lower work area: bottom={bottom}, expected={area.Bottom}");
        Invoke("CancelDrag");
        quick.SetAutoHidden(true);quick.SetAutoHidden(false);quick.UpdateLayout();
        Assert(Math.Abs(quick.PointToScreen(new Point(0,quick.ActualHeight)).Y-bottom)<=2,"auto-hide changed compact drag placement");
    }
    private static void RunStudySidebarOwnershipChecks()
    {
        const BindingFlags flags=BindingFlags.NonPublic|BindingFlags.Instance;
        string pipe="NPEduTools.Test.sidebar-ownership."+Guid.NewGuid().ToString("N");
        var main=IsolatedMainWindow(pipe);var quick=new QuickAccessWindow(pipe,()=>{},()=>{},()=>{},()=>{},_=>{},()=>{},()=>{});
        var study=new StudyWindow(pipe,()=>{},true){Width=320,Height=240};var report=new StudyReportWindow{Width=320,Height=240};
        void Field(string name,object? value)=>typeof(MainWindow).GetField(name,flags)!.SetValue(main,value);
        void Refresh()=>typeof(MainWindow).GetMethod("UpdateQuickPresentationVisibility",flags)!.Invoke(main,null);
        try {
            Field("_quick",quick);Field("_studyWindow",study);Field("_studyReportWindow",report);quick.Show();
            Refresh();Assert(quick.IsVisible,"ordinary main/editor state hid rail");
            Field("_quickProjectionAt",System.Diagnostics.Stopwatch.GetTimestamp());Refresh();Assert(!quick.IsVisible,"projector did not hide rail");
            study.ShowDisplay();Field("_quickProjectionAt",null);Refresh();Assert(!quick.IsVisible,"projector-to-study transition restored rail");
            study.HideDisplay();Refresh();Assert(quick.IsVisible,"temporary computer use did not restore rail");
            report.Show();Refresh();Assert(!quick.IsVisible,"study report failed to hide rail");report.HideDisplay();Refresh();Assert(quick.IsVisible && !quick.IsActive,"report exit activated or failed to restore rail");
            Click(main,"TeachingSettingsTab");((TextBox)main.FindName("ManualDisplayLead")).Text="1.5";Click(main,"SaveManualDisplayLeadButton");
            Assert(new ManualStudyPreferencesStore(ManualStudyPreferencesStore.PathFor(pipe)).Read().DisplayLeadSeconds==90,"settings page did not save local lead without management verification");
            ((TextBox)main.FindName("ManualDisplayLead")).Text="invalid";Click(main,"SaveManualDisplayLeadButton");
            Assert(new ManualStudyPreferencesStore(ManualStudyPreferencesStore.PathFor(pipe)).Read().DisplayLeadSeconds==90,"invalid setting overwrote previous preference");
            string preference=ManualStudyPreferencesStore.PathFor(pipe), damaged="{\"Version\":1,\"DisplayLeadSeconds\":-1}";
            File.WriteAllText(preference,damaged);Click(main,"TeachingSettingsTab");
            Assert(((TextBox)main.FindName("ManualDisplayLead")).Text=="" && ((TextBlock)main.FindName("ManualDisplayLeadMessage")).Text.Contains("无法读取"),"damaged preference did not leave a recoverable settings page");
            Assert(File.ReadAllText(preference)==damaged,"opening settings overwrote damaged preference");
            ((TextBox)main.FindName("ManualDisplayLead")).Text="1.5";Click(main,"SaveManualDisplayLeadButton");
            Assert(new ManualStudyPreferencesStore(preference).Read().DisplayLeadSeconds==90,"explicit confirmation could not repair damaged preference");
        }finally{study.Shutdown();report.Shutdown();quick.Shutdown();main.Close();var path=ManualStudyPreferencesStore.PathFor(pipe);if(File.Exists(path))File.Delete(path);}
    }
    private static void RunStudyTimingAndSidebarChecks()
    {
        var clock=new StudyUiClock();var state=StudyFixture("Preparing",-300);var surface=new StudySurface(()=>Task.CompletedTask,()=>{},true,clock);
        var window=new Window{Content=surface,Width=1100,Height=700};
        try {Exercise(window,_=>{
            surface.Apply(state,null);surface.SeatingScope("isolated");surface.ApplySeating("initial",1,SeatFrame(SeatImage(Colors.White)));
            var overlay=(FrameworkElement)surface.FindName("SeatingOverlay");var stage=(Border)surface.FindName("StageNotice");
            Assert(overlay.Visibility==Visibility.Visible && stage.Visibility==Visibility.Collapsed,"T-5 seat image did not open independently of cue");
            Snapshot(window,"study-early-seating.png");
            state=state with{SchoolNow=state.Window!.Start.AddSeconds(-120)};clock.Ms=180000;surface.Apply(state,null);
            Assert(stage.Visibility==Visibility.Visible,"T-2 cue absent");
            clock.Ms=190000;surface.Apply(state with{SchoolNow=state.Window!.Start.AddSeconds(-110)},null);
            Assert(stage.Visibility==Visibility.Collapsed && overlay.Visibility==Visibility.Visible,"ten-second cue did not return to seats");
            clock.Ms=300000;state=state with{Phase="Active",SchoolNow=state.Window!.Start};surface.Apply(state,null);Assert(stage.Visibility==Visibility.Visible,"start cue did not replace preparation");
            clock.Ms=305000;surface.Apply(state with{SchoolNow=state.Window!.Start.AddSeconds(5)},null);Assert(stage.Visibility==Visibility.Collapsed && overlay.Visibility==Visibility.Visible,"start cue did not return to seats");
            clock.Ms=420000;surface.Apply(state with{SchoolNow=state.Window!.Start.AddSeconds(120)},null);Assert(overlay.Visibility==Visibility.Collapsed,"T+2 auto seat did not close");
            ((Button)surface.FindName("SeatingButton")).RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
            surface.Apply(state with{SchoolNow=state.Window!.Start.AddSeconds(121)},null);Assert(overlay.Visibility==Visibility.Visible,"T+2 boundary closed explicit viewing");
            ((Button)surface.FindName("CloseSeating")).RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
            surface.Stop(); window.Close();
        });}finally{surface.Stop();window.Close();}
        surface=new(()=>Task.CompletedTask,()=>{},true,clock);state=StudyFixture("Preparing",-299);
        try{
            surface.Apply(state with{Mode="Exam",ClockSource="Windows"},null);
            Assert(((TextBlock)surface.FindName("DateSource")).Text.Contains("Windows"),"exam clock was labelled ClassIsland");
            surface.Apply(state,null);Assert(((FrameworkElement)surface.FindName("SeatingOverlay")).Visibility==Visibility.Collapsed,"missing seat opened empty full-screen image");
            surface.ApplySeating("late",1,SeatFrame(SeatImage(Colors.White)));Assert(surface.SeatingHash=="late","late download lost image");
            ((Button)surface.FindName("CloseSeating")).RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
            surface.Apply(state,null);surface.ApplySeating("replacement",2,SeatFrame(SeatImage(Colors.Gray)));
            Assert(((FrameworkElement)surface.FindName("SeatingOverlay")).Visibility==Visibility.Collapsed,"manual dismissal was undone by policy pulse/replacement");
            surface.Suspend();surface.Apply(state,null);Assert(((FrameworkElement)surface.FindName("SeatingOverlay")).Visibility==Visibility.Collapsed,"priority return undid manual dismissal");
        }finally{surface.Stop();}
        string pipe="NPEduTools.Test.sidebar."+Guid.NewGuid().ToString("N");
        var q=new QuickAccessWindow(pipe,()=>{},()=>{},()=>{},()=>{},_=>{},()=>{},()=>{});
        string placement=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"NPEduTools","ui",Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(pipe)))[..24]+".json");
        try{q.Show();q.UpdateLayout();
            q.SetCompact(true);Assert(((Button)q.FindName("RailTools")).Visibility==Visibility.Collapsed,"compact rail retained tool stack");Assert(q.IsVisible,"compact rail fully disappeared");
            CheckCompactDragBounds(q);
            q.SetAutoHidden(true);Assert(!q.IsVisible,"full-screen suppression retained handle");q.ShowEntry();q.OpenPanel();Assert(!q.IsVisible,"tray/panel action bypassed full-screen suppression");
            q.SetAutoHidden(false);Assert(q.IsVisible && ((Button)q.FindName("RailTools")).Visibility==Visibility.Collapsed,"restore discarded prior compact state");
            q.SetCompact(false);q.SetAutoHidden(true);q.SetAutoHidden(false);Assert(((Button)q.FindName("RailTools")).Visibility==Visibility.Visible,"normal rail did not restore");
            Snapshot(q,"quick-access-rail.png");q.SetCompact(true);Snapshot(q,"quick-access-handle.png");q.Shutdown();
        }finally{q.Shutdown();if(File.Exists(placement))File.Delete(placement);}
        RunStudySidebarOwnershipChecks();
        Checks.Add("Study timing: actual WPF T-5 seats, T-2 ten-second cue, start five seconds, T+2 dismissal, missing/late seats, manual dismissal/replacement and sidebar compact/full-hide/restore; synthetic, muted, no sensors");
    }
}

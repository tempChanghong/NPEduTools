using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using NPEduTools.App;
using NPEduTools.Contracts;

internal static partial class Program
{
    [StructLayout(LayoutKind.Sequential)] private struct BoundsRect { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] private static extern bool GetWindowRect(nint hwnd, out BoundsRect rect);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(nint hwnd, nint after, int x, int y, int w, int h, uint flags);
    private static void RunDesktopWindowBoundsChecks()
    {
        var assembly = typeof(MainWindow).Assembly;
        var boundsType = assembly.GetType("NPEduTools.App.DesktopBounds")!;
        var placement = assembly.GetType("NPEduTools.App.DesktopWindowBounds")!;
        var constrain = placement.GetMethod("Constrain", BindingFlags.Static | BindingFlags.NonPublic)!;
        var sizing = assembly.GetType("NPEduTools.App.DesktopDialogSizing")!;
        var attached = sizing.GetMethod("IsAttached", BindingFlags.Static | BindingFlags.NonPublic)!;
        object Bounds(double x, double y, double w, double h) => Activator.CreateInstance(boundsType, [x,y,w,h])!;
        double Value(object b, string key) => (double)boundsType.GetProperty(key)!.GetValue(b)!;
        foreach (var area in new[] { Bounds(0,0,2560,1360), Bounds(-1920,40,1920,1000), Bounds(2560,-1440,3840,1380) })
        {
            double x=Value(area,"Left"), y=Value(area,"Top"), w=Value(area,"Width"), h=Value(area,"Height");
            foreach (var input in new[] {Bounds(x-400,y-500,1200,1600),Bounds(x+w-80,y+h-60,900,700),Bounds(x,y,w*2,h*2)})
            {
                var output=constrain.Invoke(null,[input,area])!;
                Assert(Value(output,"Left")>=x && Value(output,"Top")>=y && Value(output,"Left")+Value(output,"Width")<=x+w && Value(output,"Top")+Value(output,"Height")<=y+h,
                    "caption/outer frame escapes work area in negative or mixed-DPI monitor coordinates");
            }
        }
        Checks.Add("Native pixel geometry: 2K/200% work area, negative monitor origins, mixed-DPI topology and oversized bounds");

        string pipe="NPEduTools.Test.window-bounds."+Guid.NewGuid().ToString("N");
        var clientType=assembly.GetType("NPEduTools.App.RecordingClient")!;
        var client=Activator.CreateInstance(clientType,[Application.Current.Dispatcher,pipe])!;
        clientType.GetMethod("Detach")!.Invoke(client,null);
        Func<Task<RecordingEnvironment>> probe=()=>Task.FromResult(new RecordingEnvironment(true,null,[new("fixture","隔离测试屏幕",0,0,1920,1080,true)],[],[]));
        var recordingCtor=typeof(RecordingWindow).GetConstructors(BindingFlags.NonPublic|BindingFlags.Instance).Single(c=>c.GetParameters().Length==3);
        var automaticCtor=typeof(AutoRecordingWindow).GetConstructors(BindingFlags.NonPublic|BindingFlags.Instance).Single();
        var factories = new (string Name, Func<Window> Create)[] {
            ("agreements",()=>new AgreementsWindow(pipe,false)),
            ("onboarding",()=>PreparationWindow(pipe,new(),_=>{})),
            ("study",()=>new StudyMonitoringWindow(pipe,()=>{})),
            ("manual-study",()=>new NoiseWindow(pipe)),
            ("seating",()=>new StudySeatingUploadWindow(pipe)),
            ("classroom",()=>new ClassroomModeWindow(pipe,()=>{},()=>{},()=>{})),
            ("examaware",()=>new ExamAwareWindow(pipe)),
            ("secrandom",()=>new SecRandomWindow(pipe)),
            ("recording",()=>(Window)recordingCtor.Invoke([client,pipe,probe])),
            ("automatic-recording",()=>(Window)automaticCtor.Invoke([pipe,client,(Action)(()=>{})])),
            ("quick-calibration",()=>new NoiseCalibrationWindow(pipe)),
            ("reference-calibration",()=>new NoiseReferenceWindow(pipe)),
            ("usb-meter",()=>new SoundMeterWindow(pipe)),
            ("usb-reference",()=>new NoiseReferenceWindow(pipe,"isolated-meter")),
            ("shortcut",()=>new ShortcutEditorWindow())
        };
        using var ownerScope=new BoundsOwnerScope();
        foreach(var item in factories)
        {
            var dialog=item.Create();dialog.Owner=ownerScope.Window;
            double originalWidth=dialog.Width, originalHeight=dialog.Height;
            Assert((bool)attached.Invoke(null,[dialog])!,item.Name+" missed common sizing");
            ExerciseBounds(dialog,window=>{
                window.UpdateLayout();
                var handle=new WindowInteropHelper(window).Handle;
                var screenType=Type.GetType("System.Windows.Forms.Screen, System.Windows.Forms")!;
                var screen=screenType.GetMethod("FromHandle")!.Invoke(null,[handle])!;
                var area=(System.Drawing.Rectangle)screenType.GetProperty("WorkingArea")!.GetValue(screen)!;
                Assert(GetWindowRect(handle,out var rect),"window rectangle unavailable");
                Assert(rect.Top>=area.Top && rect.Left>=area.Left && rect.Bottom<=area.Bottom && rect.Right<=area.Right,item.Name+" caption/frame off screen after centering");
                double previousScale=((ScaleTransform)((FrameworkElement)window.Content).LayoutTransform).ScaleX;
                double designW=double.IsFinite(originalWidth)?originalWidth:window.Width/previousScale;
                double designH=double.IsFinite(originalHeight)?originalHeight:window.Height/previousScale;
                // Simulate 2560x1440 / 200% minus taskbar without changing desktop settings.
                double scale=(double)assembly.GetType("NPEduTools.App.DesktopDensity")!.GetMethod("Resolve")!.Invoke(null,[0d,1280d,680d,designW,designH])!;
                ((FrameworkElement)window.Content).LayoutTransform=new ScaleTransform(scale,scale);
                window.MinWidth=Math.Min(window.MinWidth*scale,1264);window.MinHeight=Math.Min(window.MinHeight*scale,664);
                window.Width=Math.Min(designW*scale,1264);window.Height=Math.Min(designH*scale,664);window.UpdateLayout();
                Assert(window.ActualHeight<=664 && window.ActualWidth<=1264,item.Name+" cannot shrink within simulated 200% work area");
                if(item.Name is "study" or "reference-calibration" or "usb-meter" or "usb-reference" or "automatic-recording" or "shortcut") Snapshot(window,item.Name+"-simulated-200.png");
                if(item.Name=="usb-reference")
                {
                    ((ComboBox)window.FindName("WorkflowChoice")).SelectedValue="Stable";
                    Assert(((Expander)window.FindName("AdvancedSettings")).IsExpanded,"strict calibration hides required engineering inputs");
                    var render=typeof(NoiseReferenceWindow).GetMethod("Render",BindingFlags.NonPublic|BindingFlags.Instance|BindingFlags.DeclaredOnly)!;
                    var state=new NoiseReferenceState(Guid.NewGuid(),1,"Ready","fixture","隔离麦克风",0,0,null,new(false,null,null,"等待采样"),null,false,null);
                    render.Invoke(window,[state]);
                    ((TextBlock)window.FindName("OperationMessage")).Text="上一段采样已受理";
                    render.Invoke(window,[state with {Revision=2}]);
                    Assert(((TextBlock)window.FindName("OperationMessage")).Text.Length==0,"advanced state retains an obsolete accepted receipt");
                    typeof(NoiseReferenceWindow).GetField("_connected",BindingFlags.NonPublic|BindingFlags.Instance)!.SetValue(window,true);
                    var draft=new NoiseReferenceDraft(Guid.NewGuid(),"fixture","DLY-2202","Slow",1,1,null,[],"isolated-meter","Stable");
                    var configured=state with {Revision=3,Draft=draft};
                    render.Invoke(window,[configured]);
                    Assert(((FrameworkElement)window.FindName("SamplePanel")).IsVisible,"restored draft did not reach sampling");
                    Click(window,"SetupStep");
                    ((ComboBox)window.FindName("WorkflowChoice")).SelectedValue="Field";
                    render.Invoke(window,[configured]);
                    Assert(((TextBlock)window.FindName("LimitInstructions")).Text.Contains("0.1–20"),"old draft poll overwrote selected setup instructions");
                    typeof(NoiseReferenceWindow).GetField("_connected",BindingFlags.NonPublic|BindingFlags.Instance)!.SetValue(window,false);
                    typeof(NoiseReferenceWindow).GetMethod("UpdateButtons",BindingFlags.NonPublic|BindingFlags.Instance)!.Invoke(window,null);
                    Assert(!Button(window,"SamplingStep").IsEnabled && Button(window,"SetupStep").IsEnabled,"disconnected tabs are enabled but cannot act");
                    typeof(NoiseReferenceWindow).GetField("_connected",BindingFlags.NonPublic|BindingFlags.Instance)!.SetValue(window,true);
                    render.Invoke(window,[state with {InstanceId=Guid.NewGuid(),Revision=0}]);
                    Assert(((FrameworkElement)window.FindName("PreparePanel")).IsVisible && !((FrameworkElement)window.FindName("SamplePanel")).IsVisible,"lost draft traps calibration in a disabled sampling step");
                    Checks.Add("USB recovery: missing/restarted draft returns to setup, selected workflow instructions survive old-draft polls, disconnected tabs match actions");
                    Checks.Add("USB workflow: strict inputs expand; newer state withdraws obsolete operation receipt; no sensor commands");
                    ((ScrollViewer)window.Content).ScrollToBottom();window.UpdateLayout();
                    Snapshot(window,"usb-reference-strict-settings.png");
                }
                // Exercise native recovery of a title bar deliberately moved off screen.
                SetWindowPos(handle,0,area.Left-200,area.Top-600,(int)window.ActualWidth,(int)window.ActualHeight,0x0214);
                placement.GetMethod("KeepVisible",BindingFlags.Static|BindingFlags.NonPublic)!.Invoke(null,[window]);
                Assert(GetWindowRect(handle,out rect) && rect.Top>=area.Top && rect.Left>=area.Left,item.Name+" did not recover off-screen title bar");
                if(item.Name=="reference-calibration")
                {
                    window.WindowState=WindowState.Minimized;window.WindowState=WindowState.Normal;
                    PumpUntil(()=>GetWindowRect(handle,out var restored) && restored.Top>=area.Top && restored.Bottom<=area.Bottom,"restoring dialog lost its title bar");
                    window.Hide();window.Top=-1000;window.Show();
                    PumpUntil(()=>GetWindowRect(handle,out var restored) && restored.Top>=area.Top && restored.Bottom<=area.Bottom,"reopening dialog lost its title bar");
                }
                var shutdown=window.GetType().GetMethod("Shutdown",BindingFlags.Public|BindingFlags.Instance) ?? window.GetType().GetMethod("Dismiss",BindingFlags.Public|BindingFlags.Instance);
                if(shutdown is not null) shutdown.Invoke(window,null);else window.Close();
            });
            Checks.Add(item.Name+": common sizing, native caption visibility, simulated 2K/200%, off-screen recovery");
        }
        // Automatic height must remain scrollable when validation text expands.
        var shortcut=new ShortcutEditorWindow();
        ExerciseBounds(shortcut,window=>{
            Assert(window.SizeToContent==SizeToContent.Manual && window.Content is ScrollViewer,"auto-size dialog still grows outside work area");
            ((TextBlock)window.FindName("ErrorText")).Text=string.Join("\n",Enumerable.Repeat("隔离测试：长错误消息",80));window.UpdateLayout();
            PumpUntil(()=>((ScrollViewer)window.Content).ScrollableHeight>0,"long validation text has no reachable scroll viewport");
            Snapshot(window,"shortcut-long-error-scroll.png");window.Close();
        });
        Checks.Add("Auto-height shortcut editor keeps long validation text reachable through scrolling");
    }
    private static void ExerciseBounds(Window window,Action<Window> test)
    {
        Exception? failure=null;
        // ScrollViewer's extent is published by LayoutUpdated after Loaded finishes.
        window.Loaded+=(_,_)=>window.Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.ApplicationIdle,(Action)(()=>{
            try { test(window); }
            catch(Exception error) { failure=error;var end=window.GetType().GetMethod("Shutdown") ?? window.GetType().GetMethod("Dismiss");if(end is not null)end.Invoke(window,null);else window.Close(); }
        }));
        window.ShowDialog();if(failure is not null)throw failure;
    }
    private sealed class BoundsOwnerScope : IDisposable
    {
        public Window Window {get;}=new(){Title="隔离窗口尺寸测试",Width=420,Height=300,WindowStartupLocation=WindowStartupLocation.Manual,Left=0,Top=0,Content=new Grid()};
        public BoundsOwnerScope(){Window.Show();SetWindowPos(new WindowInteropHelper(Window).Handle,0,-450,-700,420,300,0x0214);}
        public void Dispose()=>Window.Close();
    }
}

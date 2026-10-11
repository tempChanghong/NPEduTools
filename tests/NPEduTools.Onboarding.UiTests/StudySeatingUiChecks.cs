using System.IO;
using System.IO.Pipes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using NPEduTools.App;
using NPEduTools.Contracts;
internal static partial class Program
{
    private static byte[] SeatImage(Color color, int width = 800, int height = 480)
    {
        var bitmap=new WriteableBitmap(width,height,96,96,PixelFormats.Bgra32,null);var pixels=new byte[width*height*4];
        for(int i=0;i<pixels.Length;i+=4) { pixels[i]=color.B;pixels[i+1]=color.G;pixels[i+2]=color.R;pixels[i+3]=255; }
        bitmap.WritePixels(new Int32Rect(0,0,width,height),pixels,width*4,0);
        var visual = new DrawingVisual();
        using (var draw = visual.RenderOpen()) {
            draw.DrawImage(bitmap, new Rect(0,0,width,height));
            if (width == 800) {
                var text = new FormattedText("自习座位表测试样张 · 非真实学生数据", System.Globalization.CultureInfo.GetCultureInfo("zh-CN"), FlowDirection.LeftToRight, new Typeface("Microsoft YaHei UI"), 24, Brushes.Black, 1);
                draw.DrawText(text, new Point(30,24));
                for(int row=0;row<4;row++) for(int col=0;col<6;col++) {
                    var rect=new Rect(30+col*125,100+row*85,105,60);draw.DrawRoundedRectangle(null,new Pen(Brushes.Gray,1),rect,4,4);
                    draw.DrawText(new FormattedText($"A{row*6+col+1:00}",System.Globalization.CultureInfo.InvariantCulture,FlowDirection.LeftToRight,new Typeface("Segoe UI"),22,Brushes.Black,1),new Point(rect.X+29,rect.Y+15));
                }
            }
        }
        var rendered = new RenderTargetBitmap(width,height,96,96,PixelFormats.Pbgra32);rendered.Render(visual);
        var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(rendered));using var buffer=new MemoryStream();encoder.Save(buffer);return buffer.ToArray();
    }
    private static BitmapFrame SeatFrame(byte[] bytes) { using var stream=new MemoryStream(bytes);var frame=BitmapFrame.Create(stream,BitmapCreateOptions.None,BitmapCacheOption.OnLoad);frame.Freeze();return frame; }
    private static void RunSeatingVisualChecks()
    {
        var clock=new StudyUiClock();var state=StudyFixture();var surface=new StudySurface(()=>Task.CompletedTask,()=>{},true,clock);
        var window=new Window {Content=surface,Width=1100,Height=700};
        try { Exercise(window,_=>{
            surface.Apply(state,null);clock.Ms=6000;surface.Apply(state with {SchoolNow=state.SchoolNow!.Value.AddSeconds(6)},null);
            surface.SeatingScope("one");surface.ApplySeating("a",1,SeatFrame(SeatImage(Colors.White)));
            var overlay=(FrameworkElement)surface.FindName("SeatingOverlay");var button=(Button)surface.FindName("SeatingButton");
            Assert(overlay.Visibility==Visibility.Collapsed,"seat download auto-opened");Assert(button.IsEnabled,"seat button did not become available");
            button.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));Assert(overlay.Visibility==Visibility.Visible && surface.SeatingRevision==1,"seat button did not open loaded image");
            surface.ApplySeating("b",2,SeatFrame(SeatImage(Colors.LightGray)));Assert(surface.SeatingRevision==1,"replacement interrupted current reader");
            Snapshot(window,"study-seating-button-view.png");
            ((Button)surface.FindName("CloseSeating")).RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
            Assert(overlay.Visibility==Visibility.Collapsed && surface.SeatingRevision==2,"close did not adopt pending seat image");
            clock.Ms+=61000;surface.Apply(state with {SchoolNow=state.SchoolNow!.Value.AddSeconds(67)},null);
            Assert(overlay.Visibility==Visibility.Collapsed,"homework timer opened seat image");
            button.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));surface.Suspend();Assert(overlay.Visibility==Visibility.Collapsed,"temporary hide retained an open seat view");
            surface.Apply(state,null);Assert(overlay.Visibility==Visibility.Collapsed,"return reopened seat image");
            surface.ClearSeating();Assert(surface.SeatingHash is null && !button.IsEnabled,"withdraw did not clear rendered image");
            surface.Stop();Checks.Add("Seating WPF: button-only open, old image held until close, no rotation/return auto-open, withdrawal clears");window.Close();
        }); } finally {surface.Stop();window.Close();}
    }
    private static void RunSeatingPipeChecks()
    {
        string pipe="NPEduTools.Test.seating."+Guid.NewGuid().ToString("N");var fixturePng=Environment.GetEnvironmentVariable("NPEP_SEATING_FIXTURE_PNG");
        var bytes=fixturePng is null?SeatImage(Colors.White):File.ReadAllBytes(fixturePng);var hash=Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes));
        int count=(bytes.Length+16383)/16384;var state=StudyFixture() with {Seating=new("READY",new string('a',64),1,hash,count,bytes.Length,800,480)};
        using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var server=Task.Run(async()=>{for(int i=0;i<count;i++) { using var stream=new NamedPipeServerStream(pipe,PipeDirection.InOut,1,PipeTransmissionMode.Byte,PipeOptions.Asynchronous|PipeOptions.CurrentUserOnly);
            await stream.WaitForConnectionAsync(timeout.Token);var request=await Protocol.ReadAsync<HostRequest>(stream,timeout.Token);
            Assert(request.Capability=="study.seating" && Protocol.Validate(request) is null,"seat IPC used an invalid or sensor command");
            await Protocol.WriteAsync(stream,new HostResponse(1,request.RequestId,"Succeeded",null,"fixture",SeatingChunk:new(1,hash,i,count,Convert.ToBase64String(bytes,i*16384,Math.Min(16384,bytes.Length-i*16384)))),timeout.Token);
        }});
        var window=new StudyWindow(pipe,()=>{},true) {Width=1100,Height=700};
        try {Exercise(window,_=>{window.Apply(state,null);var surface=(StudySurface)window.Content;
            PumpUntil(()=>surface.SeatingHash==hash,"actual Named Pipe image did not decode into WPF");
            Assert(((FrameworkElement)surface.FindName("SeatingOverlay")).Visibility==Visibility.Collapsed,"IPC download auto-opened image");
            window.Apply(state with {Seating=new("NOT_PUBLISHED",state.Version,2,null,0,0,0,0)},null);Assert(surface.SeatingHash is null,"withdrawal did not clear actual decoded image");
            PumpUntil(()=>server.IsCompleted,"seat pipe did not settle");server.GetAwaiter().GetResult();window.Shutdown();
            Checks.Add("Seating pipe: actual private Named Pipe, SHA256/full PNG decode into WPF, no auto-open, withdrawal clears; no sensors; chunks="+count);
        });}finally{timeout.Cancel();window.Shutdown();}
    }
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static (WeakReference Retired, WeakReference Current, WeakReference Pending) LoadSeatingMemory(StudySurface surface)
    {
        surface.SeatingScope("memory");
        var one=SeatFrame(SeatImage(Colors.White,2000,3000));surface.ApplySeating("one",1,one);
        ((Button)surface.FindName("SeatingButton")).RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
        var two=SeatFrame(SeatImage(Colors.Gray,2000,3000));surface.ApplySeating("two",2,two);
        var three=SeatFrame(SeatImage(Colors.LightGray,2000,3000));surface.ApplySeating("three",3,three);
        return (new(two),new(one),new(three));
    }
    private static void RunSeatingMemoryChecks()
    {
        var clock=new StudyUiClock();var state=StudyFixture();var surface=new StudySurface(()=>Task.CompletedTask,()=>{},true,clock);
        var window=new Window {Content=surface,Width=1100,Height=700};
        try {Exercise(window,_=>{
            surface.Apply(state,null);clock.Ms=6000;surface.Apply(state with {SchoolNow=state.SchoolNow!.Value.AddSeconds(6)},null);
            long before=GC.GetTotalMemory(true);var references=LoadSeatingMemory(surface);
            GC.Collect();GC.WaitForPendingFinalizers();GC.Collect();
            Assert(!references.Retired.IsAlive && references.Current.IsAlive && references.Pending.IsAlive,"seat replacement retained more than current plus pending images");
            using var process=System.Diagnostics.Process.GetCurrentProcess();process.Refresh();
            long loaded=GC.GetTotalMemory(false), workingSet=process.WorkingSet64, privateBytes=process.PrivateMemorySize64;
            surface.Stop();GC.Collect();GC.WaitForPendingFinalizers();GC.Collect();
            Assert(!references.Current.IsAlive && !references.Pending.IsAlive,"seat stop retained decoded images");
            File.WriteAllText(Path.Combine(_output,"seating-memory-fixture.json"),System.Text.Json.JsonSerializer.Serialize(new {
                kind="isolated-WPF-two-6MP-images-after-forced-GC",maximumPixels=6000000,retainedDecodedImages=2,
                workingSetBytes=workingSet,privateBytes,managedBefore=before,managedLoaded=loaded,managedAfterClear=GC.GetTotalMemory(false),
                retiredImageReleased=true,currentAndPendingReleased=true,fullApp=false,realDevices=false}));
            Checks.Add("Seating memory: at most current plus pending 6MP images; retired and stopped image objects reclaimed; isolated WPF only");window.Close();
        });}finally{surface.Stop();window.Close();}
    }

}

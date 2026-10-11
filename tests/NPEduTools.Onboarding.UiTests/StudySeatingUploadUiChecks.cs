using System.IO;
using System.IO.Pipes;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using NPEduTools.App;
using NPEduTools.Contracts;
using NPEduTools.Core;
internal static partial class Program
{
    private static void RunSeatingUploadChecks()
    {
        var jpeg=new JpegBitmapEncoder();
        var metadata=new BitmapMetadata("jpg");metadata.Comment="private sample metadata";
        jpeg.Frames.Add(BitmapFrame.Create(SeatFrame(SeatImage(Colors.White)),null,metadata,null));
        using var raw=new MemoryStream();jpeg.Save(raw);
        var draft=StudySeatingImage.Normalize(raw.ToArray());Assert(draft.Preview.IsFrozen && draft.Preview.PixelWidth<=960,"preview is not bounded/frozen");
        Assert(draft.Width==800 && draft.Height==480 && draft.Png.Length<=2097152,"normalization bounds changed");
        for(int offset=8;offset<draft.Png.Length;) {
            var type=System.Text.Encoding.ASCII.GetString(draft.Png,offset+4,4);
            Assert(new[]{"IHDR","IDAT","IEND","sRGB","gAMA","pHYs"}.Contains(type),"source metadata leaked into normalized PNG");
            offset+=12+(int)System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(draft.Png.AsSpan(offset));
        }
        string path=Path.Combine(_output,"native-normalized.png");File.WriteAllBytes(path,draft.Png);
        string source=Path.Combine(_output,"seat-synthetic.jpg");File.WriteAllBytes(source,raw.ToArray());
        try { StudySeatingImage.Normalize(new byte[8388609]);throw new Exception("overlimit image accepted"); }catch(InvalidDataException){}
        string pipe="NPEduTools.Test.upload-ui."+Guid.NewGuid().ToString("N");using var lifetime=new CancellationTokenSource();
        string version=new('a',64);int calls=0,revision=1;Guid? retried=null;StudySeatingUploadCommand? last=null;
        var server=Task.Run(async()=> {
            while(!lifetime.IsCancellationRequested) {
                using var stream=new NamedPipeServerStream(pipe,PipeDirection.InOut,1,PipeTransmissionMode.Byte,PipeOptions.Asynchronous|PipeOptions.CurrentUserOnly);
                await stream.WaitForConnectionAsync(lifetime.Token);
                var request=await Protocol.ReadAsync<HostRequest>(stream,lifetime.Token);
                Assert(Protocol.Validate(request) is null,"actual upload Pipe command invalid");
                HostResponse reply;
                if(request.Capability=="study.seating.upload.status")
                    reply=new(1,request.RequestId,"Succeeded",null,"synthetic",SeatingUpload:new(Guid.NewGuid(),version,"one","本班",revision,null,"Ready"));
                else {
                    var command=request.SeatingUpload!;last=command;
                    var png=StudySeatingTransfer.Read(pipe,command.UploadId,command.ByteCount,command.Hash);Assert(png.SequenceEqual(draft.Png),"Pipe transfer bytes changed");
                    int n=Interlocked.Increment(ref calls);
                    if(n==1){revision=2;reply=new(1,request.RequestId,"Rejected","SEATING_CHANGED","synthetic");}
                    else if(n==2){retried=command.UploadId;continue;} // Request arrived, response was lost.
                    else {Assert(command.UploadId==retried && command.ExpectedRevision==2,"uncertain retry changed publication ID/revision");
                        reply=new(1,request.RequestId,"Succeeded",null,"synthetic",SeatingUpload:new(command.TicketId,version,"one","本班",3,command.Hash,"Saved"));}
                }
                await Protocol.WriteAsync(stream,reply,lifetime.Token);
            }
        });
        var window=new StudySeatingUploadWindow(pipe);
        try {
            Exercise(window,_=>{
                var choose=(Button)window.FindName("ChooseImage");var save=(Button)window.FindName("SaveImage");var refresh=(Button)window.FindName("RefreshScope");
                var info=(TextBlock)window.FindName("ScopeText");var result=(TextBlock)window.FindName("ResultText");
                PumpUntil(()=>info.Text.Contains("版本 1") && refresh.IsEnabled,"upload status did not arrive");
                var load=(Task)typeof(StudySeatingUploadWindow).GetMethod("LoadDraftAsync",BindingFlags.NonPublic|BindingFlags.Instance)!.Invoke(window,[source])!;
                PumpUntil(()=>load.IsCompleted && save.IsEnabled,"draft not ready");load.GetAwaiter().GetResult();
                save.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));PumpUntil(()=>result.Text.Contains("草稿已保留") && refresh.IsEnabled,"conflict did not retain draft");
                Assert(((Image)window.FindName("Preview")).Source is not null && !save.IsEnabled,"conflict silently rebased or lost draft");
                refresh.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));PumpUntil(()=>save.IsEnabled && save.Content.ToString()!.Contains("版本 2"),"explicit refresh did not allow confirm");
                save.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));PumpUntil(()=>save.IsEnabled && save.Content.ToString()!.Contains("重试"),"uncertain outcome not exposed");
                Assert(!choose.IsEnabled && !refresh.IsEnabled,"uncertain result allowed new image/context");
                Snapshot(window,"study-seating-upload-retry.png");
                save.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));PumpUntil(()=>result.Text.StartsWith("服务器已保存") && refresh.IsEnabled,"same operation retry failed");
                Assert(((Image)window.FindName("Preview")).Source is null && calls==3,"save did not retire draft");
                Assert(last is not null && !File.Exists(StudySeatingTransfer.PathFor(pipe,last.UploadId)),"success retained transfer file");
                Snapshot(window,"study-seating-upload-saved.png");window.Close();
            });
            Checks.Add("Seating upload: JPEG normalization strips source metadata; conflict retains draft and requires confirmation; actual Pipe interrupted response retries the same ID; own transfer removed");
        }finally{
            lifetime.Cancel();try{server.GetAwaiter().GetResult();}catch(OperationCanceledException){}
            if(last is not null)StudySeatingTransfer.Delete(pipe,last.UploadId);window.Close();
        }
        var clock=new StudyUiClock();var state=StudyFixture();var surface=new StudySurface(()=>Task.CompletedTask,()=>{},true,clock);var view=new Window{Content=surface,Width=1100,Height=700};
        try{Exercise(view,_=>{
            surface.Apply(state,null);clock.Ms=6000;surface.Apply(state with {SchoolNow=state.SchoolNow!.Value.AddSeconds(6)},null);
            var button=(Button)surface.FindName("SeatingButton");var overlay=(FrameworkElement)surface.FindName("SeatingOverlay");int selected=0;surface.SeatingUploadRequested+=()=>selected++;
            Assert(button.IsEnabled && overlay.Visibility==Visibility.Collapsed,"empty seat cannot be opened manually or auto-opened");
            surface.ClearSeating();Assert(button.IsEnabled,"unpublished/withdrawn metadata disabled first-upload entry");
            button.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));Assert(overlay.Visibility==Visibility.Visible,"button did not open empty seat");
            ((Button)surface.FindName("UploadSeating")).RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));Assert(selected==1,"upload event not explicit");
            surface.ApplySeating(draft.Hash,1,draft.Preview);Assert(((Image)surface.FindName("SeatingImage")).Source is not null,"first image did not fill already-open placeholder");
            surface.Suspend();Assert(overlay.Visibility==Visibility.Collapsed,"hide retained seat overlay");
            surface.Apply(state with {Manual=new(Guid.NewGuid(),0,null,"Active",null,null,null,false,600)},null);
            Assert(button.Visibility==Visibility.Collapsed && !((Button)surface.FindName("UploadSeating")).IsEnabled,"manual study exposed seating upload");
            surface.Stop();view.Close();
        });Checks.Add("Seating: empty placeholder requires button; upload is scheduled-only and explicit; first image fills open placeholder; hide closes viewer");}
        finally{surface.Stop();view.Close();}
    }
}

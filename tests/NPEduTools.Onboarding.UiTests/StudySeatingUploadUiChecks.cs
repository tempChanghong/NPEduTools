using System.Collections.Concurrent;
using System.IO;
using System.IO.Pipes;
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
        CheckSeatingUploadWorkflow(source, draft);
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

    private static void CheckSeatingUploadWorkflow(string source, StudySeatingDraft draft)
    {
        string pipe = "NPEduTools.Test.upload-ui." + Guid.NewGuid().ToString("N");
        using var lifetime = new CancellationTokenSource();
        string version = new('a', 64);
        var first = new StudySeatingUploadState(Guid.NewGuid(), version, "original", "高二（3）班", 0, null, "Ready");
        var rechecked = first with { TicketId = Guid.NewGuid(), Revision = 2 };
        var overwrite = new StudySeatingUploadState(Guid.NewGuid(), version, "current", "高二（5）班", 7, null, "Ready");
        var afterSave = overwrite with { TicketId = Guid.NewGuid(), Revision = 8, Hash = draft.Hash };
        var afterExpired = afterSave with { TicketId = Guid.NewGuid(), Revision = 9 };
        var afterBindingChange = new StudySeatingUploadState(Guid.NewGuid(), version, "new", "高二（6）班", 3, null, "Ready");
        var refusedRetries = new[] { (Scope: afterSave, Code: "UPLOAD_EXPIRED"), (Scope: afterExpired, Code: "BINDING_CHANGED") };
        var statusReplies = new Queue<StudySeatingUploadState?>(
            [first, rechecked, null, overwrite, afterSave, afterExpired, afterBindingChange]);
        var uploadReplies = new Queue<(StudySeatingUploadState Scope, string? Outcome)>(
            [(first, "SEATING_CHANGED"), (overwrite, null), (overwrite, "Saved")]);
        foreach (var (scope, code) in refusedRetries)
        {
            uploadReplies.Enqueue((scope, null));
            uploadReplies.Enqueue((scope, code));
        }
        uploadReplies.Enqueue((afterBindingChange, "Saved"));
        var uploads = new ConcurrentQueue<StudySeatingUploadCommand>();

        var server = Task.Run(async () =>
        {
            while (!lifetime.IsCancellationRequested)
            {
                using var stream = new NamedPipeServerStream(pipe, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await stream.WaitForConnectionAsync(lifetime.Token);
                var request = await Protocol.ReadAsync<HostRequest>(stream, lifetime.Token);
                Assert(Protocol.Validate(request) is null, "actual upload Pipe request invalid");
                HostResponse reply;
                if (request.Capability == "study.seating.upload.status")
                {
                    var scope = statusReplies.Dequeue();
                    if (scope is null) continue; // The current scope could not be refreshed.
                    reply = new(1, request.RequestId, "Succeeded", null, "synthetic", SeatingUpload: scope);
                }
                else
                {
                    Assert(request.Capability == "study.seating.upload", "unexpected seating capability");
                    var command = request.SeatingUpload!;
                    uploads.Enqueue(command);
                    var (scope, outcome) = uploadReplies.Dequeue();
                    Assert(command.TicketId == scope.TicketId && command.Version == scope.Version &&
                        command.ExpectedRevision == scope.Revision, "upload used the wrong confirmed ticket or revision");
                    var png = StudySeatingTransfer.Read(pipe, command.UploadId, command.ByteCount, command.Hash);
                    Assert(png.SequenceEqual(draft.Png), "Pipe transfer bytes changed");
                    if (outcome is null) continue; // Upload arrived, but its reply was lost.
                    reply = outcome == "Saved"
                        ? new(1, request.RequestId, "Succeeded", null, "synthetic",
                            SeatingUpload: scope with { State = "Saved", Revision = scope.Revision + 1, Hash = command.Hash })
                        : new(1, request.RequestId, "Rejected", outcome, "synthetic");
                }
                await Protocol.WriteAsync(stream, reply, lifetime.Token);
            }
        });

        var window = new StudySeatingUploadWindow(pipe);
        try
        {
            Exercise(window, _ =>
            {
                try
                {
                    var choose = Button(window, "ChooseImage");
                    var save = Button(window, "SaveImage");
                    var finish = Button(window, "FinishButton");
                    var refresh = Button(window, "RefreshScope");
                    var preview = (Image)window.FindName("Preview");
                    var info = (TextBlock)window.FindName("ScopeText");
                    var heading = (TextBlock)window.FindName("ResultHeading");
                    var result = (TextBlock)window.FindName("ResultText");

                    void WaitForScope(StudySeatingUploadState scope) =>
                        PumpUntil(() => refresh.IsEnabled && info.Text.Contains(scope.ClassName) &&
                            info.Text.Contains($"版本 {scope.Revision}"), "upload Ready scope did not arrive");
                    void Recheck(StudySeatingUploadState scope)
                    {
                        Click(window, "RefreshScope");
                        WaitForScope(scope);
                        Assert(save.IsVisible && save.IsEnabled && save.Content is string label && label.Contains("覆盖") &&
                            label.Contains($"版本 {scope.Revision}"), "Ready scope did not offer confirmation to overwrite");
                    }
                    void LoadDraft(bool scopeReady = true)
                    {
                        var load = window.LoadDraftAsync(source);
                        PumpUntil(() => load.IsCompleted, "draft load did not complete");
                        load.GetAwaiter().GetResult();
                        Assert(preview.Source is not null && save.IsEnabled == scopeReady, "draft ignored the current scope readiness");
                    }
                    void WaitForRetry(StudySeatingUploadState submitted)
                    {
                        PumpUntil(() => save.IsEnabled, "uncertain upload did not settle");
                        Assert(heading.IsVisible && heading.Text == "等待确认" && save.IsVisible && Equals(save.Content, "重试确认") &&
                            !choose.IsEnabled && !refresh.IsEnabled, "uncertain upload did not expose a dedicated retry state");
                        Assert(info.Text.Contains(submitted.ClassName) && info.Text.Contains($"提交时版本 {submitted.Revision}") &&
                            !info.Text.Contains("当前版本"), "uncertain upload did not label the submitted revision as historical");
                    }
                    void WaitForSaved(StudySeatingUploadState scope)
                    {
                        PumpUntil(() => refresh.IsEnabled, "saved upload did not settle");
                        Assert(heading.IsVisible && heading.Text == "已保存到本班" && info.Text.Contains(scope.ClassName) &&
                            info.Text.Contains($"已保存版本 {scope.Revision + 1}"), "saved outcome did not show its class and revision");
                        Assert(preview.Source is null && !save.IsVisible && !save.IsEnabled, "save did not retire its draft and confirmation action");
                        Assert(finish.IsVisible && finish.IsEnabled && Equals(finish.Content, "完成") &&
                            choose.IsVisible && choose.IsEnabled, "saved outcome did not offer completion and another image");
                    }

                    WaitForScope(first);
                    Assert(!save.IsEnabled, "upload allowed without a draft");
                    LoadDraft();
                    Assert(save.IsVisible && Equals(save.Content, "确认上传"), "first upload confirmation was missing or presented as an overwrite");
                    var originalPreview = preview.Source;

                    Click(window, "SaveImage");
                    PumpUntil(() => refresh.IsEnabled, "version conflict did not settle");
                    Assert(heading.IsVisible && heading.Text == "上传失败" && result.Text.Contains("草稿已保留") &&
                        ReferenceEquals(preview.Source, originalPreview) && !save.IsEnabled,
                        "confirmed version conflict did not retain the draft and require a recheck");
                    Assert(uploads.Count == 1 && !File.Exists(StudySeatingTransfer.PathFor(pipe, uploads.Single().UploadId)),
                        "confirmed rejection reused or retained its transfer");
                    Recheck(rechecked);

                    Click(window, "RefreshScope");
                    PumpUntil(() => refresh.IsEnabled, "failed Ready refresh did not settle");
                    Assert(heading.IsVisible && heading.Text == "无法核对当前版本" &&
                        !info.Text.Contains(rechecked.ClassName) && !info.Text.Contains($"当前版本 {rechecked.Revision}") &&
                        ReferenceEquals(preview.Source, originalPreview) && !save.IsEnabled,
                        "failed refresh left a stale class or revision current, or lost the draft");
                    Recheck(overwrite);

                    Click(window, "SaveImage");
                    WaitForRetry(overwrite);
                    Snapshot(window, "study-seating-upload-retry.png");
                    Click(window, "SaveImage");
                    WaitForSaved(overwrite);
                    var commands = uploads.ToArray();
                    Assert(commands.Length == 3 && commands[1] == commands[2] &&
                        !File.Exists(StudySeatingTransfer.PathFor(pipe, commands[2].UploadId)),
                        "successful retry changed its upload command or retained the transfer");
                    Snapshot(window, "study-seating-upload-saved.png");

                    LoadDraft(scopeReady: false);
                    Assert(!finish.IsVisible, "a new draft kept the previous completion action");
                    originalPreview = preview.Source;
                    foreach (var (scope, code) in refusedRetries)
                    {
                        int previousCount = commands.Length;
                        Recheck(scope);
                        Click(window, "SaveImage");
                        WaitForRetry(scope);
                        Click(window, "SaveImage");
                        PumpUntil(() => refresh.IsEnabled, $"retry refused with {code} did not settle");
                        Assert(heading.IsVisible && heading.Text == "上次上传结果未确认" && result.Text.Contains("草稿已保留") &&
                            ReferenceEquals(preview.Source, originalPreview) && choose.IsEnabled && !save.IsEnabled &&
                            !info.Text.Contains(scope.ClassName),
                            $"retry refused with {code} claimed failure or kept the old scope available");
                        commands = uploads.ToArray();
                        Assert(commands.Length == previousCount + 2 && commands[^2] == commands[^1] &&
                            commands[^1].UploadId != commands[previousCount - 1].UploadId &&
                            !File.Exists(StudySeatingTransfer.PathFor(pipe, commands[^1].UploadId)),
                            $"retry refused with {code} changed its command, reused an old upload ID, or retained its transfer");
                    }
                    Recheck(afterBindingChange);
                    Click(window, "SaveImage");
                    WaitForSaved(afterBindingChange);
                    var finalCommands = uploads.ToArray();
                    Assert(finalCommands.Length == commands.Length + 1 && finalCommands[^1].UploadId != commands[^1].UploadId,
                        "reconfirmed draft reused the retired upload command");
                    Click(window, "FinishButton");
                    Assert(!window.IsVisible, "completion did not close the upload window");
                }
                finally { window.Dismiss(); }
            });
            Checks.Add("Seating upload: normalized JPEG; first upload and overwrite; rejected version and failed refresh retain the draft; actual Pipe retries the same command; saved class/revision and completion; expiry and binding change on retry leave the earlier outcome unconfirmed and require a new ticket");
        }
        finally
        {
            window.Dismiss();
            lifetime.Cancel();
            try { server.GetAwaiter().GetResult(); }
            catch (OperationCanceledException) { }
            finally
            {
                foreach (var command in uploads.DistinctBy(command => command.UploadId))
                    StudySeatingTransfer.Delete(pipe, command.UploadId);
            }
        }
    }
}

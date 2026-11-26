using System.Collections.Concurrent;
using System.Globalization;
using System.IO;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using NPEduTools.App;
using NPEduTools.Contracts;
using NPEduTools.Host;
using NPEduTools.Integrations.Npep;

internal static partial class Program
{
    private sealed record SeatingTarget(string ClassId, string BindingId, int Revision, string? Hash = null)
    {
        internal JsonObject State(string version) => new()
        {
            ["version"] = version, ["classId"] = ClassId, ["className"] = "高二（7）班",
            ["revision"] = Revision, ["hash"] = Hash
        };
        internal JsonObject Context() => new() { ["identity"] = new JsonObject { ["bindingId"] = BindingId } };
    }

    private static void RunStudySeatingWorkspaceChecks()
    {
        var policy = StudyFixture();
        var target = new SeatingTarget("fixture-class-a", "fixture-binding-a", 3);
        string pipe = "NPEduTools.Test.seating-workspace." + Guid.NewGuid().ToString("N");
        using var stop = new CancellationTokenSource();
        int publications = 0, readCount = 0, holdRead = 0, loseWrite = 0;
        var requests = new ConcurrentQueue<StudySeatingUploadCommand>();
        TaskCompletionSource? releaseRead = null, releaseWrite = null;
        var service = new StudySeatingUploadService(pipe, () => policy,
            (version, _) =>
            {
                var current = Volatile.Read(ref target);
                return Task.FromResult(new NpepSeatingUploadScope(current.Context(), current.State(version)));
            },
            (scope, id, revision, png, _) =>
            {
                var current = Volatile.Read(ref target);
                if (!JsonNode.DeepEquals(scope.Context, current.Context())) throw new NpepException("BINDING_CHANGED");
                if (revision != current.Revision) throw new NpepException("SEATING_CHANGED");
                current = current with { Revision = revision + 1, Hash = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(png)) };
                Volatile.Write(ref target, current);
                Interlocked.Increment(ref publications);
                return Task.FromResult(current.State(policy.Version!));
            },
            verifyBinding: (scope, _) => JsonNode.DeepEquals(scope.Context, Volatile.Read(ref target).Context())
                ? Task.CompletedTask : Task.FromException(new NpepException("BINDING_CHANGED")));
        var server = Task.Run(() => WorkspaceProxy(pipe, async request =>
        {
            if (request.Capability == "study.seating.upload.status.scoped")
            {
                Assert(Protocol.Validate(request) is null, "scoped status is not valid IPC");
                var reply = await service.HandleAsync(request, stop.Token);
                bool hold = Interlocked.Exchange(ref holdRead, 0) == 1;
                Interlocked.Increment(ref readCount);
                if (hold) await releaseRead!.Task.WaitAsync(stop.Token);
                return reply;
            }
            if (request.Capability == "study.seating.upload.scoped")
            {
                Assert(Protocol.Validate(request) is null, "scoped upload is not valid IPC");
                var reply = await service.HandleAsync(request, stop.Token);
                var command = request.SeatingUpload!;
                requests.Enqueue(command);
                if (reply.Outcome == "Succeeded" && Interlocked.Exchange(ref loseWrite, 0) == 1)
                {
                    await releaseWrite!.Task.WaitAsync(stop.Token);
                    throw new IOException("fixture dropped the reply after saving");
                }
                return reply;
            }
            return new HostResponse(1, request.RequestId, "Succeeded", null, "", Study: policy);
        }, stop.Token));

        string path = Path.Combine(_output, "synthetic-seating.png");
        DrawSeatingFixture(path);
        var main = PrivateWorkspace(pipe);
        try
        {
            Exercise(main, owner =>
            {
                var handle = new WindowInteropHelper(owner).Handle;
                Click(main, "StudyNav");
                Click(main, "SchoolWorkspaceTab");
                var school = WorkspaceField<StudyMonitoringWindow>(main, "_studyMonitoringWindow");
                var original = Button(school, "SchoolSeatingButton");
                PumpUntil(() => original.IsVisible && original.IsEnabled, "original school seating action is unavailable");
                Click(school, "SchoolSeatingButton");
                var page = WorkspaceField<StudySeatingUploadWindow>(main, "_workspaceSeatingController");
                var back = Button(main, "WorkspaceSeatingBack");
                var choose = Button(page, "ChooseImage");
                var save = Button(page, "SaveImage");
                var refresh = Button(page, "RefreshScope");
                var info = (TextBlock)page.FindName("ScopeText");
                var result = (TextBlock)page.FindName("ResultHeading");
                var preview = (Image)page.FindName("Preview");
                var savedMark = (Border)page.FindName("SavedMark");
                var scroll = (ScrollViewer)page.FindName("SeatingScroll");
                var previewPane = (Border)page.FindName("SeatingPreviewPane");
                var actionPane = (Border)page.FindName("SeatingActionsPane");
                PumpUntil(() => choose.IsEnabled && back.IsVisible, "scoped status did not arrive");
                Assert(!page.IsVisible && new WindowInteropHelper(page).Handle == nint.Zero &&
                    new WindowInteropHelper(main).Handle == handle && !original.IsVisible,
                    "school seat management opened a second native window");
                foreach (string id in new[] { "MinimizeWindow", "MaximizeRestoreWindow", "CloseWindow" })
                    Assert(Button(main, id, automation: true).IsVisible, $"missing the existing caption action {id}");
                Assert(ReferenceEquals(choose.Style, main.FindResource("TeacherPrimary")) && !save.IsEnabled,
                    "the first available step is not emphasized");
                LoadDraft();
                var image = preview.Source;
                Capture(1040, 760, "seating-preview-wide.png");
                Capture(840, 600, "seating-preview-narrow.png");
                Checks.Add("Original school-study seating key uses one native main window and its three caption controls; real 1040×760 / 840×600 bounds fit target, preview and upload");

                Volatile.Write(ref target, target with { Revision = 4 });
                Click(page, "SaveImage");
                PumpUntil(() => refresh.IsEnabled && result.Text == "上传失败", "revision conflict did not settle");
                Assert(ReferenceEquals(preview.Source, image) && !save.IsEnabled && publications == 0,
                    "another teacher's image revision dropped the same-class draft or allowed an old write");
                Click(page, "RefreshScope");
                PumpUntil(() => save.IsEnabled && info.Text.Contains("版本 4"), "same-class draft did not recheck the new revision");
                Assert(ReferenceEquals(preview.Source, image), "same binding lost the original draft");
                Checks.Add("Another teacher's same-class revision rejects the old upload with zero writes; explicit version recheck retains the original image");

                releaseWrite = NewSignal();
                Volatile.Write(ref loseWrite, 1);
                Click(page, "SaveImage");
                PumpUntil(() => requests.Count == 2, "original upload did not reach the fixture");
                Assert(!back.IsEnabled && !save.IsEnabled, "active socket write did not protect local navigation");
                releaseWrite.SetResult();
                PumpUntil(() => back.IsEnabled && save.IsEnabled && result.Text == "等待确认", "lost reply did not preserve same-request confirmation");
                Capture(840, 600, "seating-unconfirmed-narrow.png");
                Return();
                Click(school, "SchoolSeatingButton");
                PumpUntil(() => save.IsEnabled && result.Text == "等待确认", "same binding did not recover original confirmation on reopening");
                Click(page, "SaveImage");
                PumpUntil(() => result.Text == "已保存到本班", "original receipt could not be recovered");
                var sent = requests.ToArray();
                Assert(sent.Length == 3 && sent[1] == sent[2] && publications == 1 &&
                    sent[1].UploadId != sent[0].UploadId && sent[1].ExpectedRevision == 4,
                    "recovery duplicated the publication or changed the request evidence");
                Capture(1040, 760, "seating-saved-wide.png");
                Checks.Add("A real Host seating service saved once before its named-pipe reply was dropped; reopening and retry use the same upload ID, ticket, revision and hash");

                LoadDraft();
                Click(page, "RefreshScope");
                PumpUntil(() => save.IsEnabled, "new draft did not get the post-save revision");
                releaseWrite = NewSignal();
                releaseWrite.SetResult();
                Volatile.Write(ref loseWrite, 1);
                Click(page, "SaveImage");
                PumpUntil(() => requests.Count == 4 && result.Text == "等待确认", "second original operation was not left pending");
                Return();

                int priorReads = Volatile.Read(ref readCount);
                releaseRead = NewSignal();
                Volatile.Write(ref holdRead, 1);
                Click(school, "SchoolSeatingButton");
                PumpUntil(() => Volatile.Read(ref readCount) > priorReads, "slow verification was not in flight");
                Assert(back.IsEnabled && !save.IsEnabled && preview.Source is null, "a slow read blocked return or exposed the former preview");
                Return();
                Volatile.Write(ref target, target with { BindingId = "fixture-binding-replacement", Revision = 6 });
                Click(school, "SchoolSeatingButton");
                PumpUntil(() => result.Text == "上传目标已变更" && choose.IsEnabled, "same-class replacement binding was accepted as the original scope");
                releaseRead.SetResult();
                Assert(preview.Source is null && !save.IsEnabled && requests.Count == 4,
                    "the old draft or original upload survived a binding replacement");

                LoadDraft();
                Return();
                Volatile.Write(ref target, new SeatingTarget("fixture-class-b", "fixture-binding-replacement", 2));
                Click(school, "SchoolSeatingButton");
                PumpUntil(() => result.Text == "上传目标已变更" && info.Text.Contains("版本 2"), "same-name class change was not checked by ID");
                Assert(preview.Source is null && !save.IsEnabled && requests.Count == 4 && publications == 2,
                    "a late former scope or apparent same name reused its old preview, command or revision");
                Assert(ReferenceEquals(choose.Style, main.FindResource("TeacherPrimary")),
                    "the new target does not emphasize choosing a new image");
                Capture(840, 600, "seating-changed-target-narrow.png");
                Return();
                Checks.Add("Slow read can leave and restores focus to the original button; late response, a new binding for the same class and another class with the same name cannot reuse draft or request");
                main.Close();

                void LoadDraft()
                {
                    var load = page.LoadDraftAsync(path);
                    PumpUntil(() => load.IsCompleted, "synthetic image was not decoded");
                    load.GetAwaiter().GetResult();
                    Assert(preview.Source is not null, "chosen image was not previewed");
                }

                void Return()
                {
                    Click(main, "WorkspaceSeatingBack");
                    PumpUntil(() => original.IsVisible && Keyboard.FocusedElement == original,
                        "return did not restore the original school seating action and keyboard focus");
                }

                void Capture(int width, int height, string file)
                {
                    main.Width = width;
                    main.Height = height;
                    scroll.ScrollToTop();
                    main.UpdateLayout();
                    PumpUntil(() => Math.Abs(main.ActualWidth - width) < 2 && Math.Abs(main.ActualHeight - height) < 2 &&
                        Grid.GetColumn(actionPane) == (width == 1040 ? 1 : 0), "window or seating layout did not adopt the requested width");
                    Assert(Grid.GetRow(previewPane) == (width == 1040 ? 0 : 1), "preview is in the wrong reading order");
                    Assert(InView(info) && InView(choose) && InView(result) && (!save.IsVisible || InView(save)) &&
                        (preview.Source is null || InView(preview)), "target, preview or confirmation clipped by the scroll viewport: " + file);
                    Assert(savedMark.IsVisible == (result.Text == "已保存到本班"),
                        "visual success mark contradicted the upload receipt: " + file);
                    Assert(GetWindowRect(handle, out _), "native window bounds unavailable");
                    Snapshot(main, file);
                }
                bool InView(FrameworkElement element)
                {
                    var bottom = element.TranslatePoint(new Point(element.ActualWidth, element.ActualHeight), scroll);
                    var top = element.TranslatePoint(new Point(), scroll);
                    return element.IsVisible && top.X >= -1 && top.Y >= -1 &&
                        bottom.X <= scroll.ViewportWidth + 1 && bottom.Y <= scroll.ViewportHeight + 1;
                }
            });
        }
        finally
        {
            stop.Cancel();
            try { server.GetAwaiter().GetResult(); }
            catch (OperationCanceledException) { }
        }
    }

    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static void DrawSeatingFixture(string path)
    {
        var visual = new DrawingVisual();
        using (var canvas = visual.RenderOpen())
        {
            canvas.DrawRectangle(Brushes.White, null, new Rect(0, 0, 800, 480));
            canvas.DrawRoundedRectangle(Brushes.DarkSlateGray, null, new Rect(170, 22, 460, 45), 10, 10);
            Label("讲台 · 座位示意", 310, 32, 24, Brushes.White);
            for (int row = 0; row < 5; row++)
            for (int column = 0; column < 6; column++)
            {
                int x = 51 + column * 112 + (column >= 3 ? 43 : 0), y = 96 + row * 64;
                canvas.DrawRoundedRectangle(column is 2 or 3 ? Brushes.Lavender : Brushes.Honeydew, null,
                    new Rect(x, y, 91, 46), 7, 7);
                Label($"{row + 1}–{column + 1}", x + 25, y + 11, 20, Brushes.DarkSlateGray);
            }
            Label("测试图片 · 无学生信息", 303, 431, 19, Brushes.DimGray);
            void Label(string text, int x, int y, int size, Brush brush) =>
                canvas.DrawText(new FormattedText(text, CultureInfo.GetCultureInfo("zh-CN"), FlowDirection.LeftToRight,
                    new Typeface("Microsoft YaHei UI"), size, brush, 1), new Point(x, y));
        }
        var bitmap = new RenderTargetBitmap(800, 480, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        var png = new PngBitmapEncoder();
        png.Frames.Add(BitmapFrame.Create(bitmap));
        using var file = File.Create(path);
        png.Save(file);
    }
}

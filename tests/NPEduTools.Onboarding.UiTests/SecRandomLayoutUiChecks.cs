using System.Collections.Concurrent;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Runtime.InteropServices;
using NPEduTools.App;
using NPEduTools.Contracts;

internal static partial class Program
{
    [DllImport("user32.dll")]
    private static extern bool PrintWindow(nint window, nint dc, uint flags);

    private static void RunSecRandomLayoutChecks()
    {
        string pipe = "NPEduTools.Test.secrandom-layout." + Guid.NewGuid().ToString("N");
        using var stop = new CancellationTokenSource();
        var commands = new ConcurrentQueue<HostRequest>();
        const string savedExecutable = "SecRandomLauncher.exe";
        var savedPath = new TaskCompletionSource<HostRequest>(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstCheck = new TaskCompletionSource<HostRequest>(TaskCreationOptions.RunContinuationsAsynchronously);
        var ready = new SecRandomState(savedExecutable, 7, "Ready", DateTimeOffset.Now, null);
        SecRandomState current = new(null, 6, "Unchecked", null, null);
        bool readStatus = true;
        bool rejectFloat = false;
        var server = Task.Run(() => WorkspaceProxy(pipe, request =>
        {
            if (request.Capability == "secrandom.status")
                return Task.FromResult(new HostResponse(Protocol.Version, request.RequestId,
                    "Succeeded", null, "隔离状态", SecRandom: Volatile.Read(ref readStatus) ? Volatile.Read(ref current) : null));
            if (request.Capability == "secrandom.config.set")
            {
                Assert(request.ExecutablePath == savedExecutable && request.ExpectedRevision == 6, "first configuration saved the wrong path or revision");
                Volatile.Write(ref current, ready with { Connection = "Unchecked", CheckedAt = null });
                savedPath.TrySetResult(request);
                return Task.FromResult(new HostResponse(Protocol.Version, request.RequestId, "Succeeded", null,
                    "已保存 SecRandom 程序位置，请检查接口。", SecRandom: Volatile.Read(ref current)));
            }

            Assert(request.Capability == "secrandom.command" && request.ExpectedRevision == 7, "unexpected operation or revision in point-name UI fixture");
            var command = request.SecRandom!;
            var state = Volatile.Read(ref current);
            if (command.Action == "check" && state.Connection == "Unchecked")
            {
                Volatile.Write(ref current, ready);
                firstCheck.TrySetResult(request);
                return Task.FromResult(new HostResponse(Protocol.Version, request.RequestId, "Accepted", null,
                    "正在检查接口", SecRandom: ready));
            }
            string outcome = "Accepted";
            switch (command.Action)
            {
                case "quick-draw" or "check" or "open":
                    state = state with { Operation = new(request.RequestId, command.Action, "Running", "正在执行", DateTimeOffset.Now) };
                    break;
                case "acknowledge":
                    var operation = state.Operation!;
                    Assert(operation.State == "Unknown" && command.AcknowledgeOperation == operation.RequestId, "confirmation did not address the original uncertain draw");
                    state = state with { Operation = operation with { State = "Acknowledged", Message = "已核实原请求" } };
                    outcome = "Succeeded";
                    break;
                case "show-float" or "hide-float":
                    if (Volatile.Read(ref rejectFloat))
                    {
                        commands.Enqueue(request);
                        return Task.FromResult(new HostResponse(Protocol.Version, request.RequestId, "Rejected", null, "当前浮窗命令已拒绝", SecRandom: state));
                    }
                    state = state with { Operation = new(request.RequestId, command.Action, "Succeeded", "浮窗操作已确认", DateTimeOffset.Now) };
                    outcome = "Succeeded";
                    break;
                default: throw new InvalidOperationException("unexpected command " + command.Action);
            }
            Volatile.Write(ref current, state);
            commands.Enqueue(request);
            return Task.FromResult(new HostResponse(Protocol.Version, request.RequestId, outcome, null, "隔离回执", SecRandom: state));
        }, stop.Token));

        var window = new SecRandomWindow(pipe);
        try
        {
            window.Show();
            var browse = Button(window, "BrowseProgramButton");
            PumpUntil(() => window.IsLoaded && browse.IsVisible && browse.IsEnabled, "first use did not open the program editor in the main card");
            var viewport = (ScrollViewer)window.FindName("PageScroll");
            var draw = Button(window, "DrawButton");
            var acknowledge = Button(window, "AcknowledgeButton");
            var operationText = (TextBlock)window.FindName("OperationText");
            var winner = (TextBlock)window.FindName("WinnerText");
            var program = (TextBlock)window.FindName("ProgramSummary");
            var configurationFeedback = (TextBlock)window.FindName("ConfigurationMessage");
            var path = (TextBox)window.FindName("Executable");
            var save = Button(window, "SaveProgramButton");
            var check = Button(window, "CheckButton");
            var open = Button(window, "OpenButton");
            var editor = (FrameworkElement)window.FindName("ConfigSection");

            Assert(!draw.IsVisible && !draw.IsEnabled && !save.IsEnabled && ReferenceEquals(browse.Style, window.FindResource("TeacherPrimary")),
                "first use did not prioritize selecting a program");
            foreach (var (width, height) in new[] { (780, 640), (520, 440) })
            {
                Resize(width, height);
                VerifyInViewport(browse);
                VerifyInViewport(save);
                CaptureWindow($"secrandom-first-setup-{width}-{height}.png");
            }
            path.Text = savedExecutable;
            Click(window, "SaveProgramButton");
            PumpUntil(() => savedPath.Task.IsCompleted && check.IsEnabled && !editor.IsVisible, "saving the path did not expose the check as the next step");
            Assert(program.Text.Contains(savedExecutable) && !draw.IsEnabled &&
                ReferenceEquals(check.Style, window.FindResource("TeacherPrimary")),
                "saved path is missing from the configuration card or enabled drawing before an interface check");
            Assert((open.IsVisible && open.IsEnabled) || ((TextBlock)window.FindName("NextActionText")).Text.Contains("启动") ||
                ((TextBlock)window.FindName("NextActionText")).Text.Contains("打开"), "unchecked setup has no path to starting SecRandom");
            Resize(520, 440);
            VerifyInViewport(check);
            CaptureWindow("secrandom-awaiting-check-520-440.png");
            Click(window, "CheckButton");
            PumpUntil(() => firstCheck.Task.IsCompleted && draw.IsEnabled, "the explicit interface check did not enable the tool");
            Assert(open.IsVisible && open.IsEnabled, "the checked program cannot be opened from its configuration section");

            Click(window, "DrawButton");
            PumpUntil(() => commands.Count == 1, "draw did not reach the isolated backend");
            var first = commands.ToArray()[0];
            Volatile.Write(ref current, ready with { Operation = new(first.RequestId, "quick-draw", "Succeeded", "抽取完成", DateTimeOffset.Now,
                Winner: new("A-08", "周同学", "")) });
            PumpUntil(() => winner.IsVisible && winner.Text.Contains("周同学") && draw.IsEnabled, "draw did not display the winner in the visible workspace");
            CaptureSizes("winner", winner);

            var showFloat = Button(window, "ShowFloatButton");
            var hideFloat = Button(window, "HideFloatButton");
            Resize(780, 640);
            Assert(showFloat.IsVisible && hideFloat.IsVisible, "wide layout hid the two floating-window commands");
            Resize(520, 440);
            var floatSection = (Expander)window.FindName("FloatConfiguration");
            Assert(!floatSection.IsExpanded && !showFloat.IsVisible && !hideFloat.IsVisible,
                "the compact floating-window section did not start collapsed");
            floatSection.IsExpanded = true;
            window.UpdateLayout();
            floatSection.BringIntoView();
            window.UpdateLayout();
            VerifyInViewport(showFloat);
            VerifyInViewport(hideFloat);
            CaptureWindow("secrandom-float-expanded-520-440.png");
            Assert(showFloat.IsVisible && hideFloat.IsVisible && showFloat.IsEnabled && hideFloat.IsEnabled,
                "the two distinct floating-window actions were removed");
            Click(window, "ShowFloatButton");
            PumpUntil(() => commands.Count == 2 && hideFloat.IsEnabled, "show float did not finish");
            Click(window, "HideFloatButton");
            PumpUntil(() => commands.Count == 3 && showFloat.IsEnabled, "hide float did not finish");
            Assert(commands.Skip(1).Select(x => x.SecRandom!.Action).SequenceEqual(new[] { "show-float", "hide-float" }),
                "the two floating-window buttons dispatched the wrong action");

            Assert(winner.IsVisible && winner.Text.Contains("周同学") &&
                ((TextBlock)window.FindName("FloatMessage")).Text.Contains("浮窗操作已确认"),
                "floating-window feedback is missing or erased the last confirmed draw");
            Volatile.Write(ref rejectFloat, true);
            Click(window, "ShowFloatButton");
            var floatFeedback = (TextBlock)window.FindName("FloatMessage");
            PumpUntil(() => floatFeedback.Text.Contains("当前浮窗命令已拒绝"), "old floating-window success hid a newer rejection");
            Click(window, "OpenButton");
            PumpUntil(() => commands.Count == 5 && !open.IsEnabled, "open command did not start");
            var opening = commands.Last();
            Volatile.Write(ref current, ready with { Operation = new(opening.RequestId, "open", "Failed", "本次启动失败", DateTimeOffset.Now) });
            PumpUntil(() => configurationFeedback.Text.Contains("本次启动失败") && open.IsEnabled, "accepted open did not show its final failure in the configuration section");
            Assert(winner.Text.Contains("周同学") && floatFeedback.Text.Contains("当前浮窗命令已拒绝"), "opening SecRandom replaced another section's feedback");

            Click(window, "DrawButton");
            PumpUntil(() => commands.Count == 6, "second explicit draw did not reach the fixture");
            var timedOut = commands.Last();
            Click(window, "ConfigureButton");
            path.Text = "unsaved-second-program.exe";
            Assert(editor.IsVisible, "the path editor could not be opened before the timeout");
            var uncertain = ready with { Connection = "Unavailable", Operation = new(timedOut.RequestId, "quick-draw", "Unknown",
                "操作等待已结束，请核实 SecRandom 中的结果。", DateTimeOffset.Now, "timeout") };
            Volatile.Write(ref current, uncertain);
            PumpUntil(() => acknowledge.IsVisible && acknowledge.IsEnabled, "timeout did not show the acknowledgement action");
            Assert(!draw.IsEnabled && !winner.IsVisible && operationText.Text.Contains("结果待核实") && operationText.Text.Contains("抽取历史"),
                "timed-out draw shows a previous winner or does not explain reconciliation");
            Assert(!editor.IsVisible && path.Text == "unsaved-second-program.exe", "the path editor obscured recovery or discarded the draft");
            CaptureSizes("uncertain", acknowledge);

            Click(window, "DrawButton"); // Even a queued click must not dispatch a second draw.
            Click(window, "CheckButton");
            PumpUntil(() => commands.Count >= 7 && !acknowledge.IsEnabled, "checking the interface did not start");
            Assert(commands.Count == 7 && commands.Last().SecRandom?.Action == "check", "the uncertain state dispatched another draw");
            Assert(acknowledge.IsVisible && operationText.Text.Contains("结果待核实"), "a pending interface check displaced the unresolved draw");
            Volatile.Write(ref readStatus, false);
            PumpUntil(() => ((TextBlock)window.FindName("Connection")).Text.Contains("未确认"), "lost status was not surfaced");
            Assert(acknowledge.IsVisible && !acknowledge.IsEnabled && operationText.Text.Contains("后台恢复后"),
                "brief backend loss removed or enabled the original acknowledgement");
            viewport.ScrollToTop();
            window.UpdateLayout();
            VerifyInViewport(operationText);
            VerifyInViewport(acknowledge);
            Volatile.Write(ref current, uncertain with { Connection = "Ready" });
            Volatile.Write(ref readStatus, true);
            PumpUntil(() => acknowledge.IsEnabled, "the original recovery action did not return with trustworthy data");

            nint hwnd = new WindowInteropHelper(window).Handle;
            using var dialogDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var decline = Task.Run(() => RespondToDrawAcknowledgement(hwnd, accept: false, dialogDeadline.Token));
            Click(window, "AcknowledgeButton");
            Assert(decline.GetAwaiter().GetResult() && commands.Count == 7 && acknowledge.IsVisible && !draw.IsEnabled,
                "cancel cleared the pending draw or sent a command");
            var confirm = Task.Run(() => RespondToDrawAcknowledgement(hwnd, accept: true, dialogDeadline.Token));
            Click(window, "AcknowledgeButton");
            Assert(confirm.GetAwaiter().GetResult(), "native acknowledgement did not have a safe default");
            PumpUntil(() => draw.IsEnabled && !acknowledge.IsVisible, "confirming the original request did not restore the draw action");
            Assert(editor.IsVisible && path.Text == "unsaved-second-program.exe" && program.Text.Contains(savedExecutable),
                "recovery failed to restore the path draft or changed the saved summary");
            Assert(commands.Count == 8 && commands.Last().SecRandom?.AcknowledgeOperation == timedOut.RequestId &&
                commands.Count(x => x.SecRandom?.Action == "quick-draw") == 2, "acknowledgement replayed a draw or used another request");

            Checks.Add("Point-name Windows wide and 520x440 minimum: the winner/uncertain status follows the draw, with the acknowledgement fully visible before scrolling");
            Checks.Add("Timeout, interface check and transport loss retain the original draw; native cancel and acknowledge do not repeat it; show/hide float send distinct actions");
            Checks.Add("First run exposes the path controls; saving moves to explicit interface check, and draw reconciliation preserves an open path draft");
            Checks.Add("Verified setup offers a direct program shortcut; float controls remain separate and expand on demand in compact layout");
            Checks.Add("Late open failures and rejected floating-window commands report in their own section and preserve the confirmed winner");

            void CaptureWindow(string file)
            {
                window.UpdateLayout();
                var handle = new WindowInteropHelper(window).Handle;
                Assert(GetWindowRect(handle, out var rect), "native window bounds unavailable");
                using var bitmap = new System.Drawing.Bitmap(rect.Right - rect.Left, rect.Bottom - rect.Top);
                using (var graphics = System.Drawing.Graphics.FromImage(bitmap))
                {
                    var dc = graphics.GetHdc();
                    try { Assert(PrintWindow(handle, dc, 2), "full window capture failed"); }
                    finally { graphics.ReleaseHdc(dc); }
                }
                bitmap.Save(System.IO.Path.Combine(_output, file), System.Drawing.Imaging.ImageFormat.Png);
            }

            void CaptureSizes(string phase, FrameworkElement resultAction)
            {
                var result = (FrameworkElement)window.FindName("DrawResult");
                foreach (var (width, height, row) in new[] { (780, 640, 0), (520, 440, 1) })
                {
                    Resize(width, height);
                    Assert(Grid.GetRow(result) == row && Grid.GetColumn(result) == 1 - row, "point-name result is not next to/below the draw at this width");
                    VerifyInViewport(draw);
                    VerifyInViewport(operationText);
                    VerifyInViewport(resultAction);
                    CaptureWindow($"secrandom-{phase}-{width}-{height}.png");
                }
            }

            void Resize(int width, int height)
            {
                viewport.LayoutTransform = Transform.Identity;
                window.Width = width;
                window.Height = height;
                viewport.ScrollToTop();
                window.UpdateLayout();
            }

            void VerifyInViewport(FrameworkElement item)
            {
                Rect bounds = item.TransformToAncestor(viewport).TransformBounds(new Rect(item.RenderSize));
                Assert(item.IsVisible && bounds.Width > 0 && bounds.Height > 0 &&
                    bounds.Left >= -1 && bounds.Right <= viewport.ViewportWidth + 1 &&
                    bounds.Top >= -1 && bounds.Bottom <= viewport.ViewportHeight + 1,
                    $"{item.Name} is outside the default viewport at {window.Width}x{window.Height}: {bounds.Bottom:0.0}/{viewport.ViewportHeight:0.0}");
            }
        }
        finally
        {
            window.Shutdown();
            stop.Cancel();
            try { server.GetAwaiter().GetResult(); } catch (OperationCanceledException) { }
        }
    }

    private static async Task<bool> RespondToDrawAcknowledgement(nint owner, bool accept, CancellationToken token)
    {
        int buttonId = accept ? 1 : 2;
        while (!token.IsCancellationRequested)
        {
            nint dialog = GetLastActivePopup(owner);
            nint button = dialog == 0 || dialog == owner ? 0 : GetDlgItem(dialog, buttonId);
            if (button != 0)
            {
                bool defaultsToCancel = ((long)SendCaptionMessage(dialog, 0x0400, 0, 0) & 0xffff) == 2;
                return PostDialogCommand(dialog, 0x0111, buttonId, button) && defaultsToCancel;
            }
            try { await Task.Delay(20, token); }
            catch (OperationCanceledException) { break; }
        }
        return false;
    }
}

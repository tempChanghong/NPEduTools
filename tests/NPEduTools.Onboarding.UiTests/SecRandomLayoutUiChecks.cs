using System.Collections.Concurrent;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using NPEduTools.App;
using NPEduTools.Contracts;

internal static partial class Program
{
    private static void RunSecRandomLayoutChecks()
    {
        string pipe = "NPEduTools.Test.secrandom-layout." + Guid.NewGuid().ToString("N");
        using var stop = new CancellationTokenSource();
        var commands = new ConcurrentQueue<HostRequest>();
        var ready = new SecRandomState("fixture.exe", 7, "Ready", DateTimeOffset.Now, null);
        SecRandomState current = ready;
        bool readStatus = true;
        var server = Task.Run(() => WorkspaceProxy(pipe, request =>
        {
            if (request.Capability == "secrandom.status")
                return Task.FromResult(new HostResponse(Protocol.Version, request.RequestId,
                    "Succeeded", null, "隔离状态", SecRandom: Volatile.Read(ref readStatus) ? Volatile.Read(ref current) : null));

            Assert(request.Capability == "secrandom.command" && request.ExpectedRevision == 7, "unexpected operation or revision in point-name UI fixture");
            var command = request.SecRandom!;
            var state = Volatile.Read(ref current);
            string outcome = "Accepted";
            switch (command.Action)
            {
                case "quick-draw" or "check":
                    state = state with { Operation = new(request.RequestId, command.Action, "Running", "正在执行", DateTimeOffset.Now) };
                    break;
                case "acknowledge":
                    var operation = state.Operation!;
                    Assert(operation.State == "Unknown" && command.AcknowledgeOperation == operation.RequestId, "confirmation did not address the original uncertain draw");
                    state = state with { Operation = operation with { State = "Acknowledged", Message = "已核实原请求" } };
                    outcome = "Succeeded";
                    break;
                case "show-float" or "hide-float":
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
            PumpUntil(() => window.IsLoaded && Button(window, "DrawButton").IsEnabled, "point-name window did not open with a ready status");
            var viewport = (ScrollViewer)window.FindName("PageScroll");
            var draw = Button(window, "DrawButton");
            var acknowledge = Button(window, "AcknowledgeButton");
            var operationText = (TextBlock)window.FindName("OperationText");
            var winner = (TextBlock)window.FindName("WinnerText");

            Click(window, "DrawButton");
            PumpUntil(() => commands.Count == 1, "draw did not reach the isolated backend");
            var first = commands.ToArray()[0];
            Volatile.Write(ref current, ready with { Operation = new(first.RequestId, "quick-draw", "Succeeded", "抽取完成", DateTimeOffset.Now,
                Winner: new("A-08", "周同学", "")) });
            PumpUntil(() => winner.IsVisible && winner.Text.Contains("周同学") && draw.IsEnabled, "draw did not display the winner in the visible workspace");
            CaptureSizes("winner", winner);

            Click(window, "DrawButton");
            PumpUntil(() => commands.Count == 2, "second explicit draw did not reach the fixture");
            var timedOut = commands.ToArray()[1];
            var uncertain = ready with { Connection = "Unavailable", Operation = new(timedOut.RequestId, "quick-draw", "Unknown",
                "操作等待已结束，请核实 SecRandom 中的结果。", DateTimeOffset.Now, "timeout") };
            Volatile.Write(ref current, uncertain);
            PumpUntil(() => acknowledge.IsVisible && acknowledge.IsEnabled, "timeout did not show the acknowledgement action");
            Assert(!draw.IsEnabled && !winner.IsVisible && operationText.Text.Contains("结果待核实") && operationText.Text.Contains("抽取历史"),
                "timed-out draw shows a previous winner or does not explain reconciliation");
            CaptureSizes("uncertain", acknowledge);

            Click(window, "DrawButton"); // Even a queued click must not dispatch a second draw.
            Click(window, "CheckButton");
            PumpUntil(() => commands.Count >= 3 && !acknowledge.IsEnabled, "checking the interface did not start");
            Assert(commands.Count == 3 && commands.Last().SecRandom?.Action == "check", "the uncertain state dispatched another draw");
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
            Assert(decline.GetAwaiter().GetResult() && commands.Count == 3 && acknowledge.IsVisible && !draw.IsEnabled,
                "cancel cleared the pending draw or sent a command");
            var confirm = Task.Run(() => RespondToDrawAcknowledgement(hwnd, accept: true, dialogDeadline.Token));
            Click(window, "AcknowledgeButton");
            Assert(confirm.GetAwaiter().GetResult(), "native acknowledgement did not have a safe default");
            PumpUntil(() => draw.IsEnabled && !acknowledge.IsVisible, "confirming the original request did not restore the draw action");
            Assert(commands.Count == 4 && commands.Last().SecRandom?.AcknowledgeOperation == timedOut.RequestId &&
                commands.Count(x => x.SecRandom?.Action == "quick-draw") == 2, "acknowledgement replayed a draw or used another request");

            var showFloat = Button(window, "ShowFloatButton");
            var hideFloat = Button(window, "HideFloatButton");
            Assert(showFloat.IsVisible && hideFloat.IsVisible && showFloat.IsEnabled && hideFloat.IsEnabled,
                "the two distinct floating-window actions were removed");
            Click(window, "ShowFloatButton");
            PumpUntil(() => commands.Count == 5 && hideFloat.IsEnabled, "show float did not finish");
            Click(window, "HideFloatButton");
            PumpUntil(() => commands.Count == 6 && showFloat.IsEnabled, "hide float did not finish");
            Assert(commands.Skip(4).Select(x => x.SecRandom!.Action).SequenceEqual(new[] { "show-float", "hide-float" }),
                "the two floating-window buttons dispatched the wrong action");

            Checks.Add("Point-name Windows wide and 520x440 minimum: the winner/uncertain status follows the draw, with the acknowledgement fully visible before scrolling");
            Checks.Add("Timeout, interface check and transport loss retain the original draw; native cancel and acknowledge do not repeat it; show/hide float send distinct actions");

            void CaptureSizes(string phase, FrameworkElement resultAction)
            {
                var result = (FrameworkElement)window.FindName("DrawResult");
                foreach (var (width, height, row) in new[] { (780, 640, 0), (520, 440, 1) })
                {
                    viewport.LayoutTransform = Transform.Identity;
                    window.Width = width;
                    window.Height = height;
                    viewport.ScrollToTop();
                    window.UpdateLayout();
                    Assert(Grid.GetRow(result) == row && Grid.GetColumn(result) == 1 - row, "point-name result is not next to/below the draw at this width");
                    VerifyInViewport(draw);
                    VerifyInViewport(operationText);
                    VerifyInViewport(resultAction);
                    Snapshot(window, $"secrandom-{phase}-{width}-{height}.png");
                }
            }

            void VerifyInViewport(FrameworkElement item)
            {
                Rect bounds = item.TransformToAncestor(viewport).TransformBounds(new Rect(item.RenderSize));
                Assert(item.IsVisible && bounds.Top >= 0 && bounds.Bottom <= viewport.ViewportHeight + 1,
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

using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using NPEduTools.App;
using NPEduTools.Contracts;

internal static partial class Program
{
    private static void RunScreenEdgeDrawReceiptChecks(nint slideHandle, Func<int> slideNumber)
    {
        const BindingFlags flags = BindingFlags.NonPublic | BindingFlags.Instance;
        string pipe = "NPEduTools.Test.edge-receipt." + Guid.NewGuid().ToString("N");
        using var stop = new CancellationTokenSource();
        var ready = new SecRandomState("fixture.exe", 7, "Ready", DateTimeOffset.UtcNow,
            new(Guid.NewGuid(), "quick-draw", "Succeeded", "隔离旧回执", DateTimeOffset.UtcNow,
                Winner: new("older", "旧抽取姓名", "")));
        var receipts = new ConcurrentDictionary<Guid, SecRandomOperation>();
        var draws = new ConcurrentQueue<Guid>();
        var lookedUp = new ConcurrentQueue<Guid>();
        var accepted = new TaskCompletionSource<Guid>(TaskCreationOptions.RunContinuationsAsynchronously);
        var currentCount = 0;
        var server = Task.Run(() => WorkspaceProxy(pipe, request =>
        {
            HostResponse Reply(string outcome, SecRandomState? state, string? code = null) => new(Protocol.Version,
                request.RequestId, outcome, code, "隔离状态", SecRandom: state);
            if (request.Capability == "secrandom.status")
                return Task.FromResult(Reply("Succeeded", ready));
            if (request.Capability == "secrandom.receipt" && request.OperationId is { } id)
            {
                lookedUp.Enqueue(id);
                return Task.FromResult(receipts.TryGetValue(id, out var receipt)
                    ? Reply("Succeeded", ready with { Operation = receipt })
                    : Reply("Rejected", ready with { Operation = null }, "SecRandomReceiptNotFound"));
            }
            if (request.Capability != "secrandom.command") return Task.FromResult(Reply("Rejected", null, "UnknownCapability"));
            Assert(request.SecRandom?.Action == "quick-draw" && request.ExpectedRevision == 7, "unexpected mutation or stale revision");
            draws.Enqueue(request.RequestId);
            int call = Interlocked.Increment(ref currentCount);
            var operation = new SecRandomOperation(request.RequestId, "quick-draw", "Running", "隔离抽取中", DateTimeOffset.UtcNow);
            receipts[request.RequestId] = operation;
            if (call == 1)
            {
                accepted.TrySetResult(request.RequestId);
                return Task.FromResult(Reply("Accepted", ready with { Operation = operation }));
            }
            Assert(call == 2, "an extra gesture submitted another draw");
            receipts[request.RequestId] = Successful(operation, "断线本次同学");
            throw new IOException("Drop the reply after executing this exact draw.");
        }, stop.Token));

        var main = IsolatedMainWindow(pipe);
        var quick = new QuickAccessWindow(pipe, () => { }, () => { }, () => { }, () => { }, _ => { }, () => { }, () => { });
        var quickField = typeof(MainWindow).GetField("_quick", flags)!;
        string path = (string)typeof(QuickAccessWindow).GetField("_settingsPath", flags)!.GetValue(quick)!;
        quickField.SetValue(main, quick);
        try
        {
            quick.SetSecRandomQuickDrawAction(
                typeof(MainWindow).GetMethod("QuickDrawSecRandomAsync", flags)!.CreateDelegate<Func<Task<SidebarDrawReceipt>>>(main),
                typeof(MainWindow).GetMethod("VerifySidebarDrawAsync", flags)!.CreateDelegate<Func<Task>>(main));
            quick.SetSide(false);
            quick.SetDensity(.8);
            quick.ShowEntry();
            quick.UpdateLayout();
            var rail = new WindowInteropHelper(quick).Handle;
            var card = (FrameworkElement)quick.FindName("DrawReceiptCard");
            var label = (TextBlock)quick.FindName("RailSecRandomLabel");
            var detail = (TextBlock)quick.FindName("DrawReceiptDetail");
            var flash = Button(quick, "RailSecRandom");
            var action = Button(quick, "DrawReceiptAction");
            var next = Button(quick, "DrawNext");
            var dismiss = Button(quick, "DrawReceiptDismiss");
            var pointerEvents = new Queue<string>();
            quick.AddHandler(UIElement.PreviewTouchDownEvent, new EventHandler<TouchEventArgs>((_, e) => TracePointer("down", e)), true);
            quick.AddHandler(UIElement.PreviewTouchUpEvent, new EventHandler<TouchEventArgs>((_, e) => TracePointer("up", e)), true);
            Assert(InitializeTouchInjection(1, 3), "Windows touch injection unavailable: " + Marshal.GetLastWin32Error());
            var originalPoint = flash.PointToScreen(new Point());

            Tap(flash, Button(quick, "RailSettings"));
            Assert(draws.IsEmpty && !card.IsVisible && slideNumber() == 1, "sliding out of Flash Draw invoked a button or advanced the show");
            Tap(flash);
            PumpUntil(() => accepted.Task.IsCompleted && label.Text == "抽取中" && card.IsVisible, "first touch did not show progress");
            quick.UpdateLayout();
            Assert(label.ActualWidth >= 40 && label.TextTrimming == TextTrimming.None && label.IsVisible,
                "the three-character busy label can be clipped at compact density");
            Snapshot(quick, "01-flash-running-right.png");
            Tap(flash); Tap(flash);
            Assert(draws.Count == 1 && !flash.IsEnabled, $"repeated touch did not stay busy: draws={draws.Count}, enabled={flash.IsEnabled}");
            var shift = flash.PointToScreen(new Point()) - originalPoint;
            Assert(shift.Length <= 2, $"opening the receipt moved the rail beyond native pixel rounding: {shift}");
            Checks.Add("Native touch sliding out is inert; valid touch shows progress at the same coordinates and repeated touches submit once.");

            quick.SetAutoHidden(true);
            Guid first = accepted.Task.GetAwaiter().GetResult();
            receipts[first] = Successful(receipts[first], "成功本次同学");
            PumpUntil(() => label.Text == "已抽取", "successful receipt did not settle while suppressed");
            Assert(!quick.IsVisible && !card.IsVisible && !main.IsVisible, "a late receipt overrode protected presentation");
            quick.SetAutoHidden(false);
            Assert(!card.IsVisible && label.Text == "已抽取", "return from protected display lost the badge or reopened the card");

            quick.SetSide(true);
            // Let Windows reestablish the injection target after hiding, showing,
            // and moving the layered HWND in the same programmatic transaction.
            Pause(450);
            Tap(flash);
            PumpUntil(() => card.IsVisible, $"left receipt did not reopen, state={label.Text}, enabled={flash.IsEnabled}, events={string.Join("|", pointerEvents)}");
            Assert(card.IsVisible && !detail.Text.Contains("同学") && !((string)flash.ToolTip).Contains("同学") && draws.Count == 1,
                $"reopening disclosed a name or drew again: draws={draws.Count}, detail={detail.Text}");
            Assert(card.TranslatePoint(new Point(), quick).X > flash.TranslatePoint(new Point(), quick).X,
                "left-side card opened toward the screen edge");
            foreach (var button in new[] { action, next, dismiss })
            {
                var hit = button.TransformToAncestor(quick).TransformBounds(new Rect(button.RenderSize));
                Assert(hit.Width >= 43.5 && hit.Height >= 43.5 && hit.Bottom <= quick.ActualHeight + 1,
                    "receipt touch action clipped or is smaller than 44 DIP: " + button.Name);
            }
            Snapshot(quick, "02-flash-success-private-left.png");
            Tap(action);
            Assert(detail.Text.Contains("成功本次同学") && !detail.Text.Contains("旧抽取姓名"), "explicit view showed a different winner");
            Tap(dismiss);
            Assert(!card.IsVisible && label.Text == "已抽取" && detail.Text.Length == 0, "dismissal erased the state or retained a disclosed name");
            Checks.Add("Completion arriving while hidden preserves only a badge on return. On the opposite edge, inward controls are 44 DIP and only an explicit action reveals the matched winner.");

            Tap(flash);
            Tap(next);
            PumpUntil(() => label.Text == "待核实" && card.IsVisible, "lost command reply did not retain a visible pending disposition");
            Assert(draws.Count == 2 && !next.IsVisible && !main.IsVisible &&
                !detail.Text.Contains("旧抽取姓名") && !detail.Text.Contains("断线本次同学"), "lost reply enabled redraw, disclosed a name or navigated automatically");
            Tap(dismiss); Tap(flash); Tap(flash);
            Assert(label.Text == "待核实" && draws.Count == 2 && card.IsVisible && QuickForeground() == slideHandle,
                "tapping the outstanding receipt redrew or took PowerPoint focus");
            Snapshot(quick, "03-flash-pending-over-powerpoint.png");
            Assert(PostSlideKey(slideHandle, 0x100, 0x27, 0x014D0001) &&
                PostSlideKey(slideHandle, 0x101, 0x27, unchecked((nint)(int)0xC14D0001)), "could not send the isolated show a right arrow");
            PumpUntil(() => slideNumber() == 2 && QuickForeground() == slideHandle, "PowerPoint could not advance while pending");

            Guid original = draws.Last();
            Tap(action, staysOnSlide: false);
            var workspace = (ContentControl)main.FindName("SecRandomWorkspaceHost");
            PumpUntil(() => workspace.IsVisible && label.Text == "已抽取", "explicit verification did not recover the original completed draw");
            var controller = (SecRandomWindow)((WorkspacePanel)workspace.Content).Controller;
            var winner = (TextBlock)controller.FindName("WinnerText");
            Assert(winner.Text.Contains("断线本次同学") && !winner.Text.Contains("旧抽取姓名") &&
                lookedUp.LastOrDefault() == original && draws.Count == 2 && !card.IsVisible,
                "verification showed the snapshot's previous draw, reopened the card, or resent a mutation");
            Snapshot(main, "04-flash-original-verified.png");
            Checks.Add("Lost command reply kept PowerPoint in foreground and usable; explicit verification opened the same main workspace and performed a read for the exact completed draw.");

            void Tap(Button target, Button? releaseAt = null, bool staysOnSlide = true)
            {
                quick.UpdateLayout();
                var start = ScreenCenter(target);
                Assert(WindowFromPoint(start) == rail && QuickForeground() == slideHandle, "touch target is covered or the show is not foreground: " + target.Name);
                const uint down = 0x10006, move = 0x20006, up = 0x40000;
                SendTouch(down, start);
                try
                {
                    Pause(45);
                    if (releaseAt is not null)
                    {
                        var end = ScreenCenter(releaseAt);
                        Assert(WindowFromPoint(end) == rail, "slide-out target is beyond the owned window");
                        SendTouch(move, end);
                        start = end;
                        Pause(35);
                    }
                }
                finally { SendTouch(up, start); }
                Pause(70);
                if (staysOnSlide) Assert(QuickForeground() == slideHandle,
                    $"screen edge took focus before an explicit handoff: {target.Name}, foreground={QuickForeground():X}, rail={rail:X}");
            }
            void TracePointer(string phase, TouchEventArgs e)
            {
                if (pointerEvents.Count == 8) pointerEvents.Dequeue();
                pointerEvents.Enqueue($"{phase}:{(e.OriginalSource as FrameworkElement)?.Name}:{e.GetTouchPoint(quick).Position}");
            }
        }
        finally
        {
            quickField.SetValue(main, null);
            main.Close();
            quick.Shutdown();
            File.Delete(path); File.Delete(path + ".tmp");
            stop.Cancel();
            try { server.GetAwaiter().GetResult(); }
            catch (OperationCanceledException) { }
        }
    }

    private static SecRandomOperation Successful(SecRandomOperation operation, string name) =>
        operation with { State = "Succeeded", UpdatedAt = DateTimeOffset.UtcNow, Winner = new("isolated", name, "") };

    private static CursorPoint ScreenCenter(FrameworkElement element)
    {
        var point = element.PointToScreen(new Point(element.ActualWidth / 2, element.ActualHeight / 2));
        return new CursorPoint { X = (int)Math.Round(point.X), Y = (int)Math.Round(point.Y) };
    }

    private static void Pause(int milliseconds)
    {
        var time = Stopwatch.StartNew();
        PumpUntil(() => time.ElapsedMilliseconds >= milliseconds, "native touch did not settle");
    }
}

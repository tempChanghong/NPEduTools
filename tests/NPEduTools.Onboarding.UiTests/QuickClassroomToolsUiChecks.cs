using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using NPEduTools.App;
using NPEduTools.Contracts;

internal static partial class Program
{
    [StructLayout(LayoutKind.Sequential)]
    private struct QuickTestRect { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll", EntryPoint = "GetWindowRect")]
    private static extern bool ReadQuickTestRect(nint hwnd, out QuickTestRect rect);
    [DllImport("user32.dll", EntryPoint = "SetWindowPos")]
    private static extern bool ResizeQuickTestWindow(nint hwnd, nint after, int x, int y, int width, int height, uint flags);

    private static void RunQuickClassroomToolsChecks(nint slide, Func<int> slideNumber)
    {
        const BindingFlags flags = BindingFlags.NonPublic | BindingFlags.Instance;
        const uint down = 0x10006, move = 0x20006, up = 0x40000;
        string pipe = "NPEduTools.Test.quick-classroom." + Guid.NewGuid().ToString("N");
        using var stop = new CancellationTokenSource();
        var calls = new ConcurrentQueue<string>();
        var running = new TouchAssistState(true, false, false, "辅助已开启");
        var paused = running with { Paused = true, State = "辅助已暂停" };
        var off = running with { Running = false, State = "辅助已关闭" };
        var server = Task.Run(() => WorkspaceProxy(pipe, request =>
        {
            if (!request.Capability.StartsWith("presentation.touch.", StringComparison.Ordinal))
                return Task.FromResult(new HostResponse(Protocol.Version, request.RequestId, "Rejected", "UnknownCapability", "隔离后台"));
            calls.Enqueue(request.Capability);
            var state = request.Capability switch
            {
                "presentation.touch.pause" => paused,
                "presentation.touch.resume" or "presentation.touch.enable" => running,
                "presentation.touch.disable" => off,
                _ => throw new InvalidOperationException(request.Capability)
            };
            return Task.FromResult(new HostResponse(Protocol.Version, request.RequestId, "Succeeded", null, state.State, TouchAssist: state));
        }, stop.Token));

        var main = IsolatedMainWindow(pipe);
        void CallMain(string name, params object[] args) => typeof(MainWindow).GetMethod(name, flags)!.Invoke(main, args);
        var quick = new QuickAccessWindow(pipe,
            () => CallMain("TouchPowerClicked", main, new RoutedEventArgs()),
            () => CallMain("TouchPauseClicked", main, new RoutedEventArgs()),
            () => { }, () => { }, _ => { }, () => { }, () => { });
        var quickField = typeof(MainWindow).GetField("_quick", flags)!;
        string path = (string)typeof(QuickAccessWindow).GetField("_settingsPath", flags)!.GetValue(quick)!;
        quickField.SetValue(main, quick);
        try
        {
            void Apply(TouchAssistState? state) => CallMain("ApplyTouch", new HostResponse(Protocol.Version, Guid.NewGuid(),
                "Succeeded", null, "", TouchAssist: state));

            quick.SetSide(false);
            quick.SetDensity(.9);
            quick.UpdateStudy("自习空闲", "可以查看学校安排", "", "查看自习", false);
            quick.UpdateRecording(new("Idle", "当前未录制"));
            Apply(running);
            quick.ShowEntry(); quick.UpdateLayout();
            var hwnd = new WindowInteropHelper(quick).Handle;
            var pause = Button(quick, "PauseButton");
            var power = Button(quick, "PowerButton");
            var footer = Button(quick, "SettingsFooter");
            var strip = (FrameworkElement)quick.FindName("PptQuickStrip");
            var panel = (FrameworkElement)quick.FindName("PanelSurface");
            var scroll = (ScrollViewer)quick.FindName("ToolsPage");
            var plan = (Expander)quick.FindName("AutomaticRecordingDetails");
            var recordingControls = (FrameworkElement)quick.FindName("RecordingControls");
            var taskStack = (Panel)quick.FindName("TaskStack");
            ClickFixturePoint(hwnd, quick.PointToScreen(QuickHandleCenter(quick)), slide,
                () => PumpUntil(() => panel.IsVisible && HasForeground(quick), "the normal screen-edge handle did not open over PowerPoint"));
            Assert(InitializeTouchInjection(1, 3), "Windows touch injection unavailable: " + Marshal.GetLastWin32Error());
            quick.UpdateLayout();
            Assert(Grid.GetRow(power) == 0 && strip.IsVisible && !plan.IsExpanded, "wide tools did not show the PPT actions inline and keep automatic plans folded");
            Snapshot(quick, "01-classroom-tools-wide.png");

            quick.SetDensity(.8); quick.UpdateLayout();
            Assert(ReadQuickTestRect(hwnd, out var before), "could not read the panel's native bounds");
            var work = System.Windows.Forms.Screen.FromHandle(hwnd).WorkingArea;
            // Exercise the actual HWND under an 840×600 pixel cap without changing the display or its DPI.
            int width = Math.Min(840, before.Right - before.Left), height = Math.Min(600, work.Height);
            int y = Math.Clamp(before.Top, work.Top, work.Bottom - height);
            Assert(ResizeQuickTestWindow(hwnd, 0, before.Right - width, y, width, height, 0x14), "could not size the real panel to the short-screen cap");
            quick.UpdateLayout();
            Assert(ReadQuickTestRect(hwnd, out var resized) && resized.Bottom - resized.Top == height && Grid.GetRow(power) == 1,
                "the narrow native panel did not put the two PPT actions on separate rows");
            Checks.Add($"Short native window {resized.Right - resized.Left}×{resized.Bottom - resized.Top} px under 840×600 cap; DPI={VisualTreeHelper.GetDpi(quick).PixelsPerInchX}.");
            foreach (var item in new[] { pause, power })
            {
                var bounds = item.TransformToAncestor(quick).TransformBounds(new Rect(item.RenderSize));
                Assert(item.IsEnabled && item.IsVisible && bounds.Width >= 43.5 && bounds.Height >= 43.5 &&
                    bounds.Bottom <= scroll.TranslatePoint(new Point(), quick).Y + 1,
                    "PPT action is clipped or below the first touch viewport");
            }
            var shortViewport = scroll.TransformToAncestor(quick).TransformBounds(new Rect(scroll.RenderSize));
            foreach (var item in new[] { Button(quick, "StudyButton"), Button(quick, "RecordingButton") })
                Assert(shortViewport.Contains(item.TransformToAncestor(quick).TransformBounds(new Rect(item.RenderSize))),
                    "the idle classroom task action was clipped in the short first screen");
            Snapshot(quick, "02-classroom-tools-short.png");
            var position = pause.PointToScreen(new Point());
            scroll.ScrollToEnd();
            PumpUntil(() => Math.Abs(scroll.VerticalOffset - scroll.ScrollableHeight) < .5,
                "the classroom tools did not scroll to the end");
            Assert(scroll.VerticalOffset > 0 && pause.PointToScreen(new Point()) == position, "scrolling moved the fixed PPT pause action");
            var footerBounds = footer.TransformToAncestor(quick).TransformBounds(new Rect(footer.RenderSize));
            Assert(footerBounds.Top >= shortViewport.Top - 1 && footerBounds.Bottom <= shortViewport.Bottom + 1,
                "the classroom settings shortcut disappeared from the end of the scrollable content");
            scroll.ScrollToTop(); quick.UpdateLayout();

            Touch(pause);
            PumpUntil(() => calls.Count == 1 && Equals(pause.Content, "继续辅助") && pause.IsEnabled, "native touch did not pause");
            Touch(pause);
            PumpUntil(() => calls.Count == 2 && Equals(pause.Content, "暂停辅助") && pause.IsEnabled, "native touch did not resume");
            Assert(calls.SequenceEqual(new[] { "presentation.touch.pause", "presentation.touch.resume" }), "native touch submitted a changed or duplicated command");

            var originalTask = taskStack.Children[0];
            var target = ScreenCenter(pause);
            Assert(WindowFromPoint(target) == hwnd && HasForeground(quick), "PPT pause touch target is not owned by this window");
            var touchCapture = typeof(QuickAccessWindow).GetField("_pptTouch", flags)!;
            SendTouch(down, target);
            try
            {
                // An injected touch may not reach WPF until it has moved. Hold it
                // within the button while waiting for the real capture.
                for (int frame = 0; frame < 12 && touchCapture.GetValue(quick) is null; frame++)
                {
                    Pause(22);
                    var point = new CursorPoint { X = target.X + (frame % 2 == 0 ? 13 : -13), Y = target.Y };
                    SendTouch(move, point);
                    target = point;
                }
                PumpUntil(() => touchCapture.GetValue(quick) is not null, "native touch down did not reach the PPT action");
                quick.UpdateRecording(new("Recording", "自动录课开始", Control: new("Automatic", Guid.NewGuid(), "", 0, 0)));
                Apply(paused); quick.UpdateLayout();
                Assert(Equals(pause.Content, "继续辅助") && touchCapture.GetValue(quick) is null &&
                    pause.PointToScreen(new Point()) == position && !recordingControls.IsVisible && taskStack.Children[0] == originalTask,
                    "recording start or PPT status update moved actions, changed order, or retained the old press");
            }
            finally { Pause(22); SendTouch(up, target); }
            Pause(90);
            Assert(calls.Count == 2, "the release of a stale pause press submitted a command");
            Touch(pause);
            PumpUntil(() => calls.Count == 3 && calls.Last() == "presentation.touch.resume", "a fresh touch did not resume");
            Apply(null);
            Assert(!pause.IsEnabled && !power.IsEnabled && ((TextBlock)quick.FindName("StatusText")).Text.Contains("后台"),
                "unconfirmed PPT state left a live command");
            Checks.Add("Native pause/resume send their precise named-pipe commands. A real held touch cancelled on a status reversal while simulated auto recording started, with no geometry jump.");

            quick.Collapse();
            PumpUntil(() => QuickForeground() == slide && slideNumber() == 1, "dismissal did not return to the original slide");
            Assert(PostSlideKey(slide, 0x100, 0x27, 0x014D0001) &&
                PostSlideKey(slide, 0x101, 0x27, unchecked((nint)0xC14D0001)), "could not send the isolated show an arrow");
            PumpUntil(() => slideNumber() == 2, "the original PPT did not advance after closing the panel");
            Apply(running);
            quick.OpenPanel(); quick.UpdateLayout();
            Assert(taskStack.Children[0] == quick.FindName("RecordingCard") && recordingControls.IsVisible && !plan.IsExpanded,
                "reopening did not put the running recording first with its original controls");
            Snapshot(quick, "03-classroom-tools-active-recording.png");
            Touch(power);
            PumpUntil(() => calls.Count == 4 && Equals(power.Content, "开启辅助") && power.IsEnabled,
                "separate stop did not disable touch assistance");
            Assert(calls.Last() == "presentation.touch.disable", "stopping sent the wrong PPT action");
            Click(quick, "ShortcutsTab");
            Assert(!strip.IsVisible, "PPT strip occupied the shortcuts page");
            quick.SetAutoHidden(true);
            Click(quick, "PowerButton");
            Assert(!quick.IsVisible && calls.Count == 4, "protected full-screen suppression still allowed the PPT button");
            Checks.Add("Closing returns focus to the same Office slideshow and its next page. Reopening preserves the active recording's separate controls; protected full screen keeps the panel hidden.");

            void Touch(Button button)
            {
                var point = ScreenCenter(button);
                Assert(WindowFromPoint(point) == hwnd && HasForeground(quick), "touch button is clipped or covered");
                SendTouch(down, point);
                try { Pause(47); }
                finally { SendTouch(up, point); }
                Pause(90);
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
}

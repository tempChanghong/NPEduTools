using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
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
            quick.UpdateStudy("当前没有自习任务", "可以即刻开始手动自习，或查看学校定时自习安排", "下一次定时自习：正在等待学校下发安排", "开始自习", false);
            quick.UpdateRecording(new("Idle", "当前未录制"));
            quick.SetOnboardingAction(() => { }, pending: true);
            Apply(off);
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
            var status = (TextBlock)quick.FindName("StatusText");
            var onboarding = Button(quick, "OnboardingEntry");
            var study = Button(quick, "StudyButton");
            var record = Button(quick, "RecordingButton");
            ClickFixturePoint(hwnd, quick.PointToScreen(QuickHandleCenter(quick)), slide,
                () => PumpUntil(() => panel.IsVisible && HasForeground(quick), "the normal screen-edge handle did not open over PowerPoint"));
            Assert(InitializeTouchInjection(1, 3), "Windows touch injection unavailable: " + Marshal.GetLastWin32Error());
            quick.UpdateLayout();
            AssertClosedPpt();
            Assert(Bounds(status).Bottom <= Bounds(power).Bottom && Bounds(status).Bottom > Bounds(power).Top,
                "the wide panel did not place the single PPT action beside its status");
            Snapshot(quick, "00-ppt-off-wide.png");

            Apply(running); quick.UpdateLayout();
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
            Assert(ReadQuickTestRect(hwnd, out var resized) && resized.Bottom - resized.Top == height && Grid.GetRow(power) == 0,
                "the short native panel stacked two PPT actions that fit side by side");
            Checks.Add($"Short native window {resized.Right - resized.Left}×{resized.Bottom - resized.Top} px under 840×600 cap; DPI={VisualTreeHelper.GetDpi(quick).PixelsPerInchX}.");
            double runningStripHeight = Bounds(strip).Height;
            AssertFirstUseToolsFit();
            Snapshot(quick, "00-ppt-running-short-onboarding.png");
            Apply(off); quick.UpdateLayout();
            AssertClosedPpt();
            Assert(runningStripHeight - Bounds(strip).Height >= 12, "the closed PPT strip did not move its only action beside the status");
            AssertFirstUseToolsFit();
            Assert(scroll.ComputedVerticalScrollBarVisibility == Visibility.Visible, "the short fixture did not exercise a real scrollbar");
            var header = (ToggleButton)plan.Template.FindName("HeaderSite", plan);
            Assert(!plan.IsExpanded && Bounds(header).Top - Bounds(record).Bottom >= 3,
                "the recording plan still touched the row above it");
            Snapshot(quick, "01-ppt-off-short-onboarding.png");
            double setupRight = Bounds(onboarding).Right;
            scroll.VerticalScrollBarVisibility = ScrollBarVisibility.Hidden;
            quick.UpdateLayout();
            AssertClosedPpt();
            Assert(scroll.ComputedVerticalScrollBarVisibility != Visibility.Visible && Math.Abs(Bounds(onboarding).Right - setupRight) < 1.5,
                "removing the scrollbar shifted the tools out of alignment");
            scroll.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
            quick.UpdateLayout();
            Checks.Add("PPT off uses one action; fixed and scrollable cards align. First setup, study and recording all fit in the short viewport with assistance on or off.");

            // Simulate a narrower work area by changing the panel allocation and its native bounds together.
            var panelColumn = (ColumnDefinition)quick.FindName("LeftColumn");
            var panelWidth = panelColumn.Width;
            double scale = VisualTreeHelper.GetDpi(quick).DpiScaleX * ((ScaleTransform)((FrameworkElement)quick.Content).LayoutTransform).ScaleX;
            int narrowWidth = width - (int)Math.Round(88 * scale);
            panelColumn.Width = new GridLength(panelWidth.Value - 88);
            Assert(ResizeQuickTestWindow(hwnd, 0, resized.Right - narrowWidth, resized.Top, narrowWidth, height, 0x14), "could not narrow the panel further");
            Apply(running); quick.UpdateLayout();
            Assert(Grid.GetRow(power) == 1 && Bounds(power).Top > Bounds(pause).Bottom && Bounds(strip).Contains(Bounds(power)),
                "the two controls did not stack cleanly when their available width fell further");
            Snapshot(quick, "04-ppt-running-extra-narrow.png");
            Apply(off); quick.UpdateLayout();
            AssertClosedPpt();
            Assert(Bounds(power).Top >= Bounds(status).Bottom && Bounds(power).Top - Bounds(status).Bottom < 14,
                "the wrapped single action left space for a hidden action row");
            Snapshot(quick, "05-ppt-off-extra-narrow.png");

            quick.Collapse();
            PumpUntil(() => QuickForeground() == slide, "the first-use panel did not hand focus back to PowerPoint");
            quick.SetOnboardingAction(() => { }, pending: false);
            quick.UpdateStudy("自习空闲", "可以查看学校安排", "", "查看自习", false);
            Apply(running);
            quick.OpenPanel();
            Assert(ResizeQuickTestWindow(hwnd, 0, resized.Left, resized.Top, width, height, 0x14), "could not restore the short panel after reopening");
            quick.UpdateLayout();
            foreach (var item in new[] { pause, power })
            {
                var bounds = item.TransformToAncestor(quick).TransformBounds(new Rect(item.RenderSize));
                Assert(item.IsEnabled && item.IsVisible && bounds.Width >= 43.5 && bounds.Height >= 43.5 &&
                    bounds.Bottom <= scroll.TranslatePoint(new Point(), quick).Y + 1,
                    "PPT action is clipped or below the first touch viewport");
            }
            var shortViewport = scroll.TransformToAncestor(quick).TransformBounds(new Rect(scroll.RenderSize));
            foreach (var item in new[] { Button(quick, "StudyButton"), Button(quick, "RecordingButton") })
                Assert(shortViewport.Contains(Bounds(item)),
                    $"{item.Name} {Bounds(item)} was clipped in the short viewport {shortViewport}");
            var position = pause.PointToScreen(new Point());
            CheckQuickScrollBar(quick, scroll, hwnd);
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
            quick.UpdateLayout();
            AssertClosedPpt();
            Touch(power);
            PumpUntil(() => calls.Count == 5 && calls.Last() == "presentation.touch.enable" && pause.IsVisible,
                "the rearranged single action did not enable touch assistance");
            Click(quick, "ShortcutsTab");
            Assert(!strip.IsVisible, "PPT strip occupied the shortcuts page");
            quick.SetAutoHidden(true);
            Click(quick, "PowerButton");
            Assert(!quick.IsVisible && calls.Count == 5, "protected full-screen suppression still allowed the PPT button");
            Checks.Add("Closing returns focus to the same Office slideshow and its next page. Reopening preserves the active recording's separate controls; protected full screen keeps the panel hidden.");

            Rect Bounds(FrameworkElement element) => element.TransformToAncestor(quick).TransformBounds(new Rect(element.RenderSize));

            void AssertFirstUseToolsFit()
            {
                var viewport = Bounds(scroll);
                foreach (var item in new[] { onboarding, study, record })
                    Assert(item.IsVisible && viewport.Contains(Bounds(item)), $"{item.Name} {Bounds(item)} was clipped in the first-use viewport {viewport}");
            }

            void AssertClosedPpt()
            {
                Assert(pause.Visibility == Visibility.Collapsed && power.IsVisible && power.IsEnabled && Equals(power.Content, "开启辅助"),
                    "PPT off leaves an invisible pause slot in the layout or hides the start action");
                var action = Bounds(power);
                Assert(action.Width >= 43.5 && action.Height >= 43.5 && Bounds(strip).Contains(action),
                    "the single PPT start control lost its touch target or became clipped");
                foreach (var card in new[] { (FrameworkElement)quick.FindName("StudyCard"), (FrameworkElement)quick.FindName("RecordingCard") })
                    Assert(Math.Abs(Bounds(strip).Left - Bounds(card).Left) < 1.5 && Math.Abs(Bounds(strip).Right - Bounds(card).Right) < 1.5,
                        "the fixed PPT strip and scrolling cards do not share their left and right edges");
            }

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

    private static void CheckQuickScrollBar(QuickAccessWindow window, ScrollViewer scroll, nint hwnd)
    {
        var bar = Descendants<ScrollBar>(scroll).Single(item => item.Orientation == Orientation.Vertical && item.IsVisible);
        Assert(!Descendants<RepeatButton>(bar).Any(item => item.Command == ScrollBar.LineUpCommand || item.Command == ScrollBar.LineDownCommand),
            "the sidebar still renders the system's line-scroll arrow buttons");
        var track = (Track)bar.Template.FindName("PART_Track", bar);
        var thumb = track.Thumb;
        scroll.ScrollToTop(); window.UpdateLayout();
        var start = thumb.PointToScreen(new Point(2, thumb.ActualHeight / 2));
        var point = new CursorPoint { X = (int)Math.Round(start.X), Y = (int)Math.Round(start.Y) };
        // The layered window begins its reopen animation fully transparent.
        var panel = (FrameworkElement)window.FindName("PanelSurface");
        PumpUntil(() => panel.Opacity >= .99 && WindowFromPoint(point) == hwnd && HasForeground(window),
            "the scrollbar hit area did not become reachable");
        Snapshot(window, "02-classroom-tools-short.png");
        Assert(GetCursorPos(out var original), "could not read the original cursor position");
        Assert(SetCursorPos(point.X, point.Y), "could not position the test pointer in the thumb's side gutter");
        SendMouse(2);
        try
        {
            PumpUntil(() => thumb.IsDragging, "the thumb lost its full-width mouse hit area");
            Assert(SetCursorPos(point.X, point.Y + 36), "could not drag the scrollbar thumb");
            PumpUntil(() => scroll.VerticalOffset > 5, "dragging the scrollbar did not move the real viewport");
        }
        finally { SendMouse(4); SetCursorPos(original.X, original.Y); }
        PumpUntil(() => !thumb.IsDragging, "the scrollbar did not release mouse capture");
        Snapshot(window, "06-scrollbar-dragged.png");

        scroll.ScrollToTop(); window.UpdateLayout();
        var page = track.IncreaseRepeatButton;
        ClickFixturePoint(hwnd, page.PointToScreen(new Point(page.ActualWidth / 2, page.ActualHeight / 2)), hwnd,
            () => PumpUntil(() => scroll.VerticalOffset > 0, "clicking the track did not page down"));

        scroll.ScrollToTop(); window.UpdateLayout();
        var body = scroll.PointToScreen(new Point(30, 24));
        var wheel = new CursorPoint { X = (int)Math.Round(body.X), Y = (int)Math.Round(body.Y) };
        Assert(WindowFromPoint(wheel) == hwnd && SetCursorPos(wheel.X, wheel.Y), "the content is outside the sidebar hit area");
        try
        {
            SendMouse(0x0800, unchecked((uint)-120));
            PumpUntil(() => scroll.VerticalOffset > 0, "the mouse wheel did not scroll the sidebar");
        }
        finally { SetCursorPos(original.X, original.Y); }

        scroll.ScrollToTop(); window.UpdateLayout();
        var touchStart = scroll.PointToScreen(new Point(18, scroll.ActualHeight * .65));
        var finger = new CursorPoint { X = (int)Math.Round(touchStart.X), Y = (int)Math.Round(touchStart.Y) };
        Assert(WindowFromPoint(finger) == hwnd && HasForeground(window), "the touch pan target is outside the active sidebar");
        SendTouch(0x10006, finger);
        try
        {
            for (int frame = 0; frame < 5; frame++)
            {
                Pause(26);
                finger.Y -= 16;
                SendTouch(0x20006, finger);
            }
        }
        finally { Pause(26); SendTouch(0x40000, finger); }
        PumpUntil(() => scroll.VerticalOffset > 15 && HasForeground(window), "touch did not pan the sidebar content");
        Checks.Add("The narrow Window scrollbar has no arrow buttons. Native mouse input dragged it from outside the visible thumb, paged by clicking the track, and scrolled with the wheel. Touch panned the content.");

        static void SendMouse(uint flags, uint data = 0) => Assert(
            SendInput(1, [new() { Mouse = new() { Flags = flags, MouseData = data } }], Marshal.SizeOf<NativeInput>()) == 1,
            "Windows did not deliver the scrollbar mouse input");
    }
}

using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Threading;
using NPEduTools.App;
using NPEduTools.Contracts;

internal static partial class Program
{
    [DllImport("user32.dll", EntryPoint = "PostMessageW")]
    private static extern bool PostSlideKey(nint window, uint message, nint key, nint keyData);
    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint window, out uint process);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern nint FindWindowEx(nint parent, nint after, string className, string? title);
    [DllImport("user32.dll", EntryPoint = "SetForegroundWindow")]
    private static extern bool ActivateSlide(nint window);
    [StructLayout(LayoutKind.Sequential)]
    private struct CursorPoint { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)]
    private struct MouseInput
    {
        public int X, Y;
        public uint MouseData, Flags, Time;
        public nuint Extra;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct NativeInput { public uint Type; public MouseInput Mouse; }
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out CursorPoint point);
    [DllImport("user32.dll")] private static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] private static extern nint WindowFromPoint(CursorPoint point);
    [DllImport("user32.dll")] private static extern uint SendInput(uint count, NativeInput[] inputs, int size);

    private static void ClickFixturePoint(nint window, Point center, nint foreground, Action settle)
    {
        var target = new CursorPoint { X = (int)Math.Round(center.X), Y = (int)Math.Round(center.Y) };
        Assert(GetCursorPos(out var original) && WindowFromPoint(target) == window && QuickForeground() == foreground,
            "test window is covered or foreground changed");
        try
        {
            Assert(SetCursorPos(target.X, target.Y) && GetCursorPos(out var actual) && actual.X == target.X && actual.Y == target.Y &&
                WindowFromPoint(actual) == window && QuickForeground() == foreground, "test window moved before click");
            Assert(SendInput(2, [new() { Mouse = new() { Flags = 2 } }, new() { Mouse = new() { Flags = 4 } }], Marshal.SizeOf<NativeInput>()) == 2,
                "Windows did not deliver the test click");
            settle();
        }
        finally
        {
            if (GetCursorPos(out var current) && current.X == target.X && current.Y == target.Y) SetCursorPos(original.X, original.Y);
        }
    }

    // Opt-in integration check. Only create and close our own unsaved presentation.
    private static void RunQuickSlideShowChecks()
    {
        var existing = Process.GetProcessesByName("POWERPNT");
        bool startedOffice = existing.Length == 0;
        try
        {
            // Office may retain its empty, windowless automation server after Quit.
            Assert(startedOffice || existing.Length == 1 && existing[0].MainWindowHandle == 0, "PowerPoint is already in use");
        }
        finally { foreach (var process in existing) process.Dispose(); }
        var powerpoint = Type.GetTypeFromProgID("PowerPoint.Application", true)!;
        dynamic office = Activator.CreateInstance(powerpoint)!;
        dynamic? deck = null;
        dynamic? show = null;
        QuickAccessWindow? quick = null;
        try
        {
            Assert((int)office.Presentations.Count == 0 && (int)office.SlideShowWindows.Count == 0, "PowerPoint already owns an open presentation");
            deck = office.Presentations.Add(0); // Create the fixture without a separate editor window.
            for (int number = 1; number <= 2; number++)
            {
                dynamic slide = deck.Slides.Add(number, 12); // ppLayoutBlank
                slide.Shapes.AddTextbox(1, 55, 70, 550, 70).TextFrame.TextRange.Text = $"课堂演示 · 第 {number} 页";
            }
            deck.SlideShowSettings.ShowType = 1; // ppShowTypeSpeaker
            show = deck.SlideShowSettings.Run();
            using var process = Process.GetProcessesByName("POWERPNT").Single();
            nint slideHandle = 0;
            bool FindSlide()
            {
                // The Office version used by PowerPointReader exposes its show HWND through screenClass.
                var candidate = FindWindowEx(0, 0, "screenClass", null);
                GetWindowThreadProcessId(candidate, out uint owner);
                if (owner != process.Id || FindWindowEx(0, candidate, "screenClass", null) != 0) return false;
                slideHandle = candidate;
                return true;
            }
            PumpUntil(FindSlide, "could not identify the single test slide show");
            show.Activate();
            bool activated = ActivateSlide(slideHandle);
            Checks.Add($"PowerPoint initial activation={activated}, show={slideHandle:X}, foreground={QuickForeground():X}");
            PumpUntil(() => QuickForeground() == slideHandle, "PowerPoint show did not become foreground");

            string pipe = "NPEduTools.Test.slide-focus." + Guid.NewGuid().ToString("N");
            quick = new QuickAccessWindow(pipe, () => { }, () => { }, () => { }, () => { }, _ => { }, () => { }, () => { });
            quick.ShowEntry();
            Assert(QuickForeground() == slideHandle, "the collapsed entry took focus when first shown");
            quick.UpdateLayout();
            var handleButton = (Button)quick.FindName("EdgeHandle");
            var center = handleButton.PointToScreen(new Point(handleButton.ActualWidth / 2, handleButton.ActualHeight / 2));
            var rail = new WindowInteropHelper(quick).Handle;
            ClickFixturePoint(rail, center, slideHandle, () => PumpUntil(() =>
                quick.IsActive && QuickForeground() == rail && ((FrameworkElement)quick.FindName("PanelSurface")).IsVisible,
                "expanded rail did not receive focus after a mouse click"));
            var prior = typeof(QuickAccessWindow).GetField("_previous", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(quick);
            Checks.Add($"After rail click: foreground={QuickForeground():X}, rail={new WindowInteropHelper(quick).Handle:X}, previous={prior:X}");
            Snapshot(quick, "quick-over-slide-show.png");
            quick.Collapse();
            Checks.Add($"After collapse: foreground={QuickForeground():X}, expected={slideHandle:X}");
            PumpUntil(() => QuickForeground() == slideHandle, "PowerPoint did not regain foreground after collapse");
            Assert((int)show.View.CurrentShowPosition == 1, "show advanced before the teacher turned the page");
            // Deliver only to the verified slideshow. A user switching applications cannot receive the fixture's key.
            Assert(PostSlideKey(slideHandle, 0x100, 0x27, 0x014D0001) &&
                PostSlideKey(slideHandle, 0x101, 0x27, unchecked((nint)(int)0xC14D0001)), "could not deliver the right-arrow key");
            PumpUntil(() => (int)show.View.CurrentShowPosition == 2, "PowerPoint did not advance after focus restoration");
            Checks.Add($"PowerPoint {office.Version}: new two-slide full-screen show → expanded rail → collapse → foreground restored → Right arrow advanced to slide 2.");

            quick.Shutdown();
            quick = null;
            show.View.GotoSlide(1);
            PumpUntil(() => (int)show.View.CurrentShowPosition == 1 && QuickForeground() == slideHandle,
                "the test slide show was not ready for a notification");
            VerifyNotificationOverSlideShow(slideHandle);
            Assert((int)show.View.CurrentShowPosition == 1 && QuickForeground() == slideHandle,
                "reading the notification changed slides or lost foreground");
            Assert(PostSlideKey(slideHandle, 0x100, 0x27, 0x014D0001) &&
                PostSlideKey(slideHandle, 0x101, 0x27, unchecked((nint)(int)0xC14D0001)), "could not deliver the post-notification arrow");
            PumpUntil(() => (int)show.View.CurrentShowPosition == 2, "PowerPoint did not advance after closing the notification");
            Checks.Add($"PowerPoint {office.Version}: minor notice preserved foreground; normal notice focused its text, closed by mouse, restored the slide show, and Right advanced to slide 2.");
        }
        finally
        {
            quick?.Shutdown();
            if (deck is not null) deck.Saved = -1;
            if (show is not null) { show.View.Exit(); Marshal.FinalReleaseComObject(show); }
            if (deck is not null)
            {
                // A presentation created without an editor window can close itself when its show ends.
                try { deck.Close(); }
                catch (COMException error) when (error.HResult == unchecked((int)0x80048010)) { }
                finally { Marshal.FinalReleaseComObject(deck); }
            }
            int remainingDecks = (int)office.Presentations.Count;
            if (startedOffice && remainingDecks == 0) office.Quit();
            Checks.Add($"Fixture cleanup: remaining presentations={remainingDecks}");
            Marshal.FinalReleaseComObject(office);
        }
    }

    private static void VerifyNotificationOverSlideShow(nint slideHandle)
    {
        var minor = new SchoolNotificationWindow(SchoolNotification.Preview(SchoolNotificationPriority.Minor));
        try
        {
            minor.Show();
            Assert(minor.IsVisible && QuickForeground() == slideHandle, "a minor notification interrupted the slide show");
        }
        finally { minor.Shutdown(); }

        string pipe = "NPEduTools.Test.notice-slide." + Guid.NewGuid().ToString("N");
        using var stop = new CancellationTokenSource();
        var payload = new NpepNotice("classroom-demo", 1, "下节课调整至实验楼 203", "请带上学习单和笔。", "NORMAL",
            DateTimeOffset.Now, null, "教务处", true);
        int displayed = 0, dismissed = 0;
        var server = Task.Run(() => WorkspaceProxy(pipe, request =>
        {
            var command = request.Notification!;
            Assert(request.Capability == "npep.notifications" && NpepNotificationContract.Valid(command), "unexpected slide fixture request");
            if (command.Action != "poll")
                Assert(command.Scope == NoticeScope && command.PublicationId == payload.PublicationId &&
                    command.Revision == payload.Revision, "the slide-show notification changed identity");
            if (command.Action == "displayed") Interlocked.Increment(ref displayed);
            if (command.Action == "dismissed") Interlocked.Increment(ref dismissed);
            var inbox = new NpepInboxState(NoticeScope, "ONLINE", "已同步", 1,
                [new(payload.PublicationId, payload.Revision, payload.Title, payload.Priority, true, dismissed > 0)],
                Current: dismissed == 0 ? payload : null, CanPresent: true);
            return Task.FromResult(new HostResponse(Protocol.Version, request.RequestId, "Succeeded", null, "isolated", Inbox: inbox));
        }, stop.Token));
        var main = IsolatedMainWindow(pipe);
        var previous = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(main.Dispatcher));
        Task? poll = null;
        try
        {
            poll = (Task)typeof(MainWindow).GetMethod("NotificationPollAsync", NoticeMembers)!.Invoke(main, null)!;
            PumpUntil(() => NoticeField<SchoolNotificationWindow>(main, "_schoolNotification")?.IsVisible == true &&
                Volatile.Read(ref displayed) == 1, "verified school notification did not appear over the slide show");
            var notice = NoticeField<SchoolNotificationWindow>(main, "_schoolNotification")!;
            var handle = new WindowInteropHelper(notice).Handle;
            var message = (ScrollViewer)notice.FindName("MessageScroll");
            PumpUntil(() => notice.IsActive && QuickForeground() == handle && message.IsKeyboardFocusWithin,
                "normal notification did not focus its text above PowerPoint");
            var close = (Button)notice.FindName("DismissButton");
            var center = close.PointToScreen(new Point(close.ActualWidth / 2, close.ActualHeight / 2));
            ClickFixturePoint(handle, center, handle, () => PumpUntil(() => !notice.IsVisible &&
                QuickForeground() == slideHandle, "closing the notification did not restore the slide show"));
            Assert(displayed == 1 && dismissed == 1 && notice.Backdrops.Count == 0, "the notification did not release its masks and record the two receipts");
        }
        finally
        {
            main.Close();
            stop.Cancel();
            try
            {
                if (poll is not null) PumpUntil(() => poll.IsCompleted, "notification polling did not stop");
                server.GetAwaiter().GetResult();
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
            finally { SynchronizationContext.SetSynchronizationContext(previous); }
        }
    }
}

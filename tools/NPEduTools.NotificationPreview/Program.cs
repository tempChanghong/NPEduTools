using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using NPEduTools.App;

internal static class Program
{
    [StructLayout(LayoutKind.Sequential)] private struct NativeRect { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr window, out NativeRect rectangle);
    [DllImport("user32.dll")] private static extern IntPtr GetWindow(IntPtr window, uint command);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr window, IntPtr after, int x, int y, int width, int height, uint flags);
    [STAThread]
    private static int Main(string[] args)
    {
        RenderOptions.ProcessRenderMode = System.Windows.Interop.RenderMode.SoftwareOnly;
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(app.Dispatcher));
        if (args.Length == 2 && args[0] == "--verify")
        {
            try { Verify(Path.GetFullPath(args[1])); app.Shutdown(); return 0; }
            catch (Exception error) { Console.Error.WriteLine(error); app.Shutdown(); return 1; }
        }
        if (args.Length > 1 || (args.Length == 1 && !Enum.TryParse<SchoolNotificationPriority>(args[0], true, out _)))
        { Console.Error.WriteLine("Usage: NotificationPreview [Minor|Normal|Important|Urgent] or --verify OUTPUT_DIRECTORY"); return 2; }
        var priority = args.Length == 0 ? SchoolNotificationPriority.Normal : Enum.Parse<SchoolNotificationPriority>(args[0], true);
        var window = new SchoolNotificationWindow(SchoolNotification.Preview(priority));
        window.Closed += (_, _) => app.Shutdown();
        app.Run(window);
        return 0;
    }

    private static void Pump()
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer(DispatcherPriority.ApplicationIdle) { Interval = TimeSpan.FromMilliseconds(150) };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
        timer.Start();
        Dispatcher.PushFrame(frame);
    }

    private static void Verify(string output)
    {
        Directory.CreateDirectory(output);
        foreach (var screen in System.Windows.Forms.Screen.AllScreens)
            Console.WriteLine($"DISPLAY {screen.Bounds.X},{screen.Bounds.Y} {screen.Bounds.Width}x{screen.Bounds.Height}");
        VerifyBackdropOrder();
        foreach (var priority in Enum.GetValues<SchoolNotificationPriority>())
        {
            var window = new SchoolNotificationWindow(SchoolNotification.Preview(priority));
            window.Show(); Pump();
            window.Width = 1120; window.Height = 720; window.UpdateLayout(); Pump();
            Check(window.Topmost && window.IsVisible && window.ShowActivated == window.RequiresResponse, "activation follows notification priority");
            var backdrops = window.Backdrops.ToArray();
            Check(backdrops.Length == (priority == SchoolNotificationPriority.Minor ? 0 : System.Windows.Forms.Screen.AllScreens.Length), "backdrop only for normal or above, covers every monitor");
            foreach (var backdrop in backdrops)
            {
                Check(GetWindowRect(new WindowInteropHelper(backdrop).Handle, out var bounds) &&
                    bounds.Left == backdrop.Bounds.Left && bounds.Top == backdrop.Bounds.Top &&
                    bounds.Right == backdrop.Bounds.Right && bounds.Bottom == backdrop.Bounds.Bottom, "physical monitor bounds include taskbar");
                Check(backdrop.Topmost && !backdrop.ShowInTaskbar && backdrop.IsHitTestVisible, "backdrop intercepts pointer without taskbar entry");
                backdrop.Close(); Pump(); Check(backdrop.IsVisible, "backdrop cannot be independently dismissed");
                var click = new System.Windows.Input.MouseButtonEventArgs(System.Windows.Input.Mouse.PrimaryDevice,
                    Environment.TickCount, System.Windows.Input.MouseButton.Left) { RoutedEvent = System.Windows.Input.Mouse.PreviewMouseDownEvent };
                backdrop.RaiseEvent(click);
                Check(click.Handled && backdrop.IsVisible && window.IsVisible, "clicking background is consumed without dismissing notice");
            }
            Check(window.ToneInner is SolidColorBrush && window.ToneMiddle is SolidColorBrush &&
                window.ToneLight is SolidColorBrush && window.TonePale is SolidColorBrush, "discrete solid-color decoration");
            var button = (Button)window.FindName("DismissButton");
            Check(button.IsVisible && button.IsEnabled && button.ActualHeight >= 60, "accessible close button");
            Check(button.Background is SolidColorBrush { Color: var buttonColor } && buttonColor == Colors.White, "solid white close action");
            Check(((FrameworkElement)window.FindName("PriorityMark")).IsVisible && window.PriorityNumber == ((int)priority + 1).ToString("D2"), "visible grade mark matches priority");
            var messageScroll = (ScrollViewer)window.FindName("MessageScroll");
            var messageContent = (FrameworkElement)window.FindName("MessageContent");
            var messageOrigin = messageContent.TranslatePoint(new Point(), messageScroll);
            Check(messageScroll.ScrollableHeight == 0 && Math.Abs(messageOrigin.Y + messageContent.ActualHeight / 2 - messageScroll.ViewportHeight / 2) < 1,
                "short message vertically centered inside reading area");
            Check(((TextBlock)window.FindName("NotificationTitle")).Text == window.Notification.Title, "bound plain-text title");
            var color = ((SolidColorBrush)window.Background).Color;
            double Linear(byte value) { double v = value / 255d; return v <= .04045 ? v / 12.92 : Math.Pow((v + .055) / 1.055, 2.4); }
            double contrast = 1.05 / (.2126 * Linear(color.R) + .7152 * Linear(color.G) + .0722 * Linear(color.B) + .05);
            Check(contrast >= 4.5, $"white contrast {contrast:F2}");
            Render(window, Path.Combine(output, priority + ".png"));
            if (window.RequiresResponse) RenderStage(window, Path.Combine(output, priority + "-stage.png"));
            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Pump();
            Check(!window.IsVisible && window.DismissedByUser, "explicit dismissal");
            Check(backdrops.All(w => !w.IsVisible) && window.Backdrops.Count == 0, "dismissal removes every backdrop");
            Console.WriteLine($"PASS {priority}: rendered, contrast {contrast:F2}:1, priority backdrop, physical bounds, manual dismissal and cleanup");
        }

        var longMessage = SchoolNotification.Preview(SchoolNotificationPriority.Normal) with
        { Title = "长通知与小屏幕布局检查", Body = "<b>纯文本，不执行 HTML</b>\n" + string.Concat(Enumerable.Repeat("长段落仍可滚动阅读，不缩小正文，也不遮挡底部关闭按钮。\n", 45)) };
        var compact = new SchoolNotificationWindow(longMessage);
        compact.Show(); Pump(); compact.Width = 640; compact.Height = 460; compact.UpdateLayout(); Pump();
        var scroll = (ScrollViewer)compact.FindName("MessageScroll");
        var close = (Button)compact.FindName("DismissButton");
        Check(((FrameworkElement)compact.FindName("SignalPanel")).ActualWidth == 64, "compact signal panel leaves room for text");
        Check(((FrameworkElement)compact.FindName("PriorityMark")).Visibility == Visibility.Collapsed, "compact layout keeps reading width instead of duplicate grade mark");
        Check(scroll.ScrollableHeight > 0, "long body scrolls");
        var longContent = (FrameworkElement)compact.FindName("MessageContent");
        Check(Math.Abs(longContent.TranslatePoint(new Point(), scroll).Y) < 1, "long message starts at top without clipped opening");
        scroll.ScrollToEnd(); Pump();
        Check(Math.Abs(scroll.VerticalOffset - scroll.ScrollableHeight) < 1, "long message can reach the end");
        scroll.ScrollToHome(); Pump();
        var origin = close.TranslatePoint(new Point(), (UIElement)compact.Content);
        Check(origin.Y >= 0 && origin.Y + close.ActualHeight <= ((FrameworkElement)compact.Content).ActualHeight, "close remains in compact viewport");
        Check(((TextBlock)compact.FindName("NotificationBody")).Text.StartsWith("<b>"), "remote text is literal");
        Render(compact, Path.Combine(output, "Compact-long.png"));
        var compactBackdrops = compact.Backdrops.ToArray();
        compact.Close(); Pump(); Check(!compact.DismissedByUser && compactBackdrops.All(w => !w.IsVisible), "programmatic close removes mask without user dismissal");
        Console.WriteLine("PASS compact long message: scroll, close visibility, literal text, non-user shutdown");
        var durable = new SchoolNotificationWindow(SchoolNotification.Preview(SchoolNotificationPriority.Normal));
        durable.BeforeDismissAsync = () => Task.FromResult(false);
        durable.Show(); Pump();
        ((Button)durable.FindName("DismissButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Pump();
        Check(durable.IsVisible && !durable.DismissedByUser, "failed local persistence keeps dismissal retryable");
        Check(durable.Backdrops.All(w => w.IsVisible && w.Opacity > .99), "save failure keeps backdrop intact");
        durable.InvalidateNotice("此通知已撤回。");
        Check(((TextBlock)durable.FindName("NotificationBody")).Text == "此通知已撤回。", "withdrawal removes old body");
        ((Button)durable.FindName("DismissButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Pump();
        Check(!durable.IsVisible && !durable.DismissedByUser, "invalidated window close is not a receipt");
        Check(durable.Backdrops.Count == 0, "withdrawn notification close removes mask");
        var shutdown = new SchoolNotificationWindow(SchoolNotification.Preview(SchoolNotificationPriority.Urgent));
        shutdown.BeforeDismissAsync = () => throw new InvalidOperationException("Shutdown must not record a dismissal.");
        shutdown.Show(); Pump(); var shutdownBackdrops = shutdown.Backdrops.ToArray();
        shutdown.Shutdown(); Pump();
        Check(!shutdown.DismissedByUser && shutdownBackdrops.All(w => !w.IsVisible), "app shutdown releases mask without a user receipt");
        Console.WriteLine("PASS failed close persistence, withdrawn content and shutdown cleanup");
    }

    private static void VerifyBackdropOrder()
    {
        var notice = new SchoolNotificationWindow(SchoolNotification.Preview(SchoolNotificationPriority.Normal));
        NotificationBackdrop? extra = null;
        try
        {
            notice.Show(); Pump();
            // Overlap a second monitor's independent mask on this screen to exercise the
            // cross-monitor case on a single-monitor machine. Do not make it the notice's owner.
            var bounds = System.Windows.Forms.Screen.FromHandle(new WindowInteropHelper(notice).Handle).Bounds;
            extra = new NotificationBackdrop(bounds, new WindowInteropHelper(notice).Handle, () => notice.Activate());
            extra.Show(); extra.Place(); Pump();
            for (int i = 0; i < 12; i++)
            {
                foreach (var backdrop in notice.Backdrops.Append(extra))
                {
                    backdrop.Place();
                    // Exercise the same topmost placement WPF/Windows may request on show or DPI changes.
                    Check(SetWindowPos(new WindowInteropHelper(backdrop).Handle, new IntPtr(-1), 0, 0, 0, 0, 0x0010 | 0x0001 | 0x0002 | 0x0200), "topmost placement request");
                }
                Pump();
                CheckNoticeAboveMasks(notice, notice.Backdrops.Append(extra));
                notice.Activate(); notice.Left += i % 2 == 0 ? 4 : -4;
            }
            typeof(SchoolNotificationWindow).GetMethod("RebuildBackdrops", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.Invoke(notice, null);
            Pump();
            CheckNoticeAboveMasks(notice, notice.Backdrops.Append(extra));
            Console.WriteLine("PASS native Z-order: overlapping independent mask, 12 repeated placements/activations/moves and mask rebuild");
        }
        finally { extra?.Shutdown(); notice.Shutdown(); Pump(); }
    }

    private static void CheckNoticeAboveMasks(Window notice, IEnumerable<NotificationBackdrop> masks)
    {
        var maskHandles = masks.Select(w => new WindowInteropHelper(w).Handle).ToHashSet();
        var visited = new HashSet<IntPtr>();
        for (var above = GetWindow(new WindowInteropHelper(notice).Handle, 3); above != IntPtr.Zero && visited.Add(above); above = GetWindow(above, 3))
            Check(!maskHandles.Contains(above), "backdrop must remain below notification in native Z-order");
    }

    private static void RenderStage(SchoolNotificationWindow window, string path)
    {
        // The notice is an actual WPF render. The desktop behind it is deliberately synthetic
        // so previews do not capture private applications, documents or taskbar content.
        var notice = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        notice.Render(window);
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(225, 232, 229)), null, new Rect(0, 0, 1600, 1000));
            dc.DrawRectangle(Brushes.White, null, new Rect(48, 42, 1504, 916));
            dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(224, 230, 227)), null, new Rect(48, 42, 220, 916));
            for (int i = 0; i < 7; i++) dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(195, 206, 201)), null, new Rect(82, 170 + i * 62, 146, 16));
            dc.DrawText(new FormattedText("桌面背景 · 示意", System.Globalization.CultureInfo.CurrentCulture,
                FlowDirection.LeftToRight, new Typeface("Microsoft YaHei UI"), 24, Brushes.DarkSlateGray, 1), new Point(82, 88));
            dc.DrawRectangle(window.Backdrops[0].Background, null, new Rect(0, 0, 1600, 1000));
            dc.DrawImage(notice, new Rect((1600 - notice.Width) / 2, (1000 - notice.Height) / 2, notice.Width, notice.Height));
        }
        var bitmap = new RenderTargetBitmap(1600, 1000, 96, 96, PixelFormats.Pbgra32); bitmap.Render(visual);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var file = File.Create(path); encoder.Save(file);
    }

    private static void Render(Window window, string path)
    {
        // Render the full window, including its solid background, at 96 DPI for comparable previews.
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(window.ActualWidth), (int)Math.Ceiling(window.ActualHeight), 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(window);
        var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path); png.Save(stream);
    }

    private static void Check(bool condition, string message)
    { if (!condition) throw new InvalidOperationException(message); }
}

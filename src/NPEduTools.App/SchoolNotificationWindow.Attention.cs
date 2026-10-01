using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Microsoft.Win32;

namespace NPEduTools.App;

public partial class SchoolNotificationWindow
{
    private readonly List<NotificationBackdrop> _backdrops = [];
    private bool _attentionReleased;
    internal IReadOnlyList<NotificationBackdrop> Backdrops => _backdrops;
    private static bool MotionEnabled => SystemParameters.ClientAreaAnimation && !SystemParameters.HighContrast;

    private void InitializeAttention()
    {
        if (RequiresResponse)
        {
            RebuildBackdrops();
            SystemEvents.DisplaySettingsChanged += DisplaySettingsChanged;
            Activate();
            MessageScroll.Focus();
        }
        if (!MotionEnabled) return;
        BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(180)) { FillBehavior = FillBehavior.Stop });
        EntranceOffset.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(12, 0, TimeSpan.FromMilliseconds(180))
        { EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }, FillBehavior = FillBehavior.Stop });
    }

    private void DisplaySettingsChanged(object? sender, EventArgs e) => Dispatcher.BeginInvoke(() =>
    {
        if (!_attentionReleased && IsVisible) { RebuildBackdrops(); FitWorkArea(); }
    });

    private void RebuildBackdrops()
    {
        var old = _backdrops.ToArray();
        var next = new List<NotificationBackdrop>();
        try
        {
            var current = System.Windows.Forms.Screen.FromHandle(new WindowInteropHelper(this).Handle);
            foreach (var screen in System.Windows.Forms.Screen.AllScreens)
            {
                var backdrop = new NotificationBackdrop(screen.Bounds, new WindowInteropHelper(this).Handle,
                    () => { Activate(); MessageScroll.Focus(); });
                next.Add(backdrop);
                backdrop.Show();
                backdrop.Place();
                if (MotionEnabled) backdrop.BeginAnimation(OpacityProperty,
                    new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(180)) { FillBehavior = FillBehavior.Stop });
                if (screen.DeviceName == current.DeviceName) Owner = backdrop;
            }
            _backdrops.Clear(); _backdrops.AddRange(next);
            foreach (var backdrop in old) backdrop.Shutdown();
        }
        catch
        {
            // Never strand an input-blocking surface if monitor/window creation fails.
            Owner = null;
            foreach (var backdrop in next) backdrop.Shutdown();
            foreach (var backdrop in old) backdrop.Shutdown();
            _backdrops.Clear();
            throw;
        }
    }

    private async Task AnimateDismissAsync()
    {
        if (_attentionReleased || !MotionEnabled || !IsVisible) return;
        var duration = TimeSpan.FromMilliseconds(120);
        BeginAnimation(OpacityProperty, new DoubleAnimation(Opacity, 0, duration));
        foreach (var backdrop in _backdrops) backdrop.BeginAnimation(OpacityProperty, new DoubleAnimation(backdrop.Opacity, 0, duration));
        await Task.Delay(duration);
    }

    private void ReleaseAttention()
    {
        if (_attentionReleased) return;
        _attentionReleased = true;
        SystemEvents.DisplaySettingsChanged -= DisplaySettingsChanged;
        // Detach before closing the owner so it cannot recursively close this window.
        Owner = null;
        foreach (var backdrop in _backdrops) backdrop.Shutdown();
        _backdrops.Clear();
    }
}

// One physical-pixel surface per monitor, including the taskbar. It intercepts pointer
// input but installs no keyboard hooks and never modifies the Windows secure desktop.
internal sealed class NotificationBackdrop : Window
{
    private bool _closing;
    private readonly IntPtr _noticeHandle;
    private HwndSource? _source;
    private const uint NoZOrder = 0x0004, NoActivate = 0x0010, NoOwnerZOrder = 0x0200;
    private const int WindowPositionChanging = 0x0046;
    [StructLayout(LayoutKind.Sequential)]
    private struct WindowPosition
    {
        public IntPtr Window, InsertAfter;
        public int X, Y, Width, Height;
        public uint Flags;
    }
    internal System.Drawing.Rectangle Bounds { get; }
    [DllImport("user32.dll", SetLastError = true)] private static extern bool SetWindowPos(IntPtr window, IntPtr after, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern IntPtr GetWindowLongPtr(IntPtr window, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] private static extern IntPtr SetWindowLongPtr(IntPtr window, int index, IntPtr value);

    internal NotificationBackdrop(System.Drawing.Rectangle bounds, IntPtr noticeHandle, Action focusNotice)
    {
        if (noticeHandle == IntPtr.Zero) throw new ArgumentException("The notification must have a native window before creating its backdrop.", nameof(noticeHandle));
        _noticeHandle = noticeHandle;
        Bounds = bounds;
        Title = "NPEduTools · 通知遮罩";
        WindowStyle = WindowStyle.None; ResizeMode = ResizeMode.NoResize;
        AllowsTransparency = true; Background = new SolidColorBrush(Color.FromArgb(158, 0, 0, 0));
        ShowInTaskbar = false; ShowActivated = false; Topmost = true;
        SourceInitialized += (_, _) =>
        {
            var handle = new WindowInteropHelper(this).Handle;
            // Tool window, no activation: clicking the backdrop focuses the actual notice.
            SetWindowLongPtr(handle, -20, new IntPtr(GetWindowLongPtr(handle, -20).ToInt64() | 0x80 | 0x08000000));
            _source = HwndSource.FromHwnd(handle);
            _source?.AddHook(KeepBehindNotice);
        };
        PreviewMouseDown += (_, e) => { e.Handled = true; focusNotice(); };
        PreviewTouchDown += (_, e) => { e.Handled = true; focusNotice(); };
        DpiChanged += (_, _) => Dispatcher.BeginInvoke(() => { if (!_closing) Place(); });
    }

    internal void Place()
    {
        if (!SetWindowPos(new WindowInteropHelper(this).Handle, _noticeHandle, Bounds.X, Bounds.Y, Bounds.Width, Bounds.Height, NoActivate | NoOwnerZOrder))
            throw new Win32Exception(Marshal.GetLastWin32Error());
    }
    private IntPtr KeepBehindNotice(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message == WindowPositionChanging && !_closing)
        {
            var position = Marshal.PtrToStructure<WindowPosition>(lParam);
            if ((position.Flags & NoZOrder) == 0)
            {
                // WPF can request HWND_TOPMOST again when showing/repositioning a window.
                // Constrain that request before Windows applies it, including masks which
                // are not the notice's owner. No delayed activation or polling is needed.
                position.InsertAfter = _noticeHandle;
                position.Flags |= NoActivate | NoOwnerZOrder;
                Marshal.StructureToPtr(position, lParam, false);
            }
        }
        return IntPtr.Zero;
    }
    protected override void OnClosing(CancelEventArgs e) { if (!_closing) e.Cancel = true; base.OnClosing(e); }
    protected override void OnClosed(EventArgs e)
    {
        _source?.RemoveHook(KeepBehindNotice); _source = null;
        base.OnClosed(e);
    }
    internal void Shutdown() { _closing = true; Close(); }
}

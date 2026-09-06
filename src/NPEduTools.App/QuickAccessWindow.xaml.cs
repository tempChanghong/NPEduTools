using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using NPEduTools.Contracts;
using Forms = System.Windows.Forms;

namespace NPEduTools.App;

public partial class QuickAccessWindow : Window
{
    private sealed record Placement(bool LeftSide = false, double RelativeY = 0.78, string? Display = null);
    private readonly string _settingsPath;
    private readonly Action _power, _pause, _startClassIsland, _settings;
    private readonly Action<ShortcutEntry> _openShortcut;
    private readonly Action _manageShortcuts, _repairShortcut;
    private Placement _placement = new();
    private nint _handle, _previous;
    private bool _expanded, _dragging, _closing, _positioning;
    private Point? _dragStart;

    public QuickAccessWindow(string endpoint, Action power, Action pause, Action startClassIsland, Action settings,
        Action<ShortcutEntry> openShortcut, Action manageShortcuts, Action repairShortcut)
    {
        _power = power; _pause = pause; _startClassIsland = startClassIsland; _settings = settings;
        _openShortcut = openShortcut; _manageShortcuts = manageShortcuts; _repairShortcut = repairShortcut;
        _settingsPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NPEduTools", "ui",
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(endpoint)))[..24] + ".json");
        try
        {
            if (File.Exists(_settingsPath) && new FileInfo(_settingsPath).Length < 4096)
            {
                var saved = JsonSerializer.Deserialize<Placement>(File.ReadAllText(_settingsPath));
                if (saved is not null && double.IsFinite(saved.RelativeY)) _placement = saved with { RelativeY = Math.Clamp(saved.RelativeY, 0, 1) };
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException) { }
        InitializeComponent();
        SelectShortcutPage(false);
        SourceInitialized += (_, _) =>
        {
            _handle = new WindowInteropHelper(this).Handle;
            HwndSource.FromHwnd(_handle)?.AddHook(WindowHook);
            Position();
        };
        Deactivated += (_, _) => { if (_expanded) Collapse(false); };
        Closing += (_, e) => { if (!_closing) { e.Cancel = true; Collapse(); } };
        DpiChanged += (_, _) => Dispatcher.BeginInvoke(Position);
        Microsoft.Win32.SystemEvents.DisplaySettingsChanged += DisplayChanged;
        Closed += (_, _) => { _closing = true; Microsoft.Win32.SystemEvents.DisplaySettingsChanged -= DisplayChanged; };
        EdgeHandle.PreviewMouseLeftButtonDown += (_, e) =>
        { _dragStart = PointToScreen(e.GetPosition(this)); _dragging = false; EdgeHandle.CaptureMouse(); e.Handled = true; };
        EdgeHandle.PreviewMouseMove += (_, e) => { if (e.LeftButton == MouseButtonState.Pressed && _dragStart is not null) MoveHandle(PointToScreen(e.GetPosition(this))); };
        EdgeHandle.PreviewMouseLeftButtonUp += (_, e) =>
        { if (_dragStart is null) return; EdgeHandle.ReleaseMouseCapture(); FinishDrag(); e.Handled = true; };
        EdgeHandle.PreviewTouchDown += (_, e) =>
        { _dragStart = PointToScreen(e.GetTouchPoint(this).Position); _dragging = false; EdgeHandle.CaptureTouch(e.TouchDevice); e.Handled = true; };
        EdgeHandle.PreviewTouchMove += (_, e) => { if (_dragStart is not null) MoveHandle(PointToScreen(e.GetTouchPoint(this).Position)); e.Handled = true; };
        EdgeHandle.PreviewTouchUp += (_, e) => { EdgeHandle.ReleaseTouchCapture(e.TouchDevice); FinishDrag(); e.Handled = true; };
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) { Collapse(); e.Handled = true; } };
    }

    private nint WindowHook(nint hwnd, int message, nint wParam, nint lParam, ref bool handled)
    {
        // A collapsed handle must not take keyboard focus away from the slide show.
        if (message == 0x21 && !_expanded) { handled = true; return 3; } // WM_MOUSEACTIVATE / MA_NOACTIVATE
        return 0;
    }

    private void DisplayChanged(object? sender, EventArgs e) => Dispatcher.BeginInvoke(Position);
    private Forms.Screen Screen => Forms.Screen.AllScreens.FirstOrDefault(screen => screen.DeviceName == _placement.Display)
        ?? Forms.Screen.PrimaryScreen ?? Forms.Screen.AllScreens[0];

    private double Scale()
    {
        uint dpi = GetDpiForWindow(_handle);
        return dpi > 0 ? dpi / 96.0 : 1;
    }

    private void Position()
    {
        if (_handle == 0 || _closing || _positioning) return;
        _positioning = true;
        try
        {
            // WPF handles WM_DPICHANGED; its DpiChanged event reapplies bounds on the new monitor.
            var screen = Screen; var area = screen.WorkingArea; double scale = Scale();
            int width = (int)Math.Round((_expanded ? 352 : 64) * scale);
            int height = Math.Min((int)Math.Round((_expanded ? 484 : 84) * scale), area.Height);
            double anchor = area.Top + (area.Height - 84 * scale) * _placement.RelativeY;
            int y = (int)Math.Clamp(anchor + (_expanded ? 84 * scale - height : 0), area.Top, area.Bottom - height);
            int x = _placement.LeftSide ? area.Left : area.Right - width;
            SetWindowPos(_handle, new nint(-1), x, y, width, height, 0x10); // SWP_NOACTIVATE
            CollapseButton.Content = _placement.LeftSide ? "‹" : "›";
        }
        finally { _positioning = false; }
    }

    public void OpenPanel()
    {
        if (_closing) return;
        _previous = GetForegroundWindow();
        _expanded = true;
        EdgeHandle.Visibility = Visibility.Collapsed; PanelSurface.Visibility = Visibility.Visible;
        if (!IsVisible) Show();
        Position(); Activate();
    }

    public void Collapse(bool restoreFocus = true)
    {
        if (_closing) return;
        bool wasExpanded = _expanded;
        _expanded = false;
        PanelSurface.Visibility = Visibility.Collapsed; EdgeHandle.Visibility = Visibility.Visible;
        Position();
        if (restoreFocus && wasExpanded && GetForegroundWindow() == _handle && _previous != 0 && IsWindow(_previous)) SetForegroundWindow(_previous);
    }

    public bool LeftSide => _placement.LeftSide;

    public void SetSide(bool leftSide)
    { _placement = _placement with { LeftSide = leftSide }; Collapse(); Position(); Save(); }

    public void Shutdown() { _closing = true; Close(); }

    private void MoveHandle(Point point)
    {
        if (_expanded || _dragStart is not { } start || (!_dragging && (point - start).Length < 8)) return;
        _dragging = true;
        var screen = Forms.Screen.FromPoint(new System.Drawing.Point((int)point.X, (int)point.Y));
        double available = screen.WorkingArea.Height - 84 * Scale();
        double ratio = available <= 0 ? 0 : (point.Y - screen.WorkingArea.Top - 42 * Scale()) / available;
        _placement = _placement with { RelativeY = Math.Clamp(ratio, 0, 1), Display = screen.DeviceName };
        Position();
    }

    private void FinishDrag()
    {
        if (_dragStart is null) return;
        _dragStart = null;
        if (_dragging) Save(); else OpenPanel();
        _dragging = false;
    }

    private void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_settingsPath)!);
            File.WriteAllText(_settingsPath + ".tmp", JsonSerializer.Serialize(_placement));
            File.Move(_settingsPath + ".tmp", _settingsPath, true);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
    }

    public void Update(TouchAssistState? state, string status, bool available, string classIslandStatus, bool canStart, string startLabel = "启动")
    {
        StatusText.Text = status;
        PowerButton.Content = state?.Running == true ? "停止辅助" : "开启辅助";
        PowerButton.IsEnabled = available;
        PauseButton.Content = state?.Paused == true ? "继续" : "暂停";
        PauseButton.Visibility = state?.Running == true ? Visibility.Visible : Visibility.Collapsed;
        PauseButton.IsEnabled = available;
        ClassIslandStatus.Text = classIslandStatus; ClassIslandButton.IsEnabled = canStart;
        ClassIslandButton.Content = startLabel;
    }

    private void HandleClicked(object sender, RoutedEventArgs e) => OpenPanel();
    private void PowerClicked(object sender, RoutedEventArgs e) => _power();
    private void PauseClicked(object sender, RoutedEventArgs e) => _pause();
    private void ClassIslandClicked(object sender, RoutedEventArgs e) => _startClassIsland();
    private void SettingsClicked(object sender, RoutedEventArgs e) { Collapse(false); _settings(); }
    public void SetShortcuts(ShortcutEntry[] items)
    {
        ShortcutItems.ItemsSource = items;
        ShortcutsEmpty.Visibility = items.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    public void SetShortcutStatus(string message, bool available, bool repair)
    {
        ShortcutStatus.Text = message; ShortcutItems.IsEnabled = ShortcutRepair.IsEnabled = available;
        ShortcutRepair.Visibility = repair ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ToolsTabClicked(object sender, RoutedEventArgs e) => SelectShortcutPage(false);
    private void ShortcutsTabClicked(object sender, RoutedEventArgs e) => SelectShortcutPage(true);
    private void SelectShortcutPage(bool shortcuts)
    {
        ToolsPage.Visibility = SettingsFooter.Visibility = shortcuts ? Visibility.Collapsed : Visibility.Visible;
        ShortcutsPage.Visibility = ShortcutsFooter.Visibility = shortcuts ? Visibility.Visible : Visibility.Collapsed;
        var active = (System.Windows.Media.Brush)FindResource("Accent");
        var inactive = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(238, 242, 243));
        ToolsTab.Background = shortcuts ? inactive : active;
        ToolsTab.Foreground = shortcuts ? System.Windows.Media.Brushes.DarkSlateGray : System.Windows.Media.Brushes.White;
        ShortcutsTab.Background = shortcuts ? active : inactive;
        ShortcutsTab.Foreground = shortcuts ? System.Windows.Media.Brushes.White : System.Windows.Media.Brushes.DarkSlateGray;
    }
    private void ShortcutClicked(object sender, RoutedEventArgs e)
    { if (sender is FrameworkElement { DataContext: ShortcutEntry entry }) _openShortcut(entry); }
    private void ManageShortcutsClicked(object sender, RoutedEventArgs e) { Collapse(false); _manageShortcuts(); }
    private void RepairShortcutClicked(object sender, RoutedEventArgs e) { Collapse(false); _repairShortcut(); }
    private void CollapseClicked(object sender, RoutedEventArgs e) => Collapse();

    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(nint hwnd);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(nint hwnd, nint after, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(nint hwnd);
    [DllImport("user32.dll")] private static extern bool IsWindow(nint hwnd);
}

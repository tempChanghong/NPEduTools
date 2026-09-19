using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using NPEduTools.Contracts;
using Forms = System.Windows.Forms;

namespace NPEduTools.App;

public partial class QuickAccessWindow : Window
{
    private Action? _examAware;
    public void SetExamAwareAction(Action action) => _examAware = action;
    private void ExamAwareClicked(object sender, RoutedEventArgs e) => _examAware?.Invoke();
    private const double RailWidth = 80, RailHeight = 284, PanelWidth = 440, PanelHeight = 620;
    private sealed record Placement(bool LeftSide = false, double RelativeY = 0.78, string? Display = null);
    private readonly string _settingsPath;
    private readonly Action _power, _pause, _startClassIsland, _settings;
    private readonly Action<ShortcutEntry> _openShortcut;
    private readonly Action _manageShortcuts, _repairShortcut;
    private Placement _placement = new();
    private nint _handle, _previous;
    private bool _expanded, _dragging, _closing, _positioning, _shortcutsSelected;
    private Point? _dragStart;
    private double _dragOffsetY;
    private Action? _openRecording, _pauseRecording, _stopRecording, _openRecordingPlan, _skipAutomaticToday;

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
        { BeginDrag(PointToScreen(e.GetPosition(this))); EdgeHandle.CaptureMouse(); e.Handled = true; };
        EdgeHandle.PreviewMouseMove += (_, e) => { if (e.LeftButton == MouseButtonState.Pressed && _dragStart is not null) MoveHandle(PointToScreen(e.GetPosition(this))); };
        EdgeHandle.PreviewMouseLeftButtonUp += (_, e) =>
        { if (_dragStart is null) return; FinishDrag(); EdgeHandle.ReleaseMouseCapture(); e.Handled = true; };
        EdgeHandle.PreviewTouchDown += (_, e) =>
        { BeginDrag(PointToScreen(e.GetTouchPoint(this).Position)); EdgeHandle.CaptureTouch(e.TouchDevice); e.Handled = true; };
        EdgeHandle.PreviewTouchMove += (_, e) => { if (_dragStart is not null) MoveHandle(PointToScreen(e.GetTouchPoint(this).Position)); e.Handled = true; };
        EdgeHandle.PreviewTouchUp += (_, e) => { FinishDrag(); EdgeHandle.ReleaseTouchCapture(e.TouchDevice); e.Handled = true; };
        EdgeHandle.LostMouseCapture += (_, _) => CancelDrag();
        EdgeHandle.LostTouchCapture += (_, _) => CancelDrag();
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
            int width = Math.Min((int)Math.Round((_expanded ? PanelWidth : RailWidth) * scale), area.Width);
            int height = Math.Min((int)Math.Round((_expanded ? PanelHeight : RailHeight) * scale), area.Height);
            double anchor = area.Top + Math.Max(0, area.Height - RailHeight * scale) * _placement.RelativeY;
            int y = (int)Math.Clamp(anchor + (_expanded ? RailHeight * scale - height : 0), area.Top, area.Bottom - height);
            int x = _placement.LeftSide ? area.Left : area.Right - width;
            var rail = new GridLength(RailWidth - 12);
            var panel = _expanded ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
            LeftColumn.Width = _placement.LeftSide ? rail : panel;
            RightColumn.Width = _placement.LeftSide ? panel : rail;
            Grid.SetColumn(Rail, _placement.LeftSide ? 0 : 1);
            Grid.SetColumn(PanelSurface, _placement.LeftSide ? 1 : 0);
            PanelSurface.Margin = _placement.LeftSide ? new Thickness(8, 0, 0, 0) : new Thickness(0, 0, 8, 0);
            SetWindowPos(_handle, new nint(-1), x, y, width, height, 0x10); // SWP_NOACTIVATE
            CollapseButton.Content = _placement.LeftSide ? "‹" : "›";
        }
        finally { _positioning = false; }
    }

    public void OpenPanel()
    {
        if (_closing) return;
        if (!_expanded) _previous = GetForegroundWindow();
        bool animate = !_expanded && SystemParameters.ClientAreaAnimation;
        _expanded = true;
        PanelSurface.Visibility = Visibility.Visible;
        RefreshRail();
        if (!IsVisible) Show();
        Position(); Activate();
        if (animate) PanelSurface.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(140)) { FillBehavior = FillBehavior.Stop });
    }

    public void Collapse(bool restoreFocus = true)
    {
        if (_closing) return;
        bool wasExpanded = _expanded;
        _expanded = false;
        PanelSurface.BeginAnimation(OpacityProperty, null);
        PanelSurface.Visibility = Visibility.Collapsed;
        RefreshRail();
        Position();
        if (restoreFocus && wasExpanded && GetForegroundWindow() == _handle && _previous != 0 && IsWindow(_previous)) SetForegroundWindow(_previous);
    }

    public bool LeftSide => _placement.LeftSide;

    private Action? _onboarding;
    public void SetOnboardingAction(Action action, bool pending)
    {
        _onboarding = action;
        OnboardingEntry.Visibility = pending ? Visibility.Visible : Visibility.Collapsed;
        EdgeHandle.ToolTip = pending ? "初始设置待完成 · 点击展开，或拖动调整位置" : "点击展开或收起；拖动调整位置";
    }
    private void OnboardingClicked(object sender, RoutedEventArgs e) { Collapse(false); _onboarding?.Invoke(); }

    public void SetSide(bool leftSide)
    { _placement = _placement with { LeftSide = leftSide }; Collapse(); Position(); Save(); }

    public void Shutdown() { _closing = true; Close(); }

    private void BeginDrag(Point point)
    {
        _dragStart = point; _dragging = false;
        _dragOffsetY = point.Y - PointToScreen(new Point(0, 0)).Y;
    }

    private void CancelDrag()
    {
        // A release handled below clears _dragStart first; unexpected capture loss only ends the drag.
        if (_dragStart is null) return;
        if (_dragging) Save();
        _dragStart = null; _dragging = false;
    }

    private void MoveHandle(Point point)
    {
        if (_expanded || _dragStart is not { } start || (!_dragging && (point - start).Length < 8)) return;
        _dragging = true;
        var screen = Forms.Screen.FromPoint(new System.Drawing.Point((int)point.X, (int)point.Y));
        double available = screen.WorkingArea.Height - RailHeight * Scale();
        double ratio = available <= 0 ? 0 : (point.Y - screen.WorkingArea.Top - _dragOffsetY) / available;
        _placement = _placement with { RelativeY = Math.Clamp(ratio, 0, 1), Display = screen.DeviceName };
        Position();
    }

    private void FinishDrag()
    {
        if (_dragStart is null) return;
        _dragStart = null;
        if (_dragging) Save(); else if (_expanded) Collapse(); else OpenPanel();
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
        RailStatusDot.Fill = new SolidColorBrush((Color)ColorConverter.ConvertFromString(
            state?.Error is not null ? "#CD7552" : state?.Running != true ? "#A5B3AB" : state.Paused ? "#C69A4F" : "#147D68"));
        RailTools.ToolTip = "课堂工具 · " + status;
    }

    private void HandleClicked(object sender, RoutedEventArgs e) { if (_expanded) Collapse(); else OpenPanel(); }
    public void SetRecordingActions(Action open, Action pause, Action stop, Action plan, Action skipToday)
    { _openRecording = open; _pauseRecording = pause; _stopRecording = stop; _openRecordingPlan = plan; _skipAutomaticToday = skipToday; }
    public void UpdateAutomaticRecording(AutomaticRecordingState state)
    {
        AutomaticStatus.Text = state.Message;
        AutomaticSkipDay.Visibility = state.Enabled ? Visibility.Visible : Visibility.Collapsed;
    }
    private void AutomaticSkipDayClicked(object sender, RoutedEventArgs e) => _skipAutomaticToday?.Invoke();
    public void UpdateRecording(RecordingState state)
    {
        RecordingStatus.Text = state.Active ? $"{state.Message} · {TimeSpan.FromSeconds(Math.Max(0, state.Seconds)):hh\\:mm\\:ss}" : state.Message;
        RecordingButton.Content = state.Active ? "控制" : "录制";
        if (state.Active && state.Control is { Owner: "Automatic", HardDeadline: > 0 } c)
            RecordingStatus.Text = "自动 · " + RecordingStatus.Text + $" · 至多剩余 {TimeSpan.FromSeconds(Math.Max(0, RecorderDeadline.SecondsUntil(c.HardDeadline))):hh\\:mm\\:ss}";
        RecordingControls.Visibility = state.Active ? Visibility.Visible : Visibility.Collapsed;
        RecordingPause.IsEnabled = RecordingStop.IsEnabled = !state.Busy;
        RecordingPause.Content = state.Phase == "Paused" ? "继续" : "暂停";
    }
    private void RecordingClicked(object sender, RoutedEventArgs e) { Collapse(false); _openRecording?.Invoke(); }
    private void RecordingPlanClicked(object sender, RoutedEventArgs e) { Collapse(false); _openRecordingPlan?.Invoke(); }
    private void RecordingPauseClicked(object sender, RoutedEventArgs e) => _pauseRecording?.Invoke();
    private void RecordingStopClicked(object sender, RoutedEventArgs e) => _stopRecording?.Invoke();
    private void RailToolsClicked(object sender, RoutedEventArgs e) { SelectShortcutPage(false); OpenPanel(); }
    private void RailShortcutsClicked(object sender, RoutedEventArgs e) { SelectShortcutPage(true); OpenPanel(); }
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
        _shortcutsSelected = shortcuts;
        ToolsPage.Visibility = SettingsFooter.Visibility = shortcuts ? Visibility.Collapsed : Visibility.Visible;
        ShortcutsPage.Visibility = ShortcutsFooter.Visibility = shortcuts ? Visibility.Visible : Visibility.Collapsed;
        var active = (System.Windows.Media.Brush)FindResource("Accent");
        var inactive = Brushes.Transparent;
        ToolsTab.Background = shortcuts ? inactive : active;
        ToolsTab.Foreground = shortcuts ? System.Windows.Media.Brushes.DarkSlateGray : System.Windows.Media.Brushes.White;
        ShortcutsTab.Background = shortcuts ? active : inactive;
        ShortcutsTab.Foreground = shortcuts ? System.Windows.Media.Brushes.White : System.Windows.Media.Brushes.DarkSlateGray;
        RefreshRail();
    }
    private void RefreshRail()
    {
        RailTools.Tag = _expanded && !_shortcutsSelected ? "active" : null;
        RailShortcuts.Tag = _expanded && _shortcutsSelected ? "active" : null;
        System.Windows.Automation.AutomationProperties.SetName(EdgeHandle,
            _expanded ? "收起快捷工具" : "展开快捷工具，可上下拖动");
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

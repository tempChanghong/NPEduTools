using System.IO;
using System.Diagnostics;
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
using System.Windows.Threading;
using NPEduTools.Contracts;
using Forms = System.Windows.Forms;

namespace NPEduTools.App;

public partial class QuickAccessWindow : Window
{
    private Action? _examAware;
    private Action? _secRandom;
    public void SetSecRandomAction(Action action) => _secRandom = action;
    private void SecRandomClicked(object sender, RoutedEventArgs e) => _secRandom?.Invoke();
    private Action? _classroomMode;
    public void SetClassroomModeAction(Action action) => _classroomMode = action;
    private void ClassroomModeClicked(object sender, RoutedEventArgs e) => _classroomMode?.Invoke();
    public void UpdateClassroomMode(ClassroomModePresentation view)
    {
        ClassroomModeTitle.Text = view.Title;
        ClassroomModeDetail.Text = view.Detail;
        RailModeText.Text = view.RailLabel;
        _handleStatus = view.Title + "\n" + view.Detail;
        RefreshHandleLabel();
        ClassroomModeTitle.SetResourceReference(TextBlock.ForegroundProperty, view.Attention ? "TeacherWarningBrush" : "TeacherTextBrush");
    }
    public void SetExamAwareAction(Action action) => _examAware = action;
    private void ExamAwareClicked(object sender, RoutedEventArgs e) => _examAware?.Invoke();
    private const double RailWidth = 80, RailHeight = 390, CompactHeight = 94, PanelWidth = 470, PanelHeight = 620;
    private const double MinimumTouchTarget = 44, RailButtonSize = 52, MoveHintWidth = 202;
    private sealed record Placement(bool LeftSide = false, double RelativeY = 0.78, string? Display = null, bool Compact = false);
    private readonly string _settingsPath;
    private readonly Action _power, _pause, _startClassIsland, _settings;
    private readonly Action<ShortcutEntry> _openShortcut;
    private readonly Action _manageShortcuts, _repairShortcut;
    private Placement _placement = new();
    private nint _handle, _previous, _lastExternalForeground, _foregroundHook;
    private readonly ForegroundCallback _foregroundChanged;
    private bool _autoHidden;
    private bool _expanded, _dragging, _closing, _positioning, _shortcutsSelected;
    internal long ShortcutViewRevision { get; private set; }
    private bool _tapCancelled, _onboardingPending;
    private string? _handleStatus;
    private Placement? _moveOrigin;
    private nint _moveReturnFocus;
    private Point? _handlePressStart;
    private TouchDevice? _handleTouch;
    private int _handleInputRevision;
    private double _dragOffsetY;
    private double _gestureScale = 1;
    private long _handleActivity = Stopwatch.GetTimestamp();
    private readonly DispatcherTimer _idleTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private bool _studyTask, _recordingTask, _taskOrderDirty;
    private bool _recordingControlSlot, _studyNextSlot;
    private Action? _study;
    private Action? _openRecording, _openRecordingPlan, _skipAutomaticToday;

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
        try { _densityPreference = DesktopDensity.Read(endpoint); }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or JsonException) { }
        InitializeComponent();
        WireDrawTouch();
        InitializePptStrip();
        InitializeRecordingControls();
        InitializeLayoutOptions();
        SelectShortcutPage(false); RefreshCompact();
        _foregroundChanged = (_, _, window, _, _, _, _) => RememberExternalForeground(window);
        _idleTimer.Tick += (_, _) => RefreshHandleOpacity();
        _idleTimer.Start();
        EdgeHandle.MouseEnter += (_, _) => HandleActivity();
        EdgeHandle.MouseLeave += (_, _) => HandleActivity();
        EdgeHandle.GotKeyboardFocus += (_, _) => HandleActivity();
        ShortcutItems.SizeChanged += (_, _) => RefreshShortcutColumns();
        SourceInitialized += (_, _) =>
        {
            _handle = new WindowInteropHelper(this).Handle;
            HwndSource.FromHwnd(_handle)?.AddHook(WindowHook);
            RememberExternalForeground(GetForegroundWindow());
            const uint foregroundEvent = 3; // EVENT_SYSTEM_FOREGROUND, delivered on this UI thread.
            _foregroundHook = SetWinEventHook(foregroundEvent, foregroundEvent, 0, _foregroundChanged, 0, 0, 0);
            Position();
        };
        Deactivated += (_, _) => Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
        {
            // Activation can briefly change while touch capture is being released. Check the
            // current foreground after that input transaction, not the earlier notification.
            if ((_expanded || _moveOrigin is not null) && !IsActive && GetForegroundWindow() != _handle)
                Collapse(false);
        });
        Closing += (_, e) => { if (!_closing) { e.Cancel = true; Collapse(); } };
        DpiChanged += (_, _) => Dispatcher.BeginInvoke(() => { CancelLayoutSidePress(); Position(); });
        Microsoft.Win32.SystemEvents.DisplaySettingsChanged += DisplayChanged;
        Closed += (_, _) =>
        {
            _closing = true;
            _idleTimer.Stop();
            if (_foregroundHook != 0) UnhookWinEvent(_foregroundHook);
            Microsoft.Win32.SystemEvents.DisplaySettingsChanged -= DisplayChanged;
        };
        EdgeHandle.PreviewMouseLeftButtonDown += (_, e) =>
        {
            if (!IsPromotedTouch(e) && _handleTouch is null && _handlePressStart is null) { BeginHandlePress(PointToScreen(e.GetPosition(this))); if (!EdgeHandle.CaptureMouse()) CancelHandlePress(); }
            e.Handled = true;
        };
        EdgeHandle.PreviewMouseMove += (_, e) =>
        { if (!IsPromotedTouch(e) && _handleTouch is null && e.LeftButton == MouseButtonState.Pressed && _handlePressStart is not null) TrackHandlePress(PointToScreen(e.GetPosition(this))); };
        EdgeHandle.PreviewMouseLeftButtonUp += (_, e) =>
        { if (!IsPromotedTouch(e) && _handleTouch is null) ReleaseHandle(PointToScreen(e.GetPosition(this))); e.Handled = true; };
        EdgeHandle.PreviewTouchDown += (_, e) =>
        {
            if (_handleTouch is null && _handlePressStart is null)
            { _handleTouch = e.TouchDevice; BeginHandlePress(PointToScreen(e.GetTouchPoint(this).Position)); if (!EdgeHandle.CaptureTouch(e.TouchDevice)) { _handleTouch = null; CancelHandlePress(); } }
            e.Handled = true;
        };
        EdgeHandle.PreviewTouchMove += (_, e) =>
        { if (_handleTouch == e.TouchDevice && _handlePressStart is not null) TrackHandlePress(PointToScreen(e.GetTouchPoint(this).Position)); e.Handled = true; };
        EdgeHandle.PreviewTouchUp += (_, e) => { if (_handleTouch == e.TouchDevice) ReleaseHandle(PointToScreen(e.GetTouchPoint(this).Position)); e.Handled = true; };
        EdgeHandle.LostMouseCapture += (_, _) => { if (_handleTouch is null) CancelHandlePress(); };
        EdgeHandle.LostTouchCapture += (_, e) => { if (_handleTouch == e.TouchDevice) { _handleTouch = null; CancelHandlePress(); } };
        PreviewTouchDown += (_, e) =>
        {
            if ((_handleTouch ?? _drawTouch ?? _pptTouch ?? RecordingTouch ?? _layoutTouch) is { } active && active != e.TouchDevice) e.Handled = true;
        };
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key != Key.Escape) return;
            if (IsLayoutOpen)
            {
                AbortHandleGesture();
                CloseLayoutOptions(refocus: true);
            }
            else if (!DismissRecordingConfirmation(refocus: true)) Collapse();
            e.Handled = true;
        };
    }

    // A real pen uses mouse promotion too, but has no TouchDown/TouchUp counterpart.
    // Ignore only touch promotion so pen users can still operate/drag the handle.
    private static bool IsPromotedTouch(MouseEventArgs e) => e.StylusDevice?.TabletDevice.Type == TabletDeviceType.Touch;

    private nint WindowHook(nint hwnd, int message, nint wParam, nint lParam, ref bool handled)
    {
        // A collapsed handle must not take keyboard focus away from the slide show.
        if (!_expanded && message is 0x21 or 0x24B) // WM_MOUSEACTIVATE / WM_POINTERACTIVATE
        {
            RememberExternalForeground(GetForegroundWindow());
            handled = true;
            return 3; // MA_NOACTIVATE
        }
        // WPF can activate a button while processing a real pointer down. By release the
        // foreground may already be this rail; WM_ACTIVATE still identifies its predecessor.
        if (!_expanded && message == 0x06 && (wParam.ToInt64() & 0xffff) != 0)
            RememberExternalForeground(lParam);
        return 0;
    }

    private void RememberExternalForeground(nint window)
    {
        if (window != 0 && window != _handle) _lastExternalForeground = window;
    }

    private void DisplayChanged(object? sender, EventArgs e) => Dispatcher.BeginInvoke(() =>
    {
        CancelLayoutSidePress();
        if (_moveOrigin is not null) Collapse();
        else Position();
    });
    private Forms.Screen Screen => Forms.Screen.AllScreens.FirstOrDefault(screen => screen.DeviceName == _placement.Display)
        ?? Forms.Screen.PrimaryScreen ?? Forms.Screen.AllScreens[0];

    private double Scale()
    {
        uint dpi = GetDpiForWindow(_handle);
        return dpi > 0 ? dpi / 96.0 : 1;
    }
    private double _densityPreference;
    public void SetDensity(double preference) { _densityPreference = preference; Position(); }
    private double AnchorHeight => _placement.Compact ? CompactHeight : RailHeight;
    private double Density(Forms.Screen screen, double dpi) =>
        DesktopDensity.Resolve(_densityPreference, screen.WorkingArea.Width / dpi, screen.WorkingArea.Height / dpi);
    private static double RailDensity(double density) => Math.Max(density, MinimumTouchTarget / RailButtonSize);

    private void Position()
    {
        if (_handle == 0 || _closing || _positioning) return;
        _positioning = true;
        try
        {
            // WPF handles WM_DPICHANGED; its DpiChanged event reapplies bounds on the new monitor.
            var screen = Screen; var area = screen.WorkingArea; double dpi = Scale();
            double density = Density(screen, dpi);
            var content = (Grid)Content;
            if (content.LayoutTransform is not ScaleTransform transform || transform.ScaleX != density)
            {
                content.LayoutTransform = new ScaleTransform(density, density);
                double target = MinimumTouchTarget / density;
                foreach (var button in new[] { LayoutButton, LayoutBack, LayoutLeft, LayoutRight, LayoutShape, LayoutMove,
                    ToolsTab, ShortcutsTab, CollapseButton, PowerButton, PauseButton })
                {
                    button.MinWidth = target;
                    button.MinHeight = target;
                }
                foreach (var button in new[] { StudyButton, RecordingButton, RecordingPause, RecordingStop, RecordingConfirmStop,
                    RecordingCancelStop, AutomaticSkipDay, SettingsFooter })
                    button.MinHeight = target;
                CancelRecordingPress();
                CancelLayoutSidePress();
                ArrangePptActions();
            }
            // Shrink the panel with the user's density, but keep the rail and
            // the controls used to open, close, or switch pages usable by touch.
            double railRatio = RailDensity(density) / density;
            if (Rail.LayoutTransform is not ScaleTransform railTransform || railTransform.ScaleX != railRatio)
                Rail.LayoutTransform = new ScaleTransform(railRatio, railRatio);
            if (MoveHint.LayoutTransform is not ScaleTransform hintTransform || hintTransform.ScaleX != railRatio)
                MoveHint.LayoutTransform = new ScaleTransform(railRatio, railRatio);
            if (DrawReceiptCard.LayoutTransform is not ScaleTransform receiptTransform || receiptTransform.ScaleX != railRatio)
                DrawReceiptCard.LayoutTransform = new ScaleTransform(railRatio, railRatio);
            double scale = dpi * density;
            int railPixels = (int)Math.Round((RailWidth - 12) * railRatio * scale);
            int gutterPixels = (int)Math.Round(6 * scale);
            double anchorHeight = AnchorHeight * railRatio * scale;
            double insideWidth = _expanded ? PanelWidth - RailWidth :
                _moveOrigin is not null ? MoveHintWidth * railRatio :
                DrawReceiptCard.Visibility == Visibility.Visible ? DrawReceiptWidth * railRatio : 0;
            // Round the fixed rail separately from the inside panel so a new card
            // cannot move the rail by a physical pixel when it changes the window width.
            int width = Math.Min(railPixels + 2 * gutterPixels + (int)Math.Round(insideWidth * scale), area.Width);
            int height = Math.Min((int)Math.Round(Math.Max(anchorHeight, _expanded ? PanelHeight * scale : 0)), area.Height);
            double anchor = area.Top + Math.Max(0, area.Height - anchorHeight) * _placement.RelativeY;
            int y = (int)Math.Clamp(anchor + anchorHeight - height, area.Top, area.Bottom - height);
            int x = _placement.LeftSide ? area.Left : area.Right - width;
            content.Margin = new Thickness(gutterPixels / scale, 6, gutterPixels / scale, 6);
            var rail = new GridLength(railPixels / scale);
            var panel = new GridLength(Math.Max(0, width - railPixels - 2 * gutterPixels) / scale);
            LeftColumn.Width = _placement.LeftSide ? rail : panel;
            RightColumn.Width = _placement.LeftSide ? panel : rail;
            Grid.SetColumn(Rail, _placement.LeftSide ? 0 : 1);
            Grid.SetColumn(PanelSurface, _placement.LeftSide ? 1 : 0);
            Grid.SetColumn(MoveHint, _placement.LeftSide ? 1 : 0);
            Grid.SetColumn(DrawReceiptCard, _placement.LeftSide ? 1 : 0);
            // The panel can reach the top of the working area without moving the handle.
            Rail.Margin = new Thickness(0, 0, 0, Math.Max(0, (y + height - anchor - anchorHeight) / scale));
            PanelSurface.Margin = _placement.LeftSide ? new Thickness(8, 0, 0, 0) : new Thickness(0, 0, 8, 0);
            MoveHint.Margin = PanelSurface.Margin;
            DrawReceiptCard.Margin = new Thickness(PanelSurface.Margin.Left, 0, PanelSurface.Margin.Right, 42 * railRatio);
            // A touch on a passive rail must leave the actual foreground application in place.
            // The full panel intentionally takes focus once explicitly opened.
            const int extendedStyle = -20, noActivate = 0x08000000;
            int style = GetWindowLong(_handle, extendedStyle);
            int desiredStyle = _expanded ? style & ~noActivate : style | noActivate;
            if (style != desiredStyle) SetWindowLong(_handle, extendedStyle, desiredStyle);
            SetWindowPos(_handle, new nint(-1), x, y, width, height, 0x10); // SWP_NOACTIVATE
            CollapseButton.Content = _placement.LeftSide ? "‹" : "›";
        }
        finally { _positioning = false; }
    }

    public void OpenPanel()
    {
        if (_closing || _autoHidden) return;
        if (_moveOrigin is not null) Collapse();
        DismissDrawReceipt();
        if (!_expanded)
        {
            RememberExternalForeground(GetForegroundWindow());
            _previous = _lastExternalForeground;
        }
        bool animate = !_expanded && SystemParameters.ClientAreaAnimation;
        RefreshTaskOrder(); _expanded = true; RefreshCompact();
        PanelSurface.Visibility = Visibility.Visible;
        RefreshRail();
        if (!IsVisible) Show();
        Position(); Activate();
        if (animate) PanelSurface.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(140)) { FillBehavior = FillBehavior.Stop });
    }

    public void Collapse(bool restoreFocus = true)
    {
        if (_closing) return;
        AbortHandleGesture();
        DismissDrawReceipt();
        InvalidatePptPress();
        CancelRecordingPress();
        DismissRecordingConfirmation(refocus: false);
        EndMove(save: false, restoreFocus);
        bool wasExpanded = _expanded;
        if (wasExpanded) ShortcutViewRevision++;
        _expanded = false; RefreshCompact(); RefreshTaskOrder(); RefreshRecordingControlSlot(); RefreshStudyNextSlot();
        CloseLayoutOptions();
        PanelSurface.BeginAnimation(OpacityProperty, null);
        PanelSurface.Visibility = Visibility.Collapsed;
        RefreshRail();
        Position();
        if (restoreFocus && wasExpanded && GetForegroundWindow() == _handle && _previous != 0 && IsWindow(_previous)) SetForegroundWindow(_previous);
        if (wasExpanded) _previous = 0;
    }

    public bool LeftSide => _placement.LeftSide;
    internal event Action<bool>? SideChanged;

    private Action? _onboarding;
    public void SetOnboardingAction(Action action, bool pending)
    {
        _onboarding = action;
        _onboardingPending = pending;
        OnboardingEntry.Visibility = pending ? Visibility.Visible : Visibility.Collapsed;
        RefreshHandleLabel();
    }
    private void OnboardingClicked(object sender, RoutedEventArgs e) { Collapse(false); _onboarding?.Invoke(); }

    public void SetSide(bool leftSide)
    {
        if (_closing) return;
        var current = _moveOrigin ?? _placement;
        if (current.LeftSide == leftSide) return;
        var next = current with { LeftSide = leftSide };
        if (!TrySavePlacement(next))
        {
            if (IsLayoutOpen) LayoutSaveError.Visibility = Visibility.Visible;
            SideChanged?.Invoke(current.LeftSide);
            return;
        }
        Collapse();
        _placement = next;
        HandleActivity(); RefreshHandleLabel(); Position();
        SideChanged?.Invoke(leftSide);
    }

    public void Shutdown() { Collapse(); _closing = true; _idleTimer.Stop(); Close(); }

    private void AbortHandleGesture()
    {
        _handlePressStart = null; _dragging = false; _handleTouch = null;
        _handleInputRevision++;
        EdgeHandle.ClearValue(BorderBrushProperty);
        EdgeHandle.ReleaseAllTouchCaptures(); EdgeHandle.ReleaseMouseCapture();
    }

    private void BeginHandlePress(Point point)
    {
        HandleActivity();
        _handleInputRevision++; // A second gesture supersedes an earlier queued click.
        _handlePressStart = point; _dragging = false; _tapCancelled = false;
        _gestureScale = Scale();
        _dragOffsetY = point.Y - PointToScreen(new Point(0, 0)).Y;
        EdgeHandle.SetResourceReference(BorderBrushProperty, "TeacherAccentBrush");
    }

    private void CancelHandlePress()
    {
        // Normal release clears the press before giving up capture.
        if (_handlePressStart is null) return;
        AbortHandleGesture();
        EndMove(save: false, restoreFocus: true);
        HandleActivity();
    }

    private void TrackHandlePress(Point point)
    {
        if (_handlePressStart is not { } start) return;
        if (_moveOrigin is null)
        {
            _tapCancelled |= !HandleContains(point);
            return;
        }
        if (!_dragging && (point - start).Length < 12 * _gestureScale) return;
        _dragging = true;
        var screen = Forms.Screen.FromPoint(new System.Drawing.Point((int)point.X, (int)point.Y));
        double dpi = Scale();
        double available = screen.WorkingArea.Height - AnchorHeight * dpi * RailDensity(Density(screen, dpi));
        double ratio = available <= 0 ? 0 : (point.Y - screen.WorkingArea.Top - _dragOffsetY) / available;
        _placement = _placement with { RelativeY = Math.Clamp(ratio, 0, 1), Display = screen.DeviceName };
        Position();
    }

    private void ReleaseHandle(Point point)
    {
        if (_handlePressStart is null) return;
        // Input coalescing can omit the final move.
        TrackHandlePress(point);
        if (_moveOrigin is not null && !_dragging) _tapCancelled = !HandleContains(point);
        FinishHandlePress();
    }

    private bool HandleContains(Point screenPoint) =>
        new Rect(EdgeHandle.RenderSize).Contains(EdgeHandle.PointFromScreen(screenPoint));

    private void FinishHandlePress()
    {
        if (_handlePressStart is null) return;
        bool dragged = _dragging;
        bool tap = !_tapCancelled;
        HandleActivity();
        // Release capture BEFORE resizing/activating the window. Otherwise WPF/Windows can
        // cancel the same touch during expansion and immediately deactivate/collapse it.
        AbortHandleGesture();
        if (_moveOrigin is not null)
        {
            if (dragged || tap) EndMove(save: dragged, restoreFocus: true);
        }
        else if (tap)
        {
            int revision = ++_handleInputRevision;
            Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
            { if (revision == _handleInputRevision && !_closing && !_autoHidden) ToggleHandle(); });
        }
    }

    private void Save() => TrySavePlacement(_placement);

    private bool TrySavePlacement(Placement placement)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_settingsPath)!);
            File.WriteAllText(_settingsPath + ".tmp", JsonSerializer.Serialize(placement));
            File.Move(_settingsPath + ".tmp", _settingsPath, true);
            return true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return false; }
    }

    public void Update(TouchAssistState? state, string status, bool available, string classIslandStatus, bool canStart, string startLabel = "启动")
    {
        UpdatePptStrip(state, status, available);
        ClassIslandStatus.Text = classIslandStatus; ClassIslandButton.IsEnabled = canStart;
        ClassIslandButton.Content = startLabel;
        RailStatusDot.SetResourceReference(System.Windows.Shapes.Shape.FillProperty,
            state?.Error is not null ? "TeacherErrorBrush" : state?.Running != true ? "TeacherMutedBrush" : state.Paused ? "TeacherWarningBrush" : "TeacherSuccessBrush");
        RailStatusDot.ToolTip = "PPT 辅助 · " + status;
    }

    private void HandleClicked(object sender, RoutedEventArgs e) => ToggleHandle();
    private void ToggleHandle()
    {
        if (_autoHidden) return;
        if (_moveOrigin is not null || _expanded) Collapse();
        else OpenPanel();
    }

    private void LayoutShapeClicked(object sender, RoutedEventArgs e) => SetCompact(!_placement.Compact);

    private void LayoutMoveClicked(object sender, RoutedEventArgs e)
    {
        if (!_expanded || _autoHidden) return;
        var returnFocus = _previous;
        Collapse(restoreFocus: false);
        _moveOrigin = _placement;
        _moveReturnFocus = returnFocus;
        MoveHint.Visibility = Visibility.Visible;
        HandleActivity();
        RefreshHandleLabel();
        Position();
        EdgeHandle.Focus();
    }

    private void EndMove(bool save, bool restoreFocus)
    {
        if (_moveOrigin is not { } origin) return;
        _moveOrigin = null;
        var returnFocus = _moveReturnFocus;
        _moveReturnFocus = 0;
        if (save) Save();
        else _placement = origin;
        MoveHint.Visibility = Visibility.Collapsed;
        HandleActivity();
        RefreshHandleLabel();
        Position();
        if (restoreFocus && GetForegroundWindow() == _handle && returnFocus != 0 && IsWindow(returnFocus))
            SetForegroundWindow(returnFocus);
    }

    public void SetCompact(bool compact)
    {
        Collapse();
        _placement = _placement with { Compact = compact };
        HandleActivity();
        RefreshCompact();
        Position();
        Save();
    }

    private void FoldClicked(object sender, RoutedEventArgs e) => SetCompact(!_placement.Compact);
    private void RefreshCompact()
    {
        bool compact = _placement.Compact;
        RailTools.Visibility = RailShortcuts.Visibility = RailSecRandom.Visibility = RailSettings.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
        RailFold.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
        RailSeparator.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
        LayoutShape.Content = compact ? "常驻完整栏" : "缩成把手";
        RefreshHandleLabel();
        RefreshHandleOpacity();
    }
    private void HandleActivity() { _handleActivity = Stopwatch.GetTimestamp(); Rail.Opacity = 1; }
    private void RefreshHandleOpacity()
    {
        Rail.Opacity = _placement.Compact && !_expanded && !_autoHidden && _moveOrigin is null && _handlePressStart is null &&
            !EdgeHandle.IsMouseOver && !EdgeHandle.IsKeyboardFocusWithin && Stopwatch.GetElapsedTime(_handleActivity).TotalSeconds >= 3 ? .6 : 1;
    }
    private void RefreshShortcutColumns()
    {
        if (System.Windows.Media.VisualTreeHelper.GetChildrenCount(ShortcutItems) == 0) return;
        void Visit(DependencyObject node)
        {
            if (node is System.Windows.Controls.Primitives.UniformGrid grid) { grid.Columns = ShortcutItems.ActualWidth < 290 ? 1 : 2; return; }
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++) Visit(VisualTreeHelper.GetChild(node, i));
        }
        Visit(ShortcutItems);
    }
    public void SetStudyAction(Action action) => _study = action;
    private void StudyClicked(object sender, RoutedEventArgs e) { Collapse(false); _study?.Invoke(); }
    public void UpdateStudy(string title, string status, string next, string action, bool active)
    {
        StudyTitle.Text = title; StudyStatus.Text = status; StudyNext.Text = next; StudyButton.Content = action;
        StudyTitle.ToolTip = title; StudyStatus.ToolTip = status; StudyNext.ToolTip = next;
        RefreshStudyNextSlot();
        if (_studyTask != active) { _studyTask = active; _taskOrderDirty = true; }
        RefreshTaskOrder();
    }
    private void RefreshStudyNextSlot()
    {
        bool hasNext = !string.IsNullOrWhiteSpace(StudyNext.Text);
        if (!_expanded) _studyNextSlot = hasNext;
        StudyNext.Visibility = !_studyNextSlot ? Visibility.Collapsed : hasNext ? Visibility.Visible : Visibility.Hidden;
    }
    private void RefreshTaskOrder()
    {
        // Do not move controls beneath a finger or keyboard focus in an open panel.
        if (_expanded || !_taskOrderDirty) return;
        bool recordingFirst = _recordingNeedsReview || _recordingTask && !_studyTask;
        var first = recordingFirst ? RecordingCard : StudyCard;
        if (TaskStack.Children[0] != first) { TaskStack.Children.Remove(first); TaskStack.Children.Insert(0, first); }
        _taskOrderDirty = false;
    }
    public void SetAutoHidden(bool hidden)
    {
        if (_closing || _autoHidden == hidden) return;
        _autoHidden = hidden;
        if (hidden) { AbortHandleGesture(); Collapse(false); Hide(); }
        else { HandleActivity(); ShowEntry(); }
    }
    public void ShowEntry() { if (!_autoHidden && !_closing) { if (!IsVisible) Show(); Position(); } }
    public void SetRecordingActions(Action open, Action<string, RecorderControl, string> command, Action plan, Action skipToday)
    { _openRecording = open; _recordingAction = command; _openRecordingPlan = plan; _skipAutomaticToday = skipToday; }
    public void UpdateAutomaticRecording(AutomaticRecordingState state, bool stateAvailable)
    {
        AutomaticStatus.Text = state.Message;
        AutomaticSkipDay.Visibility = state.Enabled && stateAvailable ? Visibility.Visible : Visibility.Collapsed;
        AutomaticSkipDay.IsEnabled = stateAvailable;
    }
    private void AutomaticSkipDayClicked(object sender, RoutedEventArgs e) { if (AutomaticSkipDay.IsEnabled && AutomaticSkipDay.Visibility == Visibility.Visible) _skipAutomaticToday?.Invoke(); }
    private void RecordingClicked(object sender, RoutedEventArgs e) { Collapse(false); _openRecording?.Invoke(); }
    private void RecordingPlanClicked(object sender, RoutedEventArgs e) { Collapse(false); _openRecordingPlan?.Invoke(); }
    private void RailToolsClicked(object sender, RoutedEventArgs e) { SelectShortcutPage(false); OpenPanel(); }
    private void RailShortcutsClicked(object sender, RoutedEventArgs e) { SelectShortcutPage(true); OpenPanel(); }
    private void PowerClicked(object sender, RoutedEventArgs e) => PptActionClicked(sender, e);
    private void PauseClicked(object sender, RoutedEventArgs e) => PptActionClicked(sender, e);
    private void ClassIslandClicked(object sender, RoutedEventArgs e) => _startClassIsland();
    private void SettingsClicked(object sender, RoutedEventArgs e) { Collapse(false); _settings(); }
    public void SetShortcuts(ShortcutEntry[] items)
    {
        ShortcutItems.ItemsSource = items;
        ShortcutsEmpty.Visibility = items.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, RefreshShortcutColumns);
    }

    public void SetShortcutStatus(string message, bool available, bool repair)
    {
        ShortcutStatus.Text = message;
        ShortcutStatus.ToolTip = message;
        bool hasMessage = !string.IsNullOrEmpty(message);
        ShortcutStatus.Visibility = hasMessage ? Visibility.Visible : Visibility.Hidden;
        ShortcutStatus.SetResourceReference(TextBlock.ForegroundProperty, repair ? "TeacherErrorBrush" : "TeacherMutedBrush");
        ShortcutItems.IsEnabled = ShortcutRepair.IsEnabled = available;
        ShortcutRepair.Visibility = repair ? Visibility.Visible : Visibility.Collapsed;
        Grid.SetColumn(ShortcutsFooter, hasMessage ? 2 : 0);
        Grid.SetColumnSpan(ShortcutsFooter, hasMessage ? 1 : 3);
        ShortcutsFooter.Content = hasMessage ? "管理" : "管理快捷启动  →";
    }

    internal void CompleteShortcutHandoff(long view)
    {
        if (_expanded && _shortcutsSelected && ShortcutViewRevision == view) Collapse();
    }

    private void ToolsTabClicked(object sender, RoutedEventArgs e) => SelectShortcutPage(false);
    private void ShortcutsTabClicked(object sender, RoutedEventArgs e) => SelectShortcutPage(true);
    private void SelectShortcutPage(bool shortcuts)
    {
        CloseLayoutOptions();
        if (_shortcutsSelected != shortcuts)
        {
            ShortcutViewRevision++;
            InvalidatePptPress();
            CancelRecordingPress();
            DismissRecordingConfirmation(refocus: false);
        }
        _shortcutsSelected = shortcuts;
        ToolsPage.Visibility = PptQuickStrip.Visibility = shortcuts ? Visibility.Collapsed : Visibility.Visible;
        ShortcutsPage.Visibility = ShortcutsPanelFooter.Visibility = shortcuts ? Visibility.Visible : Visibility.Collapsed;
        ToolsTab.Tag = shortcuts ? null : "active";
        ShortcutsTab.Tag = shortcuts ? "active" : null;
        RefreshRail();
    }
    private void RefreshRail()
    {
        string activeTag = _placement.LeftSide ? "active-right" : "active-left";
        RailTools.Tag = _expanded && !_shortcutsSelected ? activeTag : null;
        RailShortcuts.Tag = _expanded && _shortcutsSelected ? activeTag : null;
        RefreshHandleLabel();
    }

    private void RefreshHandleLabel()
    {
        string action = _moveOrigin is not null ? "拖动调整位置，轻点取消" : _expanded ? "收起快捷面板" : "展开快捷面板";
        HandleDirection.Text = _moveOrigin is not null ? "↕" : _placement.LeftSide == _expanded ? "‹" : "›";
        HandleDirection.HorizontalAlignment = _placement.LeftSide ? HorizontalAlignment.Right : HorizontalAlignment.Left;
        HandleDirection.Margin = _placement.LeftSide ? new Thickness(0, 0, 2, 0) : new Thickness(2, 0, 0, 0);
        string? status = _onboardingPending ? "初始设置待完成" : _handleStatus;
        EdgeHandle.ToolTip = status is null ? action : $"{action}\n{status}";
        System.Windows.Automation.AutomationProperties.SetName(EdgeHandle, action);
    }
    private void ShortcutClicked(object sender, RoutedEventArgs e)
    { if (sender is FrameworkElement { DataContext: ShortcutEntry entry }) _openShortcut(entry); }
    private void ManageShortcutsClicked(object sender, RoutedEventArgs e) { Collapse(false); _manageShortcuts(); }
    private void RepairShortcutClicked(object sender, RoutedEventArgs e) { Collapse(false); _repairShortcut(); }
    private void CollapseClicked(object sender, RoutedEventArgs e) => Collapse();

    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(nint hwnd);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")] private static extern int GetWindowLong(nint hwnd, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongW")] private static extern int SetWindowLong(nint hwnd, int index, int value);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(nint hwnd, nint after, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    private delegate void ForegroundCallback(nint hook, uint eventType, nint window, int objectId, int childId, uint thread, uint time);
    [DllImport("user32.dll")] private static extern nint SetWinEventHook(uint first, uint last, nint module,
        ForegroundCallback callback, uint processId, uint threadId, uint flags);
    [DllImport("user32.dll")] private static extern bool UnhookWinEvent(nint hook);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(nint hwnd);
    [DllImport("user32.dll")] private static extern bool IsWindow(nint hwnd);
}

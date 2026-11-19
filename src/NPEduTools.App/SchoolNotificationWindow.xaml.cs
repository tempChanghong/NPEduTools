using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.ComponentModel;
using System.Globalization;
using System.Windows.Automation;
using System.Windows.Controls;

namespace NPEduTools.App;

// Plain-text surface. Never fetches messages or grants notification capability.
public partial class SchoolNotificationWindow : Window
{
    public SchoolNotification Notification { get; }
    public SchoolNotificationStyle Appearance { get; }
    public Brush Surface { get; }
    public Brush ToneInner { get; }
    public Brush ToneMiddle { get; }
    public Brush ToneLight { get; }
    public Brush TonePale { get; }
    public bool RequiresResponse => Notification.Priority >= SchoolNotificationPriority.Normal;
    public string PriorityNumber => Notification.Priority switch
    {
        SchoolNotificationPriority.Minor => "01",
        SchoolNotificationPriority.Normal => "02",
        SchoolNotificationPriority.Important => "03",
        SchoolNotificationPriority.Urgent => "04",
        _ => throw new ArgumentOutOfRangeException()
    };
    public string HeaderLabel => Notification.IsPreview ? "样式预览 / " + Appearance.Label : Appearance.Label;
    public string TimeLabel => $"发布于 {Notification.PublishedAt.ToLocalTime():yyyy.MM.dd HH:mm}";
    public bool DismissedByUser { get; private set; }
    public Func<Task<bool>>? BeforeDismissAsync { get; set; }
    private bool _dismissBusy, _closingAllowed, _invalidated;
    public bool Invalidated => _invalidated;

    public SchoolNotificationWindow(SchoolNotification notification)
    {
        notification.Validate();
        Notification = notification;
        Appearance = SchoolNotificationStyle.For(notification.Priority);
        var color = (Color)ColorConverter.ConvertFromString(Appearance.Background);
        Surface = Tone(color, color, 0);
        // Deliberately separated lightness steps make the full-height bars distinct.
        ToneInner = Tone(color, Colors.White, .12);
        ToneMiddle = Tone(color, Colors.White, .28);
        ToneLight = Tone(color, Colors.White, .48);
        TonePale = Tone(color, Colors.White, .70);
        InitializeComponent();
        ShowActivated = RequiresResponse;
        DataContext = this;
        Title = $"{Appearance.Label} · {notification.Title} · NPEduTools";
        Loaded += (_, _) =>
        {
            FitWorkArea();
            InitializeAttention();
        };
        SizeChanged += (_, _) => AdaptLayout();
    }

    private static Brush Tone(Color color, Color target, double amount)
    {
        byte Mix(byte a, byte b) => (byte)Math.Round(a + (b - a) * amount);
        var brush = new SolidColorBrush(Color.FromRgb(Mix(color.R, target.R), Mix(color.G, target.G), Mix(color.B, target.B)));
        brush.Freeze(); return brush;
    }

    private void FitWorkArea() => SizeToMonitor(center: true);

    private void SizeToMonitor(bool center)
    {
        var screen = System.Windows.Forms.Screen.FromHandle(new System.Windows.Interop.WindowInteropHelper(this).Handle);
        var area = screen.WorkingArea;
        var dpi = VisualTreeHelper.GetDpi(this);
        double availableWidth = area.Width / dpi.DpiScaleX;
        double availableHeight = area.Height / dpi.DpiScaleY;
        Width = Math.Min(1120, availableWidth - Math.Min(32, availableWidth * .04));
        Height = Math.Min(720, availableHeight - Math.Min(32, availableHeight * .04));
        if (center)
            DesktopWindowBounds.PlaceCentered(this, new(area.Left, area.Top, area.Width, area.Height),
                Width * dpi.DpiScaleX, Height * dpi.DpiScaleY);
        else
            DesktopWindowBounds.KeepVisible(this);
        AdaptLayout();
    }

    private void AdaptLayout()
    {
        if (ContentGrid is null) return;
        bool compact = Width < 850 || Height < 560;
        bool stackFooter = Width < 490;
        SignalColumn.Width = new GridLength(compact ? 64 : 96);
        PriorityMark.Visibility = compact || Invalidated ? Visibility.Collapsed : Visibility.Visible;
        ContentGrid.Margin = new Thickness(compact ? 20 : 40);
        HeaderRule.Margin = compact ? new Thickness(0, 12, 0, 14) : new Thickness(0, 20, 0, 24);
        NotificationTitle.FontSize = compact ? 32 : 44;
        NotificationTitle.LineHeight = compact ? 44 : 60;
        NotificationBody.FontSize = compact ? 24 : 28;
        NotificationBody.LineHeight = compact ? 36 : 44;
        NotificationBody.Margin = new Thickness(0, compact ? 14 : 22, 0, 0);
        NotificationFooter.Margin = new Thickness(0, compact ? 12 : 24, 0, 0);
        FooterRule.Margin = new Thickness(0, 0, 0, compact ? 12 : 20);
        NotificationFooterMetadata.Margin = stackFooter ? new Thickness(0, 0, 0, 8) : new Thickness(0, 0, 20, 0);
        Grid.SetColumnSpan(NotificationFooterMetadata, stackFooter ? 2 : 1);
        Grid.SetRow(DismissButton, stackFooter ? 3 : 2);
        UpdateNotificationMetadata();
    }

    private void NotificationFooterSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (e.WidthChanged) UpdateNotificationMetadata();
    }

    private void UpdateNotificationMetadata()
    {
        if (Invalidated) return;
        double width = NotificationFooterMetadata.ActualWidth;
        string metadata = $"{Notification.Source}  ·  {TimeLabel}";
        var dpi = VisualTreeHelper.GetDpi(NotificationSource);
        var font = new Typeface(NotificationSource.FontFamily, NotificationSource.FontStyle,
            NotificationSource.FontWeight, NotificationSource.FontStretch);
        FormattedText Format(string text) => new(text, CultureInfo.CurrentUICulture,
            NotificationSource.FlowDirection, font, NotificationSource.FontSize, Foreground, dpi.PixelsPerDip);
        var measured = Format(metadata);
        measured.MaxTextWidth = Math.Max(1, width);
        bool sourceInBody = measured.Height > Format("通知").Height * 2 + 1;
        NotificationSource.Text = sourceInBody ? TimeLabel : metadata;
        FullNotificationSource.Text = sourceInBody ? $"来源　{Notification.Source}" : "";
        FullNotificationSource.Visibility = sourceInBody ? Visibility.Visible : Visibility.Collapsed;
    }

    private void NotificationVisualLoaded(object sender, RoutedEventArgs e)
    {
        ApplyNotificationVisualTheme(SystemParameters.HighContrast);
        SystemParameters.StaticPropertyChanged += NotificationVisualSystemSettingsChanged;
    }

    private void NotificationVisualUnloaded(object sender, RoutedEventArgs e) =>
        SystemParameters.StaticPropertyChanged -= NotificationVisualSystemSettingsChanged;

    private void NotificationVisualSystemSettingsChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SystemParameters.HighContrast))
            Dispatcher.InvokeAsync(() => ApplyNotificationVisualTheme(SystemParameters.HighContrast));
    }

    private void ApplyNotificationVisualTheme(bool highContrast)
    {
        SetCurrentValue(BackgroundProperty, highContrast ? SystemColors.WindowBrush : Surface);
        Resources["NotificationInk"] = highContrast ? SystemColors.WindowTextBrush : Brushes.White;
        Resources["NotificationActionSurface"] = highContrast ? SystemColors.ControlBrush : Brushes.White;
        Resources["NotificationActionInk"] = highContrast ? SystemColors.ControlTextBrush : Surface;
        Resources["NotificationActionBorder"] = highContrast ? SystemColors.WindowTextBrush : Brushes.White;
        Resources["NotificationPressedSurface"] = highContrast ? SystemColors.HighlightBrush : Surface;
        Resources["NotificationPressedInk"] = highContrast ? SystemColors.HighlightTextBrush : Brushes.White;
        Resources["NotificationDisabledInk"] = highContrast ? SystemColors.GrayTextBrush : Brushes.DimGray;
        Resources["NotificationTonePale"] = highContrast ? SystemColors.WindowBrush : TonePale;
        Resources["NotificationToneLight"] = highContrast ? SystemColors.ControlBrush : ToneLight;
        Resources["NotificationToneMiddle"] = highContrast ? SystemColors.WindowBrush : ToneMiddle;
        Resources["NotificationToneInner"] = highContrast ? SystemColors.HighlightBrush : ToneInner;
    }

    private void HeaderDrag(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left || e.ButtonState != MouseButtonState.Pressed) return;
        _headerDragging = true;
        try { DragMove(); }
        finally
        {
            _headerDragging = false;
            if (!_attentionReleased && IsVisible) FitAfterMove();
        }
    }

    public void InvalidateNotice(string message)
    {
        _invalidated = true;
        NotificationTitle.Text = "此通知已失效";
        NotificationBody.Text = message;
        NotificationSource.Text = "NPEduTools · 通知状态";
        NotificationHeader.Text = "通知状态";
        FullNotificationSource.Text = "";
        FullNotificationSource.Visibility = Visibility.Collapsed;
        PriorityMark.Visibility = Visibility.Collapsed;
        NotificationCloseStatus.Visibility = Visibility.Collapsed;
        SetDismissCaption("关闭窗口");
        MessageScroll.ScrollToTop();
        Title = "通知已失效 · NPEduTools";
    }

    private void SetDismissCaption(string caption)
    {
        DismissButton.Content = caption;
        AutomationProperties.SetName(DismissButton, caption);
    }

    private async void DismissClicked(object sender, RoutedEventArgs e) => await DismissAsync();
    private async Task DismissAsync()
    {
        if (_dismissBusy) return;
        bool hadFocus = DismissButton.IsKeyboardFocused;
        _dismissBusy = true; DismissButton.IsEnabled = false;
        try
        {
            if (!_invalidated && BeforeDismissAsync is not null && !await BeforeDismissAsync())
            {
                if (!_invalidated)
                {
                    NotificationCloseStatus.Text = "暂时无法保存关闭回执。请重试关闭。";
                    NotificationCloseStatus.Visibility = Visibility.Visible;
                    SetDismissCaption("重试关闭");
                }
                return;
            }
            DismissedByUser = !_invalidated;
            await AnimateDismissAsync();
            _closingAllowed = true; if (IsVisible) Close();
        }
        finally
        {
            _dismissBusy = false;
            DismissButton.IsEnabled = true;
            if (hadFocus && IsVisible && IsActive) DismissButton.Focus();
        }
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!_closingAllowed && BeforeDismissAsync is not null)
        { e.Cancel = true; Dispatcher.BeginInvoke(() => _ = DismissAsync()); }
        base.OnClosing(e);
        if (!e.Cancel) ReturnToPreviousWindow();
    }
    protected override void OnClosed(EventArgs e)
    {
        ReleaseAttention();
        base.OnClosed(e);
    }
    public void Shutdown() { _closingAllowed = true; ReleaseAttention(); Close(); }
}

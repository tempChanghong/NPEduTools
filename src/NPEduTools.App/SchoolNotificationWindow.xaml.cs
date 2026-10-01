using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.ComponentModel;

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
    public string TimeLabel => Notification.PublishedAt.ToLocalTime().ToString("MM.dd  HH:mm") + "  ·  请手动关闭";
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

    private void FitWorkArea()
    {
        var screen = System.Windows.Forms.Screen.FromHandle(new System.Windows.Interop.WindowInteropHelper(this).Handle);
        var area = screen.WorkingArea;
        var dpi = VisualTreeHelper.GetDpi(this);
        double availableWidth = area.Width / dpi.DpiScaleX;
        double availableHeight = area.Height / dpi.DpiScaleY;
        Width = Math.Min(1120, availableWidth - Math.Min(32, availableWidth * .04));
        Height = Math.Min(720, availableHeight - Math.Min(32, availableHeight * .04));
        Left = area.Left / dpi.DpiScaleX + (availableWidth - Width) / 2;
        Top = area.Top / dpi.DpiScaleY + (availableHeight - Height) / 2;
        AdaptLayout();
    }

    private void AdaptLayout()
    {
        if (ContentGrid is null) return;
        bool compact = Width < 850 || Height < 560;
        SignalColumn.Width = new GridLength(compact ? 64 : 96);
        PriorityMark.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
        ContentGrid.Margin = new Thickness(compact ? 20 : 40);
        NotificationTitle.FontSize = compact ? 32 : 44;
        NotificationTitle.LineHeight = compact ? 44 : 60;
        NotificationBody.FontSize = compact ? 24 : 28;
        NotificationBody.LineHeight = compact ? 36 : 44;
    }

    private void HeaderDrag(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left && e.ButtonState == MouseButtonState.Pressed) DragMove();
    }

    public void InvalidateNotice(string message)
    {
        _invalidated = true;
        NotificationTitle.Text = "此通知已失效";
        NotificationBody.Text = message;
        NotificationSource.Text = "NPEduTools · 通知状态";
        Title = "通知已失效 · NPEduTools";
    }

    private async void DismissClicked(object sender, RoutedEventArgs e) => await DismissAsync();
    private async Task DismissAsync()
    {
        if (_dismissBusy) return;
        _dismissBusy = true; DismissButton.IsEnabled = false;
        try
        {
            if (!_invalidated && BeforeDismissAsync is not null && !await BeforeDismissAsync())
            { DismissButton.Content = "保存失败 · 重试关闭"; return; }
            DismissedByUser = !_invalidated;
            await AnimateDismissAsync();
            _closingAllowed = true; if (IsVisible) Close();
        }
        finally { _dismissBusy = false; DismissButton.IsEnabled = true; }
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!_closingAllowed && BeforeDismissAsync is not null)
        { e.Cancel = true; Dispatcher.BeginInvoke(() => _ = DismissAsync()); }
        base.OnClosing(e);
    }
    protected override void OnClosed(EventArgs e)
    {
        ReleaseAttention();
        base.OnClosed(e);
    }
    public void Shutdown() { _closingAllowed = true; ReleaseAttention(); Close(); }
}

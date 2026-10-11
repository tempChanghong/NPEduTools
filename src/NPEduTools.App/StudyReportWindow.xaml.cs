using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using NPEduTools.Contracts;

namespace NPEduTools.App;

public partial class StudyReportWindow : Window
{
    private Guid? _instance, _dismissed;
    private StudyReportData? _data;
    private long _received;
    private double _remaining;
    private bool _shutdown;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    public StudyReportWindow()
    {
        InitializeComponent();
        Closing += (_, e) => { if (_shutdown) return; e.Cancel = true; _dismissed = _instance; HideDisplay(); };
        _timer.Tick += (_, _) => Countdown();
    }
    public void Apply(StudyReportState state, TimeSpan roundTrip)
    {
        _instance = state.InstanceId;
        _received = Stopwatch.GetTimestamp();
        _remaining = Math.Max(0, state.RemainingSeconds - Math.Max(0, roundTrip.TotalSeconds));
        if (state.Data is not { } d) { HideDisplay(); return; }
        Completion.Text = d.Complete ? "统计已收尾 · 本机报告" : "统计收尾中 · 暂存数据";
        if (_data is null || _data.Window != d.Window || _data.Frames != d.Frames || _data.SampledSeconds != d.SampledSeconds ||
            _data.DisciplineReminders != d.DisciplineReminders || _data.Complete != d.Complete ||
            _data.Estimated?.SampledSeconds != d.Estimated?.SampledSeconds || !_data.Notes.SequenceEqual(d.Notes))
        {
            _data = d;
            SessionTime.Text = $"{d.Window.Start:yyyy年M月d日}   {d.Window.Start:HH:mm} – {d.Window.End:HH:mm}";
            Coverage.Text = $"{d.Coverage * 100:0.0}%";
            Sampled.Text = Duration(d.SampledSeconds);
            ReminderCount.Text = d.DisciplineReminders.ToString(CultureInfo.InvariantCulture);
            RawValues.Text = Values(d.Raw);
            RawThresholds.Text = Thresholds(d.Raw);
            RawQuality.Text = $"削波时间占比 {d.ClippedPercent:0.00}% · {d.Frames:N0} 帧统计";
            EstimatedValues.Text = d.Estimated is { } a ? Values(a) : "— / —";
            EstimatedThresholds.Text = d.Estimated is { } estimated ? Thresholds(estimated) : "没有有效外部参考数据";
            EstimatedQuality.Text = d.Estimated is { } ea ? $"有效估算覆盖 {ea.SampledSeconds / d.ExpectedSeconds * 100:0.0}% · 仅包含参考有效时段" : "未将 dBFS 当作实际声压级。";
            Notes.Text = string.Join("\n", d.Notes);
            Draw(d);
        }
        Countdown();
        if (state.Available && _remaining > 0 && _dismissed != _instance) { if (!IsVisible) Show(); _timer.Start(); }
        else HideDisplay();
    }
    private static string Number(double? value) => value is { } v && double.IsFinite(v) ? v.ToString("0.0", CultureInfo.InvariantCulture) : "—";
    private static string Values(StudyReportChannel c) => $"{Number(c.EnergyMean)} / {Number(c.Maximum)}";
    private static string Duration(double seconds) => seconds >= 60 ? $"{(int)(seconds / 60)} 分 {seconds % 60:0} 秒" : $"{seconds:0.0} 秒";
    private static string Thresholds(StudyReportChannel c) => c.ThresholdSampledSeconds > 0
        ? $"高线以上  {Duration(c.HighSeconds)}\n{(c.MediumThresholdSampledSeconds > 0 ? $"中线以上  {Duration(c.MediumSeconds)}" : "中线没有有效判定数据")}\n阈值有效采样  {Duration(c.ThresholdSampledSeconds)}"
        : "本单位没有有效阈值判定数据";
    private void Countdown()
    {
        double seconds = Math.Max(0, _remaining - Stopwatch.GetElapsedTime(_received).TotalSeconds);
        Remaining.Text = $"{(int)Math.Ceiling(seconds) / 60:00}:{(int)Math.Ceiling(seconds) % 60:00}";
        if (seconds <= 0) HideDisplay();
    }
    private void Draw(StudyReportData d)
    {
        Chart.Children.Clear();
        var channel = d.Estimated is { SampledSeconds: > 0 } a ? a : d.Raw;
        bool estimated = channel.Unit == "estimated-dB(A)";
        OverviewTitle.Text = estimated ? "噪音概况 · 估算 dB(A)" : "噪音概况 · dBFS";
        PrimaryMean.Text = Number(channel.EnergyMean); PrimaryMaximum.Text = Number(channel.Maximum);
        MaximumCaption.Text = estimated ? "采样最大值" : "数字信号峰值";
        TrendTitle.Text = estimated ? "噪音趋势 · 估算 dB(A)" : "噪音趋势 · dBFS";
        ChartStart.Text = d.Window.Start.ToString("HH:mm"); ChartEnd.Text = d.Window.End.ToString("HH:mm");
        const double left = 56, top = 10;
        double width = Chart.Width - left - 4, height = Chart.Height - 24;
        double floor = estimated ? 30 : -160, ceiling = estimated ? 130 : 0;
        var levels = channel.Trend.Select(p => p.Mean).OfType<double>().Where(double.IsFinite).ToArray();
        double min = levels.Length == 0 ? floor : Math.Max(floor, Math.Floor((levels.Min() - 3) / 5) * 5);
        double max = levels.Length == 0 ? ceiling : Math.Min(ceiling, Math.Ceiling((levels.Max() + 3) / 5) * 5);
        if (max - min < 20) { min = Math.Max(floor, max - 20); max = Math.Min(ceiling, min + 20); }
        for (int i = 0; i <= 4; i++)
        {
            double y = top + height * i / 4;
            Chart.Children.Add(new Line { X1 = left, X2 = left + width, Y1 = y, Y2 = y, Stroke = new SolidColorBrush(Color.FromRgb(32, 48, 58)), StrokeThickness = 1 });
            var label = new TextBlock { Text = (max - (max - min) * i / 4).ToString("0"), Foreground = new SolidColorBrush(Color.FromRgb(160, 172, 181)), FontSize = 16 };
            Canvas.SetLeft(label, 0); Canvas.SetTop(label, y - 11); Chart.Children.Add(label);
        }
        var brush = new SolidColorBrush(Color.FromRgb(120, 198, 214)); brush.Freeze();
        if (levels.Length == 0)
        {
            var empty = new TextBlock { Text = "没有有效趋势数据", Foreground = Brushes.LightGray, FontSize = 28, Width = width, TextAlignment = TextAlignment.Center };
            Canvas.SetLeft(empty, left); Canvas.SetTop(empty, top + height / 2 - 20); Chart.Children.Add(empty);
        }
        // Aggregate minute segments; empty minutes remain empty. Width is not a timestamp of each missing frame.
        foreach (var point in channel.Trend)
        {
            if (point.Mean is not { } mean) continue;
            double x1 = left + width * Math.Clamp(point.Seconds / d.ExpectedSeconds, 0, 1);
            double x2 = left + width * Math.Clamp((point.Seconds + 60) / d.ExpectedSeconds, 0, 1);
            double y = top + height * (1 - Math.Clamp((mean - min) / (max - min), 0, 1));
            Chart.Children.Add(new Line { X1 = x1, X2 = Math.Max(x1 + 2, x2), Y1 = y, Y2 = y, Stroke = brush, StrokeThickness = 4 });
        }
    }
    private void CloseReportClicked(object sender, RoutedEventArgs e) { _dismissed = _instance; HideDisplay(); }
    public void HideDisplay() { _timer.Stop(); Hide(); }
    public void Shutdown() { _shutdown = true; _timer.Stop(); Close(); }
}

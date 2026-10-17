using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using NPEduTools.App;
using NPEduTools.Contracts;

internal static partial class Program
{
    private static void SnapshotReportViewport(Window window, Size size, string file)
    {
        // Native window bounds are constrained by the runner's virtual desktop.
        // Lay out the actual report tree at an exact viewport independent of it.
        var content = (FrameworkElement)window.Content;
        double width = content.Width, height = content.Height;
        var host = new Grid { Width = size.Width, Height = size.Height, Background = window.Background };
        host.SetValue(TextElement.ForegroundProperty, window.Foreground);
        host.SetValue(TextElement.FontFamilyProperty, window.FontFamily);
        host.SetValue(TextElement.FontSizeProperty, window.FontSize);
        host.SetValue(TextElement.FontWeightProperty, window.FontWeight);
        host.SetValue(TextElement.FontStyleProperty, window.FontStyle);
        host.FlowDirection = window.FlowDirection;
        host.Language = window.Language;
        window.Content = null;
        window.UpdateLayout();
        try
        {
            Assert(VisualTreeHelper.GetParent(content) is null, "report content still attached to native window");
            content.Width = size.Width; content.Height = size.Height;
            host.Children.Add(content);
            host.Measure(size);
            host.Arrange(new Rect(new Point(), size));
            host.UpdateLayout();
            Assert(Math.Abs(content.ActualWidth - size.Width) < .01 &&
                Math.Abs(content.ActualHeight - size.Height) < .01,
                $"report content viewport mismatch: {content.ActualWidth}x{content.ActualHeight}, requested {size.Width}x{size.Height}");
            var bitmap = new RenderTargetBitmap((int)size.Width, (int)size.Height, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(host);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var stream = File.Create(Path.Combine(_output, file));
            encoder.Save(stream);
        }
        finally { host.Children.Clear(); content.Width = width; content.Height = height; window.Content = content; window.UpdateLayout(); }
    }

    private static IEnumerable<DependencyObject> ReportVisuals(DependencyObject root)
    {
        yield return root;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            foreach (var child in ReportVisuals(VisualTreeHelper.GetChild(root, i))) yield return child;
    }
    private static void RunStudyReportChecks()
    {
        var window = new StudyReportWindow { WindowState = WindowState.Normal, Width = 1920, Height = 1080,
            WindowStartupLocation = WindowStartupLocation.Manual, Left = 5000, Top = 5000, Topmost = false, ShowActivated = false };
        var range = new SchoolNoiseWindow(new(2026, 10, 12, 19, 0, 0, TimeSpan.Zero), new(2026, 10, 12, 21, 30, 0, TimeSpan.Zero));
        var raw = new StudyReportChannel("dBFS", 8640, -35, -18, 8640, 390, 60,
            Enumerable.Range(0, 150).Where(i => i is < 70 or > 76).Select(i => new StudyReportPoint(i * 60, 60, -43 + Math.Sin(i / 8d) * 10, -18)).ToArray(), 8640);
        var estimated = new StudyReportChannel("estimated-dB(A)", 7800, 57, 78, 7800, 390, 60,
            Enumerable.Range(0, 130).Select(i => new StudyReportPoint(i * 60, 60, 54 + Math.Sin(i / 8d) * 10, 78)).ToArray(), 7800);
        var notes = new[] { "参考已失效或不可用；估算数据仅包含有效参考期间。", "超限时长按采样时生效的阈值统计；中线以上包含高线以上。未判定时段不记作未超限。" };
        var data = new StudyReportData(range, 9000, 8640, .96, .03, 86400, 4, raw, estimated, true, notes);
        var state = new StudyReportState(Guid.NewGuid(), "fixture-version", true, "Report", 300, data);
        try
        {
            window.Apply(state, TimeSpan.Zero); window.UpdateLayout();
            Assert(window.IsVisible, "fresh report did not show");
            Assert(((TextBlock)window.FindName("Coverage")).Text == "96.0%", "coverage missing");
            Assert(((TextBlock)window.FindName("ReminderCount")).Text == "4", "discipline count missing");
            Assert(((TextBlock)window.FindName("RawValues")).Text == "-35.0 / -18.0", "raw mean/peak changed");
            Assert(((TextBlock)window.FindName("EstimatedValues")).Text == "57.0 / 78.0", "estimated mean/maximum changed");
            Assert(((TextBlock)window.FindName("TrendTitle")).Text.Contains("估算 dB(A)"), "trend unit missing");
            Assert(((TextBlock)window.FindName("OverviewTitle")).Text.Contains("估算 dB(A)") &&
                ((TextBlock)window.FindName("PrimaryMean")).Text == "57.0" &&
                ((TextBlock)window.FindName("PrimaryMaximum")).Text == "78.0" &&
                ((TextBlock)window.FindName("MaximumCaption")).Text == "采样最大值", "primary statistics lose their acoustic estimate identity");
            var visuals = ReportVisuals(window).ToArray();
            var panel = (Border)window.FindName("StatisticsPanel");
            var statistics = (StackPanel)window.FindName("StatisticsContent");
            SnapshotReportViewport(window, new Size(1920, 1080), "study-report-detailed.png");
            Assert(statistics.DesiredSize.Height <= panel.Height - 2, $"statistics overflow right panel: desired {statistics.DesiredSize.Height:0.0}, actual {statistics.ActualHeight:0.0}, available {panel.Height - 2:0.0}");
            var glowing = visuals.OfType<UIElement>().Where(v => v.Effect is not null).ToArray();
            Assert(glowing.Length == 2 && glowing.All(v => v == window.FindName("PrimaryMean") || v == window.FindName("PrimaryMaximum")), "report blurs surfaces outside the two clock-style numbers");
            Assert(!visuals.OfType<TextBlock>().Any(b => b.Text.Contains("摄像头画面") || b.Text.Contains("验证失败")), "private camera/verification data leaked into classroom report");
            SnapshotReportViewport(window, new Size(1280, 720), "study-report-1280.png");
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
            var chart = (Canvas)window.FindName("Chart"); var first = chart.Children[^1];
            for (int i = 0; i < 200; i++) window.Apply(state with { RemainingSeconds = 299 }, TimeSpan.Zero);
            Assert(ReferenceEquals(first, chart.Children[^1]), "unchanged report rebuilds chart every poll");
            var corrected = data with { Mode = "Manual", ClockSource = "Windows", Raw = raw with { EnergyMean = -30, HighSeconds = 90 },
                Estimated = estimated with { EnergyMean = 60, Maximum = 80, Trend = [new(0, 60, 60, 80)] } };
            window.Apply(state with { Data = corrected }, TimeSpan.Zero);
            Assert(((TextBlock)window.FindName("PrimaryMean")).Text == "60.0" &&
                ((TextBlock)window.FindName("PrimaryMaximum")).Text == "80.0" &&
                ((TextBlock)window.FindName("RawValues")).Text.StartsWith("-30.0") &&
                ((TextBlock)window.FindName("RawThresholds")).Text.Contains("1 分 30 秒") &&
                ((TextBlock)window.FindName("SessionTime")).Text.Contains("手动自习"), "report correction with unchanged frame/sample counters did not refresh actual content");
            Assert(!ReferenceEquals(first, chart.Children[^1]), "changed trend retained the old graph");
            var correctedChart = chart.Children[^1];
            var equivalent = corrected with { Raw = corrected.Raw with { Trend = corrected.Raw.Trend.ToArray() },
                Estimated = corrected.Estimated! with { Trend = corrected.Estimated!.Trend.ToArray() }, Notes = corrected.Notes.ToArray() };
            window.Apply(state with { Data = equivalent }, TimeSpan.Zero);
            Assert(ReferenceEquals(correctedChart, chart.Children[^1]), "equivalent deserialized report rebuilt its chart");
            window.Apply(state, TimeSpan.Zero);
            Checks.Add("Actual report correction: mean/peak, trend, thresholds and session metadata refresh despite identical frame/sample counts; equivalent new collections do not redraw");
            var fractional = data with { SampledSeconds = 239.8, Coverage = 239.8 / data.ExpectedSeconds,
                Raw = raw with { SampledSeconds = 239.8, ThresholdSampledSeconds = 239.8, MediumThresholdSampledSeconds = 239.8,
                    MediumSeconds = 179.8, HighSeconds = 119.8, Trend = [] },
                Estimated = estimated with { SampledSeconds = 239.8, ThresholdSampledSeconds = 239.8, MediumThresholdSampledSeconds = 239.8,
                    MediumSeconds = 179.8, HighSeconds = 119.8, Trend = [] } };
            window.Apply(state with { Data = fractional }, TimeSpan.Zero);
            Assert(((TextBlock)window.FindName("Sampled")).Text == "4 分 0 秒", "sampled time rounded its seconds to 60 without carrying the minute");
            foreach (string field in new[] { "RawThresholds", "EstimatedThresholds" })
            {
                string text = ((TextBlock)window.FindName(field)).Text;
                Assert(text.Contains("高线以上  2 分 0 秒") && text.Contains("中线以上  3 分 0 秒") && text.Contains("阈值有效采样  4 分 0 秒"),
                    "threshold durations rounded seconds independently of their minute component");
            }
            window.Apply(state, TimeSpan.Zero);
            Checks.Add("Report duration carry: fractional sampled time and both raw/estimated threshold durations render whole-minute boundaries without a 60-second component; report data is unchanged");
            // UI monotonic deadline also closes if the pipe disconnects just before expiry.
            typeof(StudyReportWindow).GetField("_received", flags)!.SetValue(window, Stopwatch.GetTimestamp() - 301L * Stopwatch.Frequency);
            typeof(StudyReportWindow).GetMethod("Countdown", flags)!.Invoke(window, null);
            Assert(!window.IsVisible, "expired report did not close locally");
            state = state with { InstanceId = Guid.NewGuid(), Data = data with { Estimated = null } };
            window.Apply(state, TimeSpan.Zero); Assert(((TextBlock)window.FindName("TrendTitle")).Text.EndsWith("dBFS"), "raw fallback is mislabeled acoustic");
            Assert(((TextBlock)window.FindName("OverviewTitle")).Text.EndsWith("dBFS") &&
                ((TextBlock)window.FindName("PrimaryMean")).Text == "-35.0" &&
                ((TextBlock)window.FindName("MaximumCaption")).Text == "数字信号峰值", "primary raw fallback is mislabeled acoustic");
            window.UpdateLayout(); SnapshotReportViewport(window, new Size(1920, 1080), "study-report-dbfs.png");
            var close = ReportVisuals(window).OfType<Button>().Single(b => Equals(b.Content, "关闭报告")); close.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
            Assert(!window.IsVisible, "manual close failed"); window.Apply(state, TimeSpan.Zero); Assert(!window.IsVisible, "polling reopened dismissed report");
            var nextManual = state with { Version = "next-manual-session", Data = state.Data! with { Mode = "Manual", ClockSource = "Windows" } };
            window.Apply(nextManual, TimeSpan.Zero);
            Assert(window.IsVisible, "closing a report suppressed another manual session from the same Host instance");
            close.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
            window.Apply(nextManual, TimeSpan.Zero);
            Assert(!window.IsVisible, "fresh polling reopened the dismissed next session");
            Checks.Add("Report dismissal is session-specific: the same Host instance can present a new manual version, while polling never reopens that session after closing");
            state = state with { InstanceId = Guid.NewGuid(), Data = data with { SampledSeconds = 0, Coverage = 0,
                Frames = 0, ClippedPercent = 0, DisciplineReminders = 0, Estimated = null,
                Notes = ["未取得有效采样；没有数据不代表安静。", "采集覆盖不足；空白时段未记作安静。"],
                Raw = raw with { SampledSeconds = 0, EnergyMean = null, Maximum = null,
                    ThresholdSampledSeconds = 0, MediumThresholdSampledSeconds = 0, MediumSeconds = 0, HighSeconds = 0, Trend = [] } } };
            window.Apply(state, TimeSpan.Zero); Assert(((TextBlock)window.FindName("RawValues")).Text == "— / —", "no data displayed fake zero");
            Assert(((TextBlock)window.FindName("PrimaryMean")).Text == "—", "primary no-data value displayed fake zero");
            SnapshotReportViewport(window, new Size(1920, 1080), "study-report-no-data.png");
            window.Apply(state with { Available = false }, TimeSpan.Zero); Assert(!window.IsVisible, "withdrawn report remains visible");
            Checks.Add("Detailed report: clock-style overview, independent raw/estimated statistics, complete-period trend, unit labels and primary-channel fallback, no private data, panel fit and exact offscreen 1920x1080/1280x720 content viewports, bounded redraw, local monotonic expiry, close and no-data behavior; no sensors/audio/Host startup");
        }
        finally { window.Shutdown(); }
    }
}

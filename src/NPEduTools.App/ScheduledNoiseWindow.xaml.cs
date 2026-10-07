using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Shapes;
using NPEduTools.Contracts;
using NPEduTools.Core;

namespace NPEduTools.App;

public partial class ScheduledNoiseWindow : Window
{
    private readonly string _pipe;
    private readonly Action _manage;
    private NoiseDisplayState? _display;
    private NoiseState? _noise;
    private bool _closing, _returning;
    public ScheduledNoiseWindow(string pipe, Action manage)
    {
        _pipe = pipe; _manage = manage; InitializeComponent();
        // WPF logical pixels; keep taskbar and accessibility controls reachable.
        var area = SystemParameters.WorkArea;
        Left = area.Left; Top = area.Top; Width = area.Width; Height = area.Height;
        Closing += (_, args) => { if (!_closing) { args.Cancel = true; _ = ReturnAsync(); } };
    }
    public void Shutdown() { _closing = true; Close(); }
    public void Apply(NoiseDisplayState display, NoiseState noise)
    {
        _display = display; _noise = noise;
        WindowText.Text = display.Window is { } w ? $"学校时间 {w.Start:HH:mm} — {w.End:HH:mm} · {noise.DeviceName}" : "";
        LevelText.Text = NoiseSignalPresentation.Level(noise.CurrentDbfs);
        QualityText.Text = NoiseSignalPresentation.Quality(noise.Quality, noise.CurrentDbfs);
        MessageText.Text = display.Message;
        ReturnButton.Content = $"返回作业板（{display.ReturnMinutes} 分钟）";
        ReturnButton.IsEnabled = !_returning;
        DrawTrend();
    }
    private async Task ReturnAsync()
    {
        if (_closing || _returning || _display is not { InstanceId: { } instance, SessionId: { } session }) return;
        bool StillCurrent() => !_closing && _display?.InstanceId == instance && _display.SessionId == session;
        _returning = true; ReturnButton.IsEnabled = false;
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var response = await HostClient.RequestAsync(_pipe, new HostRequest(1, Guid.NewGuid(), "noise.display.return",
                NoiseDisplay: new("return", instance, session)), timeout.Token);
            if (!StillCurrent()) return;
            if (response.Outcome == "Succeeded") Hide();
            else MessageText.Text = response.Message;
        }
        catch (Exception e) when (e is IOException or TimeoutException or OperationCanceledException or JsonException or UnauthorizedAccessException)
        { if (StillCurrent()) MessageText.Text = "后台暂未确认返回期限，请等待连接恢复。没有停止监测。"; }
        finally { _returning = false; if (!_closing) ReturnButton.IsEnabled = true; }
    }
    private async void ReturnClicked(object sender, RoutedEventArgs e) => await ReturnAsync();
    private void ManageClicked(object sender, RoutedEventArgs e) { Hide(); _manage(); }
    private void TrendSizeChanged(object sender, SizeChangedEventArgs e) => DrawTrend();
    private void DrawTrend()
    {
        TrendCanvas.Children.Clear();
        if (_noise?.Summary is not { } summary || TrendCanvas.ActualWidth <= 0) return;
        double end = Math.Max(60, summary.ElapsedSeconds), start = end - 60, previous = -10;
        Polyline? line = null;
        foreach (var point in _noise.Trend)
        {
            if (point.Seconds < start) continue;
            if (point.Dbfs is null || point.Quality == "Invalid") { line = null; continue; }
            if (line is null || point.Seconds - previous > 1)
            {
                line = new() { Stroke = new SolidColorBrush(Color.FromRgb(20, 125, 104)), StrokeThickness = 3 };
                TrendCanvas.Children.Add(line);
            }
            line.Points.Add(new((point.Seconds - start) / 60 * TrendCanvas.ActualWidth,
                -Math.Clamp(point.Dbfs.Value, -100, 0) / 100 * TrendCanvas.ActualHeight));
            previous = point.Seconds;
        }
    }
}

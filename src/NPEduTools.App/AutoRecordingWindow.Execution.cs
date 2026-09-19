using System.Windows;
using NPEduTools.Contracts;

namespace NPEduTools.App;

public partial class AutoRecordingWindow
{
    private bool _automaticChanging;
    private string? _automaticError;
    private void AutomaticChanged(AutomaticRecordingState state)
    {
        RealStatus.Text = _automaticError ?? state.Message + (state.Error is null ? "" : " · " + state.Error);
        ToggleReal.Content = state.Enabled ? "停用自动录制" : state.SuspendedByMode ? "启用（仍由课堂模式暂停）" : "启用自动录制";
        ToggleReal.IsEnabled = !_automaticChanging;
        RealSkipDay.Content = state.SkipDate is not null && state.SkipDate == _today ? "恢复今日自动录制" : "今天不再录制";
        RealEvents.ItemsSource = state.Recent.Select(e => $"{e.Date:yyyy-MM-dd} {e.Start:HH:mm} · {e.Subject} · {PhaseLabel(e.Phase)}\n{e.Reason}" +
            (e.OutputFile is null ? e.RecoveryDirectory is null ? "" : "\n保留片段：" + e.RecoveryDirectory : "\n视频：" + e.OutputFile)).ToArray();
    }
    private static string PhaseLabel(string phase) => phase switch
    {
        "Starting" => "准备录制", "Recording" => "录制中", "Paused" => "已暂停", "Finalizing" => "正在保存",
        "Recorded" => "已保存", "Skipped" => "已跳过", "Missed" => "已错过", "Conflict" => "计划冲突", "Interrupted" => "中断待检查", _ => "未完成"
    };
    private async Task AutomaticActionAsync(string action, RecordingOptions? options = null)
    {
        if (_automaticChanging) return;
        _automaticChanging = true; _automaticError = null; ToggleReal.IsEnabled = false;
        try { await _realRecording.SetAutomaticAsync(action, options); }
        catch (Exception error) when (error is not OutOfMemoryException) { RealStatus.Text = _automaticError = error.Message; }
        finally { _automaticChanging = false; ToggleReal.IsEnabled = true; }
    }
    private async void ToggleRealClicked(object sender, RoutedEventArgs e)
    {
        if (_realRecording.Automatic.Enabled) { await AutomaticActionAsync("disable"); return; }
        try
        {
            var options = RecordingWindow.ReadSavedOptions(_pipe);
            if (options is null) { RealStatus.Text = _automaticError = "请先到“微课录制”配置屏幕、音源和目录，点击“保存设置”。"; return; }
            await AutomaticActionAsync("enable", options);
        }
        catch (Exception error) when (error is not OutOfMemoryException) { RealStatus.Text = _automaticError = "录制设置无法读取：" + error.Message; }
    }
    private async void RealSkipClicked(object sender, RoutedEventArgs e) => await AutomaticActionAsync("skip-next");
    private async void RealSkipDayClicked(object sender, RoutedEventArgs e) => await AutomaticActionAsync(_realRecording.Automatic.SkipDate is not null && _realRecording.Automatic.SkipDate == _today ? "resume-day" : "skip-day");
}

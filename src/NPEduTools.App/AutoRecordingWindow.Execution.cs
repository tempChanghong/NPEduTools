using System.Windows;
using System.Diagnostics;
using System.IO;
using System.Windows.Controls;
using NPEduTools.Contracts;

namespace NPEduTools.App;

public partial class AutoRecordingWindow
{
    private bool _automaticChanging;
    private RecordingExecution[] _recordRows = [];
    private int _recordFilter;
    private void RecordFilterChanged(object sender, SelectionChangedEventArgs e)
    { _recordFilter = RecordFilter.SelectedIndex; RefreshRecordRows(); }
    private void RefreshRecordRows()
    {
        if (RealEvents is null || RealEventsEmpty is null) return;
        var rows = _recordRows.Where(e => _recordFilter == 0 || _recordFilter == 1 && (e.Phase is "Failed" or "Interrupted" or "Conflict" or "Missed") || _recordFilter == 2 && e.Phase == "Recorded").ToArray();
        RealEventsEmpty.Visibility = rows.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        RealEventsEmpty.Text = _recordFilter == 0 ? "暂无自动录课记录；到达已启用的计划时间后才会执行。" : "此筛选下暂无自动录课记录。";
        RealEvents.ItemsSource = rows.Select(e => $"{e.Date:yyyy-MM-dd} {e.Start:HH:mm} · {e.Subject} · {PhaseLabel(e.Phase)}\n{e.Reason}" +
            (e.OutputFile is null ? e.RecoveryDirectory is null ? "" : "\n保留片段：" + e.RecoveryDirectory : "\n视频：" + e.OutputFile)).ToArray();
    }
    private void RecordingChanged(RecordingState state)
    {
        LatestRecording.Text = _realRecording.StateAvailable ? state.Message : "当前录制状态未确认；下列位置是最后收到的结果，不代表新任务已经完成。";
        LatestRecordingPath.Text = state.OutputFile ?? state.RecoveryDirectory ?? "后台尚未返回视频或保留片段位置。";
        OpenLatestVideo.IsEnabled = state.OutputFile is not null;
        OpenLatestFolder.IsEnabled = state.OutputFile is not null || state.RecoveryDirectory is not null;
        StopCurrent.Visibility = state.Active ? Visibility.Visible : Visibility.Collapsed;
        StopCurrent.IsEnabled = _realRecording.StateAvailable && state.Active && !state.Busy;
        RealSkip.IsEnabled = !_automaticChanging && _realRecording.StateAvailable;
    }
    private void OpenLatestVideoClicked(object sender, RoutedEventArgs e) => OpenLatestPath(_realRecording.State.OutputFile);
    private void OpenLatestFolderClicked(object sender, RoutedEventArgs e) => OpenLatestPath(_realRecording.State.OutputFile is { } file ? Path.GetDirectoryName(file) : _realRecording.State.RecoveryDirectory);
    private void OpenLatestPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); RecordsError.Text = ""; }
        catch (Exception error) when (error is IOException or System.ComponentModel.Win32Exception or ArgumentException)
        { RecordsError.Text = "无法打开该位置，请检查文件是否已移动。"; }
    }
    private async void StopCurrentClicked(object sender, RoutedEventArgs e) => await _realRecording.SendAsync("stop");
    private void AutomaticChanged(AutomaticRecordingState state)
    {
        RealStatus.Text = (_realRecording.AutomaticAvailable ? state.Message : "录制后台连接中断或尚未确认；自动录课状态未知。") + (state.Error is null ? "" : " · " + state.Error);
        ToggleReal.Content = state.Enabled ? "关闭自动录课" : "开启自动录课";
        ToggleReal.ToolTip = state.SuspendedByMode ? "课堂模式暂停仍然有效；开启总开关不会解除暂停。" : "使用已保存的录制设置，按计划和学校时间执行。";
        ToggleReal.IsEnabled = !_automaticChanging && _realRecording.AutomaticAvailable;
        RealSkip.IsEnabled = !_automaticChanging && _realRecording.StateAvailable;
        RealSkipDay.Content = state.SkipDate is not null && state.SkipDate == _today ? "恢复今日自动录制" : "今天不再录制";
        if (!_recordRows.SequenceEqual(state.Recent)) { _recordRows = state.Recent; RefreshRecordRows(); }

    }
    private static string PhaseLabel(string phase) => phase switch
    {
        "Starting" => "准备录制", "Recording" => "录制中", "Paused" => "已暂停", "Finalizing" => "正在保存",
        "Recorded" => "已保存", "Skipped" => "已跳过", "Missed" => "已错过", "Conflict" => "计划冲突", "Interrupted" => "中断待检查", _ => "未完成"
    };
    private void SetAutomaticFeedback(string? message, bool pending = false)
    {
        AutomaticFeedbackText.Text = message is null ? "" : (pending ? "本次操作：" : "上次操作提示：") + message;
        AutomaticFeedbackText.Visibility = message is null ? Visibility.Collapsed : Visibility.Visible;
    }
    private async Task AutomaticActionAsync(string action, RecordingOptions? options = null)
    {
        if (_automaticChanging) return;
        _automaticChanging = true; SetAutomaticFeedback("正在等待后台确认…", pending: true); ToggleReal.IsEnabled = false;
        try { await _realRecording.SetAutomaticAsync(action, options); SetAutomaticFeedback(null); }
        catch (Exception error) when (error is not OutOfMemoryException) { SetAutomaticFeedback(error.Message); }
        finally { _automaticChanging = false; ToggleReal.IsEnabled = _realRecording.AutomaticAvailable; RecordingChanged(_realRecording.State); }
    }
    private async void ToggleRealClicked(object sender, RoutedEventArgs e)
    {
        if (_realRecording.Automatic.Enabled) { await AutomaticActionAsync("disable"); return; }
        try
        {
            var options = RecordingWindow.ReadSavedOptions(_pipe);
            if (options is null) { SetAutomaticFeedback("请先到“立即录制 → 修改录制设置”配置屏幕、音源和目录，点击“保存设置”。"); return; }
            await AutomaticActionAsync("enable", options);
        }
        catch (Exception error) when (error is not OutOfMemoryException) { SetAutomaticFeedback("录制设置无法读取：" + error.Message); }
    }
    private async void RealSkipClicked(object sender, RoutedEventArgs e) => await AutomaticActionAsync("skip-upcoming");
    private async void RealSkipDayClicked(object sender, RoutedEventArgs e) => await AutomaticActionAsync(_realRecording.Automatic.SkipDate is not null && _realRecording.Automatic.SkipDate == _today ? "resume-day" : "skip-day");
}

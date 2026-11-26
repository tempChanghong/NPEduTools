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
    private sealed record ExecutionRow(string Title, string Reason, string? Video, string? Recovery)
    {
        public string Location => Video is not null ? "视频：" + Video : Recovery is not null ? "保留片段：" + Recovery : "";
        public string? Folder => Video is not null ? Path.GetDirectoryName(Video) : Recovery;
        public bool HasVideo => !string.IsNullOrWhiteSpace(Video);
        public bool HasFolder => !string.IsNullOrWhiteSpace(Folder);
        public string FolderAction => Video is null ? "打开保留片段" : "打开文件夹";
        public override string ToString() => string.Join("\n", Title, Reason, Location);
    }
    private void RecordFilterChanged(object sender, SelectionChangedEventArgs e)
    { _recordFilter = RecordFilter.SelectedIndex; RefreshRecordRows(); }
    private void RefreshRecordRows()
    {
        if (RealEvents is null || RealEventsEmpty is null) return;
        var rows = _recordRows.Where(e => _recordFilter == 0 || _recordFilter == 1 && (e.Phase is "Failed" or "Interrupted" or "Conflict" or "Missed") || _recordFilter == 2 && e.Phase == "Recorded").ToArray();
        RealEventsEmpty.Visibility = rows.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        RealEventsEmpty.Text = _recordFilter == 0 ? "暂无自动录课记录；到达已启用的计划时间后才会执行。" : "此筛选下暂无自动录课记录。";
        RealEvents.ItemsSource = rows.Select(e => new ExecutionRow(
            $"{e.Date:yyyy-MM-dd} {e.Start:HH:mm} · {e.Subject} · {PhaseLabel(e.Phase)}", e.Reason,
            e.Phase == "Recorded" && !string.IsNullOrWhiteSpace(e.OutputFile) ? e.OutputFile : null,
            string.IsNullOrWhiteSpace(e.RecoveryDirectory) ? null : e.RecoveryDirectory)).ToArray();
    }
    private static ExecutionRow LatestResult(RecordingState state) => new("", state.Message,
        state.Phase == "Saved" && !string.IsNullOrWhiteSpace(state.OutputFile) ? state.OutputFile : null,
        state.Phase == "Failed" && !string.IsNullOrWhiteSpace(state.RecoveryDirectory) ? state.RecoveryDirectory : null);
    private void RecordingChanged(RecordingState state)
    {
        var result = LatestResult(state);
        LatestRecording.Text = _realRecording.StateAvailable ? state.Message : "当前录制状态未确认；下列位置是最后收到的结果，不代表新任务已经完成。";
        if (_realRecording.StateAvailable && state.Phase == "Failed" && !string.IsNullOrWhiteSpace(state.Error) && state.Error != state.Message)
            LatestRecording.Text += " · " + state.Error;
        LatestRecordingPath.Text = result.Video ?? result.Recovery ?? "后台尚未返回视频或保留片段位置。";
        OpenLatestVideo.IsEnabled = result.HasVideo;
        OpenLatestFolder.IsEnabled = result.HasFolder;
        OpenLatestFolder.Content = result.Recovery is not null ? "打开保留片段" : "打开文件夹";
        StopCurrent.Visibility = state.Active ? Visibility.Visible : Visibility.Collapsed;
        StopCurrent.IsEnabled = _realRecording.StateAvailable && state.Active && !state.Busy;
        RefreshAutomaticControls();
    }
    private void OpenLatestVideoClicked(object sender, RoutedEventArgs e) => OpenLatestPath(LatestResult(_realRecording.State).Video);
    private void OpenLatestFolderClicked(object sender, RoutedEventArgs e) => OpenLatestPath(LatestResult(_realRecording.State).Folder);
    private void OpenRecordVideoClicked(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: ExecutionRow row }) OpenLatestPath(row.Video);
    }
    private void OpenRecordFolderClicked(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: ExecutionRow row }) OpenLatestPath(row.Folder);
    }
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
        ToggleReal.SetResourceReference(StyleProperty, state.Enabled ? "TeacherButton" : "TeacherPrimary");
        ToggleReal.ToolTip = state.SuspendedByMode ? "课堂模式暂停仍然有效；开启总开关不会解除暂停。" : "使用已保存的录制设置，按计划和学校时间执行。";
        RefreshAutomaticControls();
        bool skippedToday = state.SkipDate is not null && state.SkipDate == _today;
        RealSkipDay.Content = skippedToday ? "恢复今日自动录制" : "今天不再录制";
        RealSkipDay.ToolTip = skippedToday ? "恢复今天余下的自动录课安排。" : "今天不再自动录制，并结束正在进行的自动录课。";
        if (!_recordRows.SequenceEqual(state.Recent)) { _recordRows = state.Recent; RefreshRecordRows(); }
        bool confirmed = _realRecording.AutomaticAvailable;
        NextPlanOverview.Text = confirmed && !state.Enabled ? "保存安排后可手动开启自动录课" :
            (confirmed && HomeRecordingPresentation.ConfirmedNext(state) is not null ? "下一节  " : "") + HomeRecordingPresentation.Next(state, confirmed);
    }
    private void RefreshAutomaticControls()
    {
        bool available = !_automaticChanging && _realRecording.StateAvailable && _realRecording.AutomaticAvailable;
        ToggleReal.IsEnabled = RealSkipDay.IsEnabled = available;
        bool canSkip = _realRecording.AutomaticAvailable && HomeRecordingPresentation.ConfirmedNext(_realRecording.Automatic) is not null;
        RealSkip.Visibility = canSkip ? Visibility.Visible : Visibility.Collapsed;
        RealSkip.IsEnabled = available && canSkip;
    }
    private static string PhaseLabel(string phase) => phase switch
    {
        "Starting" => "准备录制", "Recording" => "录制中", "Paused" => "已暂停", "Finalizing" => "正在保存",
        "Recorded" => "已保存", "Skipped" => "已跳过", "Missed" => "已错过", "Conflict" => "计划冲突", "Interrupted" => "中断待检查", _ => "未完成"
    };
    private void SetAutomaticFeedback(string? message, bool pending = false)
    {
        AutomaticFeedbackText.Text = message is null ? "" : (pending ? "本次操作：" : "上次操作提示：") + message;
        AutomaticFeedbackText.SetResourceReference(TextBlock.ForegroundProperty, pending ? "TeacherMutedBrush" : "TeacherErrorBrush");
        AutomaticFeedbackText.Visibility = message is null ? Visibility.Collapsed : Visibility.Visible;
    }
    private async Task AutomaticActionAsync(string action, RecordingOptions? options = null)
    {
        if (_automaticChanging || !_realRecording.StateAvailable || !_realRecording.AutomaticAvailable) return;
        _automaticChanging = true; SetAutomaticFeedback("正在等待后台确认…", pending: true); RefreshAutomaticControls();
        try { await _realRecording.SetAutomaticAsync(action, options); SetAutomaticFeedback(null); }
        catch (Exception error) when (error is not OutOfMemoryException) { SetAutomaticFeedback(error.Message); }
        finally { _automaticChanging = false; RefreshAutomaticControls(); }
    }
    private async void ToggleRealClicked(object sender, RoutedEventArgs e)
    {
        if (_realRecording.Automatic.Enabled) { await AutomaticActionAsync("disable"); return; }
        try
        {
            var options = RecordingWindow.ReadSavedOptions(_pipe);
            if (options is null) { SetAutomaticFeedback("请先到“立即录制 → 修改录制设置”配置屏幕、音源和目录，点击“保存录制设置”。"); return; }
            await AutomaticActionAsync("enable", options);
        }
        catch (Exception error) when (error is not OutOfMemoryException) { SetAutomaticFeedback("录制设置无法读取：" + error.Message); }
    }
    private async void RealSkipClicked(object sender, RoutedEventArgs e)
    {
        if (RealSkip.IsEnabled) await AutomaticActionAsync("skip-upcoming");
    }
    private async void RealSkipDayClicked(object sender, RoutedEventArgs e) => await AutomaticActionAsync(_realRecording.Automatic.SkipDate is not null && _realRecording.Automatic.SkipDate == _today ? "resume-day" : "skip-day");
}

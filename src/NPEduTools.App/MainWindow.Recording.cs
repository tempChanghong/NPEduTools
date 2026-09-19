using System.Windows;
using NPEduTools.Contracts;

namespace NPEduTools.App;

public partial class MainWindow
{
    private RecordingClient _recording = null!;
    private RecordingWindow? _recordingWindow;
    private AutoRecordingWindow? _autoRecordingWindow;
    private void OpenRecordingPlanClicked(object sender, RoutedEventArgs e) => ShowRecordingPlan();
    private void ShowRecordingPlan()
    {
        _quick?.Collapse(false);
        _autoRecordingWindow ??= new AutoRecordingWindow(_pipe, _recording);
        _autoRecordingWindow.Show();
        if (_autoRecordingWindow.WindowState == WindowState.Minimized) _autoRecordingWindow.WindowState = WindowState.Normal;
        _autoRecordingWindow.Activate();
    }
    private void InitializeRecording()
    {
        _recording = new(Dispatcher, _pipe);
        _recording.Changed += RefreshRecording;
        _recording.AutomaticChanged += state => _quick?.UpdateAutomaticRecording(state);
        RefreshRecording(_recording.State);
    }
    private void OpenRecordingClicked(object sender, RoutedEventArgs e) => ShowRecording();
    private void ShowRecording()
    {
        _quick?.Collapse(false);
        _recordingWindow ??= new RecordingWindow(_recording, _pipe);
        _recordingWindow.Show();
        if (_recordingWindow.WindowState == WindowState.Minimized) _recordingWindow.WindowState = WindowState.Normal;
        _recordingWindow.Activate();
    }
    private void RefreshRecording(RecordingState state)
    {
        string clock = TimeSpan.FromSeconds(Math.Max(0, state.Seconds)).ToString(@"hh\:mm\:ss");
        RecordingHomeStatus.Text = state.Active ? $"{state.Message} · {clock}" : state.Message;
        RecordingHomeButton.Content = state.Active ? "录制控制" : "配置与录制";
        _quick?.UpdateRecording(state);
    }
    private async void SkipAutomaticToday()
    {
        try { await _recording.SetAutomaticAsync("skip-day"); }
        catch (Exception error) when (error is not OutOfMemoryException) { HomeMessage.Text = error.Message; ShowRecordingPlan(); }
    }
}

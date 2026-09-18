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
        _autoRecordingWindow ??= new AutoRecordingWindow(_pipe);
        _autoRecordingWindow.Show();
        if (_autoRecordingWindow.WindowState == WindowState.Minimized) _autoRecordingWindow.WindowState = WindowState.Normal;
        _autoRecordingWindow.Activate();
    }
    private void InitializeRecording()
    {
        // An explicitly private test endpoint may record an owned fixture instead of the desktop.
        _recording = new(Dispatcher, _pipe.StartsWith("NPEduTools.Test.", StringComparison.Ordinal)
            ? Environment.GetEnvironmentVariable("NPEEDUTOOLS_RECORDING_FIXTURE") : null);
        _recording.Changed += RefreshRecording;
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
}

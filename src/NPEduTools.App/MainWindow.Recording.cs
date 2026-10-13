using System.Windows;
using NPEduTools.Contracts;

namespace NPEduTools.App;

public partial class MainWindow
{
    private RecordingClient _recording = null!;
    private RecordingWindow? _recordingWindow;
    private AutoRecordingWindow? _autoRecordingWindow;
    private void OpenRecordingPlanClicked(object sender, RoutedEventArgs e) => ShowRecordingPlan();
    private void ShowRecordingPlan() => ShowRecordingWorkspace("Plan");
    private string _recordingTab = "Plan";
    private Func<Task<RecordingEnvironment>> _recordingProbe = RecordingClient.ProbeAsync;
    private void RecordingNavClicked(object sender, RoutedEventArgs e) => ShowRecordingWorkspace(_recording?.State.Active == true ? "Manual" : _recordingTab);
    private void RecordingManualTabClicked(object sender, RoutedEventArgs e) => ShowRecordingWorkspace("Manual");
    private void RecordingPlanTabClicked(object sender, RoutedEventArgs e) => ShowRecordingWorkspace("Plan");
    private void RecordingRecordsTabClicked(object sender, RoutedEventArgs e) => ShowRecordingWorkspace("Records");
    private void HideRecordingWorkspace()
    {
        RecordingWorkspace.Visibility = Visibility.Collapsed;
        RecordingNav.Tag = null;
    }
    private void ShowRecordingWorkspace(string tab)
    {
        if (_recording is null) return;
        _quick?.Collapse(false);
        SelectPage(false); HomePage.Visibility = Visibility.Collapsed;
        RecordingWorkspace.Visibility = Visibility.Visible;
        HomeNav.Tag = null; RecordingNav.Tag = "active"; PageBreadcrumb.Text = "录课";
        _recordingTab = tab;
        RecordingManualTab.Tag = tab == "Manual" ? "active" : null;
        RecordingPlanTab.Tag = tab == "Plan" ? "active" : null;
        RecordingRecordsTab.Tag = tab == "Records" ? "active" : null;
        RecordingManualHost.Visibility = tab == "Manual" ? Visibility.Visible : Visibility.Collapsed;
        RecordingPlanHost.Visibility = tab != "Manual" ? Visibility.Visible : Visibility.Collapsed;
        if (tab == "Manual" && _recordingWindow is null)
        {
            _recordingWindow = new RecordingWindow(_recording, _pipe, _recordingProbe) { WorkspaceReturn = () => SelectPage(false) };
            RecordingManualHost.Content = new WorkspacePanel(_recordingWindow, this);
        }
        if (tab != "Manual")
        {
            if (_autoRecordingWindow is null)
            {
                _autoRecordingWindow = new AutoRecordingWindow(_pipe, _recording, ShowRecordingSettings)
                { WorkspaceReturn = () => SelectPage(false) };
                RecordingPlanHost.Content = new WorkspacePanel(_autoRecordingWindow, this);
            }
            _autoRecordingWindow.ShowWorkspaceSection(tab == "Records");
        }
        RestoreWindow();
    }
    private void InitializeRecording()
    {
        _recording = new(Dispatcher, _pipe);
        _recording.Changed += RefreshRecording;
        _recording.AutomaticChanged += state => { _quick?.UpdateAutomaticRecording(state); RefreshHomeRecordingNext(state); };
        RefreshHomeRecordingNext(_recording.Automatic);
        RefreshRecording(_recording.State);
    }
    private void OpenRecordingClicked(object sender, RoutedEventArgs e) => ShowRecording();
    private void ShowRecording() => ShowRecordingWorkspace("Manual");
    private void ShowRecordingSettings() { ShowRecording(); _recordingWindow?.ShowSettingsPanel(); }
    private void RefreshRecording(RecordingState state)
    {
        string clock = TimeSpan.FromSeconds(Math.Max(0, state.Seconds)).ToString(@"hh\:mm\:ss");
        RecordingHomeTitle.Text = HomeRecordingPresentation.Title(state, _recording.StateAvailable);
        RecordingHomeStatus.Text = !_recording.StateAvailable ? "录制后台未确认当前状态，等待重新连接。" : state.Active ? $"{state.Message} · {clock}" : state.Message;
        RecordingHomeAction.Text = HomeRecordingPresentation.Action(state, _recording.StateAvailable);
        _quick?.UpdateRecording(state, _recording.StateAvailable);
    }
    private void HomeRecordingPrimaryClicked(object sender, RoutedEventArgs e)
    {
        if (_recording?.StateAvailable == true && _recording.State.Phase is "Saved" or "Failed") ShowRecordingWorkspace("Records");
        else ShowRecording();
    }
    private void RefreshHomeRecordingNext(AutomaticRecordingState state) =>
        HomeRecordingNext.Text = HomeRecordingPresentation.Next(state, _recording.AutomaticAvailable);
    private async void SkipAutomaticToday()
    {
        try { await _recording.SetAutomaticAsync("skip-day"); }
        catch (Exception error) when (error is not OutOfMemoryException) { HomeMessage.Text = error.Message; ShowRecordingPlan(); }
    }
}

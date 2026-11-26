using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using NPEduTools.Contracts;
using NPEduTools.Core;

namespace NPEduTools.App;

public partial class MainWindow
{
    private bool _bridgeInspectionBusy;
    private bool _classIslandConfigurationPresented;
    private bool _bridgeChecked;
    private bool _bridgeReadFailed;
    private bool? _classIslandNeedsProgram;
    private readonly SchoolClockTracker _bridgeClock = new();
    private readonly Stopwatch _bridgeElapsed = Stopwatch.StartNew();
    private readonly DispatcherTimer _bridgeClockTimer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private void InitializeBridgeClock()
    {
        _bridgeClockTimer.Tick += (_, _) => RenderBridgeClock();
        Loaded += (_, _) => _bridgeClockTimer.Start();
        Closed += (_, _) => _bridgeClockTimer.Stop();
        _model.PropertyChanged += (_, _) =>
            ClassIslandLessonSummaryText.Text = _model.LessonPlan == "—" ? "未核实" : _model.LessonPlan;
    }
    private void ClassIslandOverviewSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (sender is Grid grid && ClassIslandTimePane is not null && ClassIslandStatusPane is not null)
            LayoutClassIslandOverview(grid, e.NewSize.Width);
    }
    private void LayoutClassIslandOverview(Grid grid, double width)
    {
        bool showClock = ClassIslandTimePane.Visibility == Visibility.Visible;
        bool split = width >= 770 && showClock;
        grid.ColumnDefinitions[0].Width = new(split ? 2 : 1, GridUnitType.Star);
        grid.ColumnDefinitions[1].Width = split ? new(3, GridUnitType.Star) : new(0);
        Grid.SetRow(ClassIslandStatusPane, split || !showClock ? 0 : 1);
        Grid.SetColumn(ClassIslandStatusPane, split ? 1 : 0);
        ClassIslandTimePane.Margin = split ? new(0, 0, 24, 0) : new(0, 0, 0, 10);
    }
    private void RenderBridgeClock()
    {
        var reading = _bridgeClock.Read(_bridgeElapsed.Elapsed);
        var view = ClassIslandClockPresentation.From(reading);
        ClassIslandClockText.Text = view.Clock;
        ClassIslandClockDetail.Text = _bridgeReadFailed ? "暂时无法读取后台，请重新检查连接。" : view.Detail;
        string summary = reading.Fresh
            ? reading.CanStart ? "已同步" : "时间校验中"
            : _bridgeChecked ? "暂不可用" : "等待检查";
        if (ClassIslandBridgeSummaryText.Text != summary)
            ClassIslandBridgeSummaryText.Text = summary;
        var clockVisibility = _classIslandNeedsProgram == true && !reading.Fresh ? Visibility.Collapsed : Visibility.Visible;
        if (ClassIslandTimePane.Visibility != clockVisibility)
        {
            ClassIslandTimePane.Visibility = clockVisibility;
            LayoutClassIslandOverview(ClassIslandOverviewGrid, ClassIslandOverviewGrid.ActualWidth);
        }
        bool needsBridgeCheck = _bridgeChecked && !reading.Fresh && _classIslandNeedsProgram != true;
        ClassIslandRefreshButton.Visibility = clockVisibility;
        string action = needsBridgeCheck ? "重新检查连接" : "检查时间桥接";
        if (!Equals(ClassIslandRefreshButton.Content, action))
        {
            ClassIslandRefreshButton.Content = action;
            ClassIslandRefreshButton.SetResourceReference(StyleProperty, needsBridgeCheck ? "TeacherPrimary" : "TeacherQuiet");
        }
    }
    private async void RefreshClassIslandBridgeClicked(object sender, RoutedEventArgs e) => await RefreshClassIslandBridgeAsync();
    private async Task RefreshClassIslandBridgeAsync()
    {
        if (_bridgeInspectionBusy || _lifetime.IsCancellationRequested) return;
        _bridgeInspectionBusy = true;
        long generation = _launchUiGeneration;
        try
        {
            var data = await ReadClassIslandProgramAsync(generation);
            if (!CurrentBridgeInspection(generation)) return;
            string? path = data?.Settings.ExecutablePath;
            await InspectClassIslandTimeAsync(generation, path);
            if (!CurrentBridgeInspection(generation) || data is null) return;

            bool hasPath = !string.IsNullOrWhiteSpace(path);
            bool accessible = hasPath && await Task.Run(() => System.IO.File.Exists(path), _lifetime.Token);
            if (!CurrentBridgeInspection(generation)) return;
            ShowClassIslandProgramStatus(!accessible, accessible ? "文件可访问" : hasPath ? "文件不可访问，请重新选择" : "尚未保存");
            if (!_classIslandConfigurationPresented)
            {
                _classIslandConfigurationPresented = true;
                if (!ClassIslandConfiguration.IsKeyboardFocusWithin)
                    ClassIslandConfiguration.IsExpanded = !accessible;
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        finally { _bridgeInspectionBusy = false; }
    }

    private async Task InspectClassIslandTimeAsync(long generation, string? path)
    {
        try
        {
            var start = Stopwatch.GetTimestamp();
            var response = await ManagementRequestAsync(new(1, Guid.NewGuid(), "classisland.school-clock"));
            if (!CurrentBridgeInspection(generation)) return;
            double roundTripMs = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            var frame = response.Outcome == "Succeeded" ? response.SchoolClock : null;
            _bridgeChecked = true;
            _bridgeReadFailed = false;
            _bridgeClock.Accept(frame ?? SchoolClockFrame.Unavailable(response.Message), _bridgeElapsed.Elapsed, roundTripMs);
            RenderBridgeClock();
            ClassIslandBridgeStatus.Text = ClassIslandBridgeInspection.Describe(path,
                frame, roundTripMs);
        }
        catch (Exception error) when (IsManagementError(error))
        {
            if (CurrentBridgeInspection(generation))
            {
                _bridgeChecked = true;
                _bridgeReadFailed = true;
                _bridgeClock.Unavailable("后台状态读取失败", _bridgeElapsed.Elapsed);
                RenderBridgeClock();
                ClassIslandBridgeStatus.Text = "时间桥接插件：后台状态读取失败，旧结果已撤下；请检查连接后刷新。";
            }
        }
    }

    private bool CurrentBridgeInspection(long generation) =>
        !_lifetime.IsCancellationRequested && generation == _launchUiGeneration;

    private async Task<LaunchData?> ReadClassIslandProgramAsync(long generation)
    {
        try
        {
            var response = await ManagementRequestAsync(new(1, Guid.NewGuid(), "classisland.config.get"));
            if (!CurrentBridgeInspection(generation)) return null;
            if (response is not { Outcome: "Succeeded", Launch: { StorageWarning: null } data })
            {
                ShowClassIslandProgramStatus(null, "保存状态待核实");
                return null;
            }
            ApplyClassIslandConfiguration(data, rebaseDraft: false);
            return data;
        }
        catch (Exception error) when (IsManagementError(error))
        {
            if (CurrentBridgeInspection(generation)) ShowClassIslandProgramStatus(null, "保存状态待核实");
            return null;
        }
    }

    private void ShowClassIslandProgramStatus(bool? needsProgram, string message)
    {
        _classIslandNeedsProgram = needsProgram;
        ClassIslandProgramStatus.Text = message;
        ClassIslandConfiguration.Header = needsProgram switch
        {
            true => "选择程序位置",
            false => "更改程序位置",
            null => "程序位置"
        };
        RenderBridgeClock();
    }
}

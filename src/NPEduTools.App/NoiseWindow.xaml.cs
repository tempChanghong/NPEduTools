using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Shapes;
using NPEduTools.Contracts;
using NPEduTools.Core;

namespace NPEduTools.App;

public partial class NoiseWindow : Window
{
    private const string UnconfirmedManualResult = "请求结果待确认，恢复连接后将自动核对。";
    private readonly string _pipe;
    private readonly CancellationTokenSource _lifetime = new();
    private NoiseState? _state;
    private ManualStudyState? _manual;
    private readonly StudyDisplayReturn _returnDisplay = new();
    private bool _closing, _busy, _connected;
    private bool _manualResultUnconfirmed;
    private ManualStudyCommand? _pendingManualCommand;
    private string? _timeValidationMessage;
    private long _uiOperation;
    private readonly NoiseCalibrationRead _calibrationRead = new();
    internal Action? OpenWorkspaceDevices { get; init; }
    internal Action? OpenWorkspaceCalibration { get; init; }
    internal Action? OpenWorkspaceReport { get; init; }
    public NoiseWindow(string pipe)
    {
        _pipe = pipe;
        InitializeComponent();
        ManualPageScroll.SizeChanged += (_, args) => ArrangeManualPage(args.NewSize.Width);
        ManualStartDate.SelectedDate = ManualEndDate.SelectedDate = DateTime.Today;
        DesktopDialogSizing.Attach(this, pipe);
        Closing += (_, args) => { if (!_closing) { args.Cancel = true; Hide(); } };
        IsVisibleChanged += (_, _) =>
        {
            if (!IsVisible && !WorkspacePanel.Embedded(this)) WorkspaceVisibilityChanged(false);
        };
        Closed += (_, _) => _lifetime.Cancel();
        Microphone.SelectionChanged += (_, _) => UpdateButtons();
        ManualConfiguration.AddHandler(System.Windows.Controls.Primitives.TextBoxBase.TextChangedEvent,
            new TextChangedEventHandler((_, _) => ClearTimeValidationMessage()));
        ManualStartDate.SelectedDateChanged += (_, _) => ClearTimeValidationMessage();
        ManualEndDate.SelectedDateChanged += (_, _) => ClearTimeValidationMessage();
        TrendCanvas.IsVisibleChanged += (_, _) => DrawTrend();
        ManualOptionsChanged(this, new RoutedEventArgs());
        _ = PollAsync();
    }
    public void Shutdown() { _closing = true; Close(); }
    private void ArrangeManualPage(double width)
    {
        // The main window scales its content on compact displays, which can inflate the
        // layout width seen by this scroll viewer. Use the window width as a second limit.
        bool wide = ManualConfiguration.Visibility == Visibility.Visible &&
            width >= 760 && WorkspacePanel.DialogOwner(this).ActualWidth >= 930;
        ManualSideColumn.Width = new GridLength(wide ? 276 : 0);
        Grid.SetColumn(ManualSidePanel, wide ? 1 : 0);
        ManualSidePanel.Margin = wide ? new Thickness(10, 0, 0, 0) : new Thickness();
        Grid.SetRow(ManualConfiguration, wide ? 1 : 2);
    }
    internal void WorkspaceVisibilityChanged(bool visible)
    {
        if (_lifetime.IsCancellationRequested) return;
        _uiOperation++;
        Render(null); // A hidden snapshot cannot become a fresh execution fact on return.
    }
    private async Task<HostResponse> RequestAsync(string capability, NoiseCommand? command = null)
    {
        var request = new HostRequest(Protocol.Version, Guid.NewGuid(), capability, Noise: command);
        if (command?.Action == "stop")
        {
            var authorized = await NoiseManagementDialog.AuthorizeAsync(WorkspacePanel.DialogOwner(this), _pipe, request);
            if (authorized is null) return new(Protocol.Version, request.RequestId, "Rejected", "MANAGEMENT_CANCELLED", "未验证，监测继续。", Noise: _state);
            request = authorized;
        }
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        deadline.CancelAfter(TimeSpan.FromSeconds(4));
        return await _calibrationRead.RequestAsync(_pipe, request, deadline.Token);
    }
    private async Task PollAsync()
    {
        bool loadedDevices = false;
        while (!_lifetime.IsCancellationRequested)
        {
            long operation = _uiOperation;
            var guard = GuardFiles.ForPipe(_pipe).Read<GuardStatus>("status.json");
            var age = guard is null ? TimeSpan.MaxValue : DateTimeOffset.UtcNow - guard.UpdatedAt;
            GuardStatusText.Text = guard is not null && age >= TimeSpan.Zero && age < TimeSpan.FromSeconds(5)
                ? $"{guard.Message} 累计恢复请求：{guard.Restarts} 次。" : "守护未连接或状态已陈旧；不会据此判断监测已停止。";
            try
            {
                if (!_busy && (WorkspacePanel.Visible(this) || !loadedDevices))
                {
                    long calibrationAt = System.Diagnostics.Stopwatch.GetTimestamp();
                    var response = await RequestAsync(loadedDevices ? "noise.status" : "noise.devices");
                    if (_lifetime.IsCancellationRequested || operation != _uiOperation) continue;
                    if (response.NoiseDevices is not null) { _state = response.Noise; SetDevices(response.NoiseDevices); loadedDevices = true; }
                    using var manualTimeout=CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);manualTimeout.CancelAfter(TimeSpan.FromSeconds(4));
                    long manualAt = System.Diagnostics.Stopwatch.GetTimestamp();
                    var manual=await HostClient.RequestAsync(_pipe,new HostRequest(1,Guid.NewGuid(),"manual.study.status"),manualTimeout.Token);
                    if(_lifetime.IsCancellationRequested || operation!=_uiOperation)continue;
                    _manual=manual.Outcome == "Succeeded" && System.Diagnostics.Stopwatch.GetElapsedTime(manualAt).TotalSeconds < 3.5 ? manual.ManualStudy : null;
                    _returnDisplay.Observe(_manual is not null ? manual.Study : null);
                    bool signalFresh = System.Diagnostics.Stopwatch.GetElapsedTime(calibrationAt).TotalSeconds < 3.5;
                    if (_manual is not null && signalFresh && response.Noise is not null && _manualResultUnconfirmed)
                    {
                        _manualResultUnconfirmed = false;
                        if (MessageText.Text == UnconfirmedManualResult) MessageText.Text = "";
                    }
                    RenderWithCalibration(signalFresh ? response.Noise : null, signalFresh ? response.NoiseCalibrationStatus : null);
                    if (!signalFresh)
                    {
                        StateText.Text = "状态读取较慢，当前监测状态未知。";
                        DisplayStatusText.Text = "展示状态待确认；等待后台最新状态。";
                        SummaryText.Text = "尚未取得新鲜状态，暂不展示旧统计；不能据此判断后台监测已停止。";
                    }
                    if (signalFresh && response.Noise is not null)
                        DisplayStatusText.Text = response.NoiseDisplay is { } display
                            ? display.Message + (display.ErrorCode is { } code ? $"（{code}）" : "")
                            : "当前后台不支持原生备用展示，请更新并重启后台。";
                    if (response.Outcome == "Rejected" && !_manualResultUnconfirmed) MessageText.Text = response.Message;
                }
            }
            catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or TimeoutException or OperationCanceledException or JsonException)
            { if (!_lifetime.IsCancellationRequested && operation == _uiOperation) Render(null); }
            try { await Task.Delay(500, _lifetime.Token); } catch (OperationCanceledException) { break; }
        }
    }
    private void SetDevices(IReadOnlyList<NoiseDevice> choices)
    {
        string? id = Microphone.SelectedValue as string ?? _state?.SelectedDeviceId ?? _state?.DeviceId;
        Microphone.ItemsSource = choices;
        Microphone.SelectedValue = id;
        // One available microphone is unambiguous; enumeration itself never starts capture.
        if (Microphone.SelectedIndex < 0 && choices.Count == 1) Microphone.SelectedIndex = 0;
    }
    private void UpdateButtons()
    {
        string? phase = _manual?.State;
        bool reservation = phase is "Waiting" or "Preparing";
        bool live = reservation || phase is "Starting" or "Active" or "Stopping";
        bool canArrange = phase is "Idle" or "Cancelled" or "Interrupted" or "Ended" or "Stopped";
        bool captureBusy = _state?.State is "Starting" or "Active" or "Stopping";
        bool needsDevice = _connected && canArrange && !captureBusy && Microphone.SelectedItem is not NoiseDevice;
        bool canReturn = live && _returnDisplay.Available;

        ManualHeading.Text = ManualStudyPresentation.Heading(_manual);
        ManualStatus.Text = ManualStudyPresentation.Details(_manual);
        if (_state?.State == "Idle")
            StateText.Text = !live && Microphone.SelectedItem is NoiseDevice selected ? $"监测麦克风　{selected.Name}" : "";
        var configurationVisibility = live ? Visibility.Collapsed : Visibility.Visible;
        if (ManualConfiguration.Visibility != configurationVisibility)
        {
            ManualConfiguration.Visibility = configurationVisibility;
            ArrangeManualPage(ManualPageScroll.ActualWidth);
        }
        ManualConfiguration.IsEnabled = !_busy;
        StartButton.Visibility = live ? Visibility.Collapsed : Visibility.Visible;
        StartButton.Content = UseStart.IsChecked == true ? "保存预约" : "开始自习";
        if (_pendingManualCommand is { Action: "start" } pending)
            StartButton.Content = pending.StartUtc.HasValue ? "正在保存预约…" : "正在启动…";
        StartButton.IsEnabled = _connected && !_busy && !_manualResultUnconfirmed && canArrange && !captureBusy && !needsDevice;
        StopButton.Visibility = live ? Visibility.Visible : Visibility.Collapsed;
        StopButton.Content = reservation ? "取消预约" : "结束手动自习";
        if (_pendingManualCommand?.Action == "end" || phase == "Stopping")
            StopButton.Content = reservation ? "正在取消预约…" : "正在结束…";
        StopButton.IsEnabled = _connected && !_busy && !_manualResultUnconfirmed && live && phase != "Stopping" && _manual?.SessionId is not null;
        ReturnStudyButton.Visibility = canReturn ? Visibility.Visible : Visibility.Collapsed;
        ReturnStudyButton.IsEnabled = _connected && !_busy && canReturn;
        ManualReportButton.Visibility = OpenWorkspaceReport is not null && phase is ("Ended" or "Stopped") ? Visibility.Visible : Visibility.Collapsed;
        ManualReportButton.IsEnabled = !_busy;
        ManualDeviceLink.Visibility = OpenWorkspaceDevices is null ? Visibility.Collapsed : Visibility.Visible;
        ManualCalibrationLink.Visibility = OpenWorkspaceCalibration is null ? Visibility.Collapsed : Visibility.Visible;
        ManualChooseDevice.Visibility = needsDevice ? Visibility.Visible : Visibility.Collapsed;
        string hint = "";
        if (!canArrange && !live && !_manualResultUnconfirmed) hint = "等待后台确认会话状态。";
        else if (needsDevice) hint = Microphone.HasItems ? "开始前请选择麦克风。" : "未找到可用麦克风，请连接设备后刷新。";
        else if (canArrange && captureBusy) hint = "采集设备正在使用中。";
        ManualActionHint.Text = hint;
        if (_manualResultUnconfirmed && !_busy) MessageText.Text = UnconfirmedManualResult;
        Microphone.IsEnabled = !_busy && !captureBusy && !live;
        RefreshButton.IsEnabled = !_busy && !captureBusy;
        SaveDeviceButton.IsEnabled = StartButton.IsEnabled;
    }
    private void ManualOptionsChanged(object sender, RoutedEventArgs e)
    {
        if (ManualActionHint is null || StopButton is null) return;
        ClearTimeValidationMessage();
        StartFields.Visibility = UseStart.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        EndFields.Visibility = UseEnd.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        ManualHideMinutes.IsEnabled = NoManualReturn.IsChecked != true;
        UpdateButtons();
    }
    private void ClearTimeValidationMessage()
    {
        if (_timeValidationMessage is null) return;
        if (MessageText.Text == _timeValidationMessage) MessageText.Text = "";
        _timeValidationMessage = null;
    }
    private void Render(NoiseState? state) => RenderWithCalibration(state, null);
    private void RenderWithCalibration(NoiseState? state, NoiseCalibrationStatus? calibration)
    {
        var label = NoiseCalibrationPresentation.Describe(calibration, state);
        CalibrationStatus.Text = label.Text; CalibrationStatus.ToolTip = label.Detail;
        CalibrationStatus.SetResourceReference(TextBlock.ForegroundProperty, label.Tone == "Warning" ? "TeacherWarningBrush" : "TeacherMutedBrush");
        _connected = state is not null;
        if (state is null)
        {
            _manual = null;
            _state = null;
            _returnDisplay.Observe(null);
            StateText.Text = "后台连接中断，当前监测状态未知。";
            DisplayStatusText.Text = "网页与备用展示状态未知，等待后台重新连接。";
            LevelText.Text = "—"; LevelBar.Value = -100; QualityText.Text = "未知";
            LevelHintText.Text = "";
            StateHintText.Text = "旧读数不代表当前状态。恢复连接后可查询或停止，请勿重复启动。";
            StateHintText.Visibility = _manualResultUnconfirmed ? Visibility.Collapsed : Visibility.Visible;
            if (!_manualResultUnconfirmed) MessageText.Text = "";
            SummaryTitle.Text = "监测统计（状态未知）";
            SummaryText.Text = "后台连接中断，暂不展示旧统计。恢复连接后重新读取；不能据此判断监测已经停止。";
            EstimateText.Text = "估算状态待确认";
            TrendCanvas.Children.Clear(); UpdateButtons();
            return;
        }
        _state = state;
        StateText.Text = state.Message;
        StateHintText.Text = "";
        StateHintText.Visibility = Visibility.Collapsed;
        LevelText.Text = NoiseSignalPresentation.Level(state.CurrentDbfs);
        EstimateText.Text = NoiseSignalPresentation.Estimated(state.Estimate?.CurrentDbA) + " · " + NoiseSignalPresentation.EstimateReason(state.Estimate) +
            (state.ReferenceNote is { } note ? "\n" + note : "");
        LevelBar.Value = Math.Clamp(state.CurrentDbfs ?? -100, -100, 0);
        QualityText.Text = NoiseSignalPresentation.Quality(state.Quality, state.CurrentDbfs);
        LevelHintText.Text = state.State == "Active" && state.Quality == "Good"
            ? NoiseSignalPresentation.Hint(state.CurrentDbfs) : "";
        if (state.State is "Stopped" or "Faulted") QualityText.Text = "已结束";
        SummaryTitle.Text = state.Summary is null ? "监测统计" : state.State switch
        {
            "Starting" or "Active" or "Stopping" => "本次监测累计统计",
            "Stopped" => "上次监测统计（已结束）",
            "Faulted" => "上次监测统计（采集中断）",
            _ => "后台返回的监测统计"
        };
        if (state.Summary is { } s)
            SummaryText.Text = $"总时长 {s.ElapsedSeconds:F1} 秒 · 有效采样 {s.SampledSeconds:F1} 秒 · 覆盖率 {s.Coverage:P0}\n" +
                $"能量平均 {FormatDb(s.EnergyMeanDbfs)} · 峰值 {FormatDb(s.PeakDbfs)} · 削波 {s.ClippedPercent:F2}%\n" +
                $"设备：{state.DeviceName ?? "正在打开"}";
        else SummaryText.Text = "开始后显示有效采样、覆盖率与能量平均电平。";
        if (state.Estimate is { } e)
            SummaryText.Text += $"\nA/{e.TimeWeighting} 平滑电平能量均值：{NoiseSignalPresentation.Estimated(e.Summary.EnergyMeanDbA)}\n" +
                $"最高平滑电平：{NoiseSignalPresentation.Estimated(e.Summary.MaximumDbA)} · 估算有效覆盖 {e.Summary.Coverage:P0}\n" +
                "参考版本：" + e.ProfileId[..12] + " · " + NoiseSignalPresentation.EstimateReason(e);
        DrawTrend(); UpdateButtons();
    }
    private static string FormatDb(double? value) => NoiseSignalPresentation.Statistic(value);
    private async void ReturnStudyClicked(object sender, RoutedEventArgs e)
    {
        if (!ReturnStudyButton.IsEnabled || _returnDisplay.Request(true) is not { } request) return;
        _busy = true; long operation = ++_uiOperation; UpdateButtons();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token); deadline.CancelAfter(4000);
        try
        {
            var reply = await HostClient.RequestAsync(_pipe, request, deadline.Token);
            if (_lifetime.IsCancellationRequested || operation != _uiOperation) return;
            _returnDisplay.Observe(reply.Study); MessageText.Text = reply.Message;
        }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or OperationCanceledException or TimeoutException or JsonException)
        { if (!_lifetime.IsCancellationRequested && operation == _uiOperation) { Render(null); MessageText.Text = "返回结果待确认，请等待最新状态后重试；后台监测继续。"; } }
        finally { _busy = false; UpdateButtons(); }
    }
    private void DrawTrend()
    {
        TrendCanvas.Children.Clear();
        if (!TrendCanvas.IsVisible || _state?.Summary is not { } summary || TrendCanvas.ActualWidth <= 0) return;
        double end = Math.Max(60, summary.ElapsedSeconds), start = end - 60;
        Polyline? line = null; double previous = -10;
        foreach (var point in _state.Trend)
        {
            if (point.Seconds < start) continue;
            if (point.Dbfs is null || point.Quality == "Invalid") { line = null; continue; }
            if (line is null || point.Seconds - previous > 1)
            {
                line = new Polyline { StrokeThickness = 2 };
                line.SetResourceReference(Shape.StrokeProperty, "TeacherAccentBrush");
                TrendCanvas.Children.Add(line);
            }
            line.Points.Add(new((point.Seconds - start) / 60 * TrendCanvas.ActualWidth,
                (0 - Math.Clamp(point.Dbfs.Value, -100, 0)) / 100 * TrendCanvas.ActualHeight));
            previous = point.Seconds;
        }
    }
    private void TrendSizeChanged(object sender, SizeChangedEventArgs e) { if (_connected) DrawTrend(); }
    private async Task RunAsync(string capability, NoiseCommand? command = null)
    {
        if (_busy) return;
        _busy = true; long operation = ++_uiOperation; UpdateButtons();
        try
        {
            var response = await RequestAsync(capability, command);
            if (_lifetime.IsCancellationRequested || operation != _uiOperation) return;
            if (response.NoiseDevices is not null)
            {
                SetDevices(response.NoiseDevices);
                MessageText.Text = response.NoiseDevices.Count == 0 ? "没有可用麦克风，请连接后刷新。" : "设备列表已更新。";
            }
            else MessageText.Text = response.Message;
            Render(response.Noise);
        }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or TimeoutException or OperationCanceledException or JsonException)
        { if (!_lifetime.IsCancellationRequested && operation == _uiOperation) Render(null); }
        finally { _busy = false; UpdateButtons(); }
    }
    private async void RefreshClicked(object sender, RoutedEventArgs e) => await RunAsync("noise.devices");
    private void ChooseManualDeviceClicked(object sender, RoutedEventArgs e)
    {
        ManualDeviceSettings.IsExpanded = true;
        ManualDeviceSettings.UpdateLayout();
        Control target = Microphone.Items.Count == 0 ? RefreshButton : Microphone;
        target.BringIntoView();
        target.Focus();
    }
    private void ManualDevicesClicked(object sender, RoutedEventArgs e) => OpenWorkspaceDevices?.Invoke();
    private void ManualCalibrationClicked(object sender, RoutedEventArgs e) => OpenWorkspaceCalibration?.Invoke();
    private void ManualReportClicked(object sender, RoutedEventArgs e)
    {
        if (_connected && !_busy && _manual?.State is "Ended" or "Stopped") OpenWorkspaceReport?.Invoke();
    }
    private async void SaveDeviceClicked(object sender, RoutedEventArgs e)
    {
        if (_state is { } state && Microphone.SelectedItem is NoiseDevice device)
            await RunAsync("noise.command", new("select", state.InstanceId, state.Revision, device.Id));
    }
    private async void StartClicked(object sender, RoutedEventArgs e)
    {
        if (!StartButton.IsEnabled || _manual is not { } m || Microphone.SelectedItem is not NoiseDevice device) return;
        DateTimeOffset? start = null, end = null;
        int? hideSeconds = null;
        try
        {
            if (UseStart.IsChecked == true) start = ReadLocalTime(ManualStartDate, ManualStartTime.Text);
            if (UseEnd.IsChecked == true) end = ReadLocalTime(ManualEndDate, ManualEndTime.Text);
            if (NoManualReturn.IsChecked != true)
            {
                if (!int.TryParse(ManualHideMinutes.Text, out int minutes) || minutes is < 1 or > 180) throw new FormatException();
                hideSeconds = minutes * 60;
            }
        }
        catch (Exception error) when (error is FormatException or ArgumentException)
        {
            _timeValidationMessage = MessageText.Text = "请选择有效日期，时间填写为 HH:mm，返回时长为 1–180 分钟。";
            return;
        }
        await ManualCommandAsync(new("start", m.InstanceId, m.Revision, DeviceId: device.Id, StartUtc: start, EndUtc: end, HideSeconds: hideSeconds));
    }
    private static DateTimeOffset ReadLocalTime(DatePicker picker, string text)
    {
        if (picker.SelectedDate is not { } date || !TimeSpan.TryParseExact(text.Trim(), @"hh\:mm", System.Globalization.CultureInfo.InvariantCulture, out var time))
            throw new FormatException();
        return new DateTimeOffset(DateTime.SpecifyKind(date.Date + time, DateTimeKind.Local)).ToUniversalTime();
    }
    private async void StopClicked(object sender, RoutedEventArgs e)
    {
        if (StopButton.IsEnabled && _manual is { SessionId: not null } m)
            await ManualCommandAsync(new("end", m.InstanceId, m.Revision, m.SessionId));
    }
    private async Task ManualCommandAsync(ManualStudyCommand command)
    {
        if (_busy) return;
        _busy = true;
        long operation = ++_uiOperation;
        bool Current() => !_lifetime.IsCancellationRequested && operation == _uiOperation && WorkspacePanel.Visible(this);
        _timeValidationMessage = null;
        MessageText.Text = command.Action == "start" ? "正在核对发起手动自习的权限…" : "";
        _returnDisplay.Observe(null);
        UpdateButtons();
        try
        {
            var request = new HostRequest(Protocol.Version, Guid.NewGuid(), "manual.study.command", ManualStudy: command);
            if (command.Action == "start")
            {
                string? blocked = null;
                var authorized = await NoiseManagementDialog.AuthorizeAsync(WorkspacePanel.DialogOwner(this), _pipe, request,
                    reportBlocked: message => blocked = message, stillCurrent: Current);
                if (!Current()) return;
                if (authorized is null)
                {
                    MessageText.Text = blocked ?? (command.StartUtc.HasValue ? "预约未提交。" : "本次开始请求未提交。");
                    return;
                }
                request = authorized;
            }
            _pendingManualCommand = command;
            UpdateButtons();
            MessageText.Text = "";
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
            deadline.CancelAfter(5000);
            _manualResultUnconfirmed = true;
            var reply = await HostClient.RequestAsync(_pipe, request, deadline.Token);
            if (!Current()) return;
            if (reply.Outcome == "Succeeded" && (reply.ManualStudy is null || reply.Noise is null))
            {
                Render(null);
                return;
            }
            _manualResultUnconfirmed = false;
            if (reply.ManualStudy is not null) _manual = reply.ManualStudy;
            if (reply.Noise is not null) Render(reply.Noise);
            // Successful commands already have a phase, time and reason in the session panel.
            MessageText.Text = reply.Outcome == "Succeeded" && reply.ManualStudy is not null ? "" : reply.Message;
            _timeValidationMessage = reply.ErrorCode == "MANUAL_TIME_INVALID" ? reply.Message : null;
        }
        catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException or TimeoutException or JsonException or OperationCanceledException)
        {
            if (Current())
            {
                Render(null);
                if (!_manualResultUnconfirmed) MessageText.Text = "无法核实发起权限。请检查后台连接后重新提交。";
            }
        }
        finally
        {
            _pendingManualCommand = null;
            _busy = false;
            UpdateButtons();
        }
    }
    private async void ManagementClicked(object sender, RoutedEventArgs e)
    {
        try { await NoiseManagementDialog.ConfigureAsync(WorkspacePanel.DialogOwner(this), _pipe); }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or TimeoutException or OperationCanceledException or JsonException)
        { MessageText.Text = "无法读取后台管理设置，请重试。"; }
    }
}

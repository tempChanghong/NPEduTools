using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Shapes;
using NPEduTools.Contracts;
using NPEduTools.Core;

namespace NPEduTools.App;

public partial class NoiseWindow : Window
{
    private readonly string _pipe;
    private readonly CancellationTokenSource _lifetime = new();
    private NoiseState? _state;
    private ManualStudyState? _manual;
    private bool _closing, _busy, _connected;
    private long _uiOperation;
    private readonly NoiseCalibrationRead _calibrationRead = new();
    public NoiseWindow(string pipe)
    {
        _pipe = pipe; InitializeComponent();ManualStartDate.SelectedDate=ManualEndDate.SelectedDate=DateTime.Today;
        DesktopDialogSizing.Attach(this, pipe);
        Closing += (_, args) => { if (!_closing) { args.Cancel = true; Hide(); } };
        Closed += (_, _) => _lifetime.Cancel();
        Microphone.SelectionChanged += (_, _) => UpdateButtons();
        ManualOptionsChanged(this, new RoutedEventArgs());
        _ = PollAsync();
    }
    public void Shutdown() { _closing = true; Close(); }
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
                ? $"{guard.Message} 恢复次数：{guard.Restarts}/3。" : "守护未连接或状态已陈旧；不会据此判断监测已停止。";
            try
            {
                if (!_busy && (WorkspacePanel.Visible(this) || !loadedDevices))
                {
                    long calibrationAt = System.Diagnostics.Stopwatch.GetTimestamp();
                    var response = await RequestAsync(loadedDevices ? "noise.status" : "noise.devices");
                    if (_lifetime.IsCancellationRequested || operation != _uiOperation) continue;
                    if (response.NoiseDevices is not null) { _state = response.Noise; SetDevices(response.NoiseDevices); loadedDevices = true; }
                    using var manualTimeout=CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);manualTimeout.CancelAfter(TimeSpan.FromSeconds(4));
                    var manual=await HostClient.RequestAsync(_pipe,new HostRequest(1,Guid.NewGuid(),"manual.study.status"),manualTimeout.Token);
                    if(_lifetime.IsCancellationRequested || operation!=_uiOperation)continue;
                    _manual=manual.ManualStudy;
                    ManualStatus.Text = _manual is { } m ? ManualStateText(m) : "手动会话状态待确认，不能据此判断已结束。";
                    RenderWithCalibration(response.Noise, System.Diagnostics.Stopwatch.GetElapsedTime(calibrationAt).TotalSeconds < 3.5
                        ? response.NoiseCalibrationStatus : null);
                    if (response.Noise is not null)
                        DisplayStatusText.Text = response.NoiseDisplay is { } display
                            ? display.Message + (display.ErrorCode is { } code ? $"（{code}）" : "")
                            : "当前后台不支持原生备用展示，请更新并重启后台。";
                    if (response.Outcome == "Rejected") MessageText.Text = response.Message;
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
        bool manualRunning = _manual?.State is "Waiting" or "Preparing" or "Starting" or "Active" or "Stopping";
        ManualConfiguration.Visibility = manualRunning ? Visibility.Collapsed : Visibility.Visible;
        ManualHeading.Text = manualRunning ? "当前手动自习" : "开始一节手动自习";
        StartButton.Visibility = manualRunning ? Visibility.Collapsed : Visibility.Visible;
        StopButton.Visibility = manualRunning ? Visibility.Visible : Visibility.Collapsed;
        StartButton.Content = UseStart.IsChecked == true ? "保存预约" : "开始自习";
        StopButton.Content = _manual?.State is "Waiting" or "Preparing" ? "取消预约" : "结束自习";
        bool running = _state?.State is "Starting" or "Active" or "Stopping";
        StartButton.IsEnabled = _manual is not null && _manual.State is not ("Waiting" or "Preparing" or "Starting" or "Active" or "Stopping") && _connected && !_busy && !running && Microphone.SelectedItem is NoiseDevice;
        SaveDeviceButton.IsEnabled = StartButton.IsEnabled;
        StopButton.IsEnabled = _connected && !_busy && _manual?.State is "Waiting" or "Preparing" or "Starting" or "Active";
        Microphone.IsEnabled = RefreshButton.IsEnabled = !_busy && !running;
    }
    private static string ManualStateText(ManualStudyState state)
    {
        string phase = state.State switch { "Waiting" => "等待预约开始", "Preparing" => "准备自习（尚未采集）",
            "Starting" => "正在启动，等待采样确认", "Active" => "手动会话已启动，实际采集见下方状态",
            "Stopping" => "正在结束", "Stopped" => "已结束", _ => state.Reason ?? "当前没有手动会话" };
        return phase + (state.StartUtc is { } start ? $"\n开始：{start.ToLocalTime():yyyy-MM-dd HH:mm}" : "") +
            (state.EndUtc is { } end ? $"\n结束：{end.ToLocalTime():yyyy-MM-dd HH:mm}" : "") +
            (state.Reason is { } reason ? "\n" + reason : "");
    }
    private void ManualOptionsChanged(object sender, RoutedEventArgs e)
    {
        if (StartFields is null) return;
        StartFields.Visibility = UseStart.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        EndFields.Visibility = UseEnd.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        ManualHideMinutes.IsEnabled = NoManualReturn.IsChecked != true;
        UpdateButtons();
    }
    private void Render(NoiseState? state) => RenderWithCalibration(state, null);
    private void RenderWithCalibration(NoiseState? state, NoiseCalibrationStatus? calibration)
    {
        var label = NoiseCalibrationPresentation.Describe(calibration, state);
        CalibrationStatus.Text = label.Text; CalibrationStatus.ToolTip = label.Detail;
        CalibrationStatus.Foreground = label.Tone == "Warning" ? Brushes.Sienna : new SolidColorBrush(Color.FromRgb(97, 120, 107));
        _connected = state is not null;
        if (state is null)
        {
            StateText.Text = "后台连接中断，当前监测状态未知。";
            DisplayStatusText.Text = "网页与备用展示状态未知，等待后台重新连接。";
            LevelText.Text = "—"; LevelBar.Value = -100; QualityText.Text = "未知";
            LevelHintText.Text = "";
            StateHintText.Text = "旧读数不代表当前状态。恢复连接后可查询或停止，请勿重复启动。";
            StateHintText.Visibility = Visibility.Visible;
            MessageText.Text = "";
            SummaryTitle.Text = "监测统计（状态未知）";
            SummaryText.Text = "后台连接中断，暂不展示旧统计。恢复连接后重新读取；不能据此判断监测已经停止。";
            EstimateText.Text = "估算状态待确认";
            TrendCanvas.Children.Clear(); UpdateButtons();
            ManualStatus.Text = "手动会话状态未知，不能据此判断已结束。";
            ManualHeading.Text = "后台状态待确认";
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
    private void DrawTrend()
    {
        TrendCanvas.Children.Clear();
        if (_state?.Summary is not { } summary || TrendCanvas.ActualWidth <= 0) return;
        double end = Math.Max(60, summary.ElapsedSeconds), start = end - 60;
        Polyline? line = null; double previous = -10;
        foreach (var point in _state.Trend)
        {
            if (point.Seconds < start) continue;
            if (point.Dbfs is null || point.Quality == "Invalid") { line = null; continue; }
            if (line is null || point.Seconds - previous > 1)
            {
                line = new Polyline { Stroke = new SolidColorBrush(Color.FromRgb(20, 125, 104)), StrokeThickness = 2 };
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
    private async void SaveDeviceClicked(object sender, RoutedEventArgs e)
    {
        if (_state is { } state && Microphone.SelectedItem is NoiseDevice device)
            await RunAsync("noise.command", new("select", state.InstanceId, state.Revision, device.Id));
    }
    private async void StartClicked(object sender, RoutedEventArgs e)
    {
        if(_manual is not {} m || Microphone.SelectedItem is not NoiseDevice device)return;
        DateTimeOffset? start=null,end=null;int? hide=null;
        try {
            DateTimeOffset Parse(System.Windows.Controls.DatePicker picker,string text){if(picker.SelectedDate is not {} d || !TimeSpan.TryParseExact(text.Trim(),@"hh\:mm",System.Globalization.CultureInfo.InvariantCulture,out var t))throw new FormatException();return new DateTimeOffset(DateTime.SpecifyKind(d.Date+t,DateTimeKind.Local)).ToUniversalTime();}
            if(UseStart.IsChecked==true)start=Parse(ManualStartDate,ManualStartTime.Text);
            if(UseEnd.IsChecked==true)end=Parse(ManualEndDate,ManualEndTime.Text);
            if(NoManualReturn.IsChecked!=true){if(!int.TryParse(ManualHideMinutes.Text,out var minutes) || minutes is <1 or >180)throw new FormatException();hide=minutes*60;}
        } catch(FormatException){MessageText.Text="请选择日期，时间使用 HH:mm；返回时长为 1–180 分钟。";return;}
        await ManualCommandAsync(new("start",m.InstanceId,m.Revision,DeviceId:device.Id,StartUtc:start,EndUtc:end,HideSeconds:hide));
    }
    private async void StopClicked(object sender, RoutedEventArgs e)
    {
        if(_manual is {} m)await ManualCommandAsync(new("end",m.InstanceId,m.Revision,m.SessionId));
    }
    private async Task ManualCommandAsync(ManualStudyCommand command)
    {
        if(_busy)return;_busy=true;long operation=++_uiOperation;UpdateButtons();
        try{using var deadline=CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);deadline.CancelAfter(5000);var reply=await HostClient.RequestAsync(_pipe,new HostRequest(1,Guid.NewGuid(),"manual.study.command",ManualStudy:command),deadline.Token);if(_lifetime.IsCancellationRequested || operation!=_uiOperation)return;_manual=reply.ManualStudy;ManualStatus.Text=_manual is {} m?ManualStateText(m):"手动会话状态待确认。";MessageText.Text=reply.Message;Render(reply.Noise);}
        catch(Exception e)when(e is IOException or InvalidDataException or UnauthorizedAccessException or TimeoutException or JsonException or OperationCanceledException){if(!_lifetime.IsCancellationRequested && operation==_uiOperation){Render(null);MessageText.Text="请求结果待确认；请等待后台状态恢复，不重复启动。";}}
        finally{_busy=false;UpdateButtons();}
    }
    private async void ManagementClicked(object sender, RoutedEventArgs e)
    {
        try { await NoiseManagementDialog.ConfigureAsync(WorkspacePanel.DialogOwner(this), _pipe); }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or TimeoutException or OperationCanceledException or JsonException)
        { MessageText.Text = "无法读取后台管理设置，请重试。"; }
    }
}

using System.IO;
using System.Windows;
using System.Windows.Controls;
using NPEduTools.Contracts;

namespace NPEduTools.App;

public partial class StudyDevicePicker : UserControl
{
    public event EventHandler? SelectionSaved;
    public event EventHandler? StatusChanged;
    public string Summary => !_connected ? "设备状态待刷新" :
        (_noise?.SelectedDeviceId is not {} id ? "麦克风待配置" : _microphones.Any(d => d.Id == id) ? "麦克风已配置" : "已选麦克风不可用") + " · " +
        (CameraReadError ?? (_camera is null ? "摄像头状态待确认" : _camera.CameraId is null ? "未使用摄像头" : _camera.CameraAvailable == true ? "摄像头已配置" : "已选摄像头待确认")) +
        (_camera?.Locked == true ? " · 学校已锁定" : "");
    private string? _pipe;
    private NoiseState? _noise;
    private StudyDeviceState? _camera;
    private CancellationTokenSource? _operation;
    private Task? _refresh;
    private long _generation;
    private bool _busy, _rendering, _connected;
    private NoiseDevice[] _microphones = [];
    private readonly NoiseCalibrationRead _calibrationRead = new();
    public StudyDevicePicker()
    {
        InitializeComponent();
        IsVisibleChanged += async (_, _) => { if (!IsVisible) CancelPending(); else await RefreshAsync(); };
        Unloaded += (_, _) => CancelPending();
        UpdateButtons();
    }
    public void Bind(string pipe) { if (_pipe != pipe) { CancelPending(); _pipe = pipe; _noise = null; _camera = null; _connected = false; } }
    public void CancelPending()
    {
        EquipmentSecret.Clear();
        CalibrationStatus.Text = "校准状态待刷新"; CalibrationStatus.ToolTip = null;
        if (_busy) DeviceMessage.Text = "设备操作结果待确认，请刷新核对。";
        _connected = false; _refresh = null;
        _generation++; _operation?.Cancel(); _operation?.Dispose(); _operation = null; _busy = false; UpdateButtons();
    }
    private CancellationToken Begin()
    {
        EquipmentSecret.Clear();
        _operation?.Cancel(); _operation?.Dispose(); _operation = new(); _operation.CancelAfter(6000);
        _busy = true; _generation++; DeviceMessage.Text = "";
        DeviceMessage.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty, "TeacherWarningBrush");
        UpdateButtons(); return _operation.Token;
    }
    public Task RefreshAsync()
    {
        // Visibility, initial load and OOBE readiness may ask together. They must
        // await the same fresh read, not treat an in-flight refresh as completed.
        if (_refresh is { IsCompleted: false }) return _refresh;
        if (_pipe is null || _busy) return Task.CompletedTask;
        return _refresh = RefreshCoreAsync(_pipe);
    }
    private async Task RefreshCoreAsync(string pipe)
    {
        var token = Begin(); long generation = _generation;
        // Preserve edits, not the last rendered saved value. Another settings client
        // may have changed the Host preference since this picker last refreshed.
        string? draftMic = StudyMicrophone.SelectedValue is string mic && mic != _noise?.SelectedDeviceId ? mic : null;
        string? draftCamera = StudyCamera.SelectedValue is string cam && cam != (_camera?.CameraId ?? "") ? cam : null;
        try
        {
            long calibrationAt = System.Diagnostics.Stopwatch.GetTimestamp();
            var microphones = await _calibrationRead.RequestAsync(pipe, new(1, Guid.NewGuid(), "noise.devices"), token);
            if (generation != _generation) return;
            if (microphones.Outcome != "Succeeded" || microphones.Noise is not { } microphoneState ||
                microphoneState.InstanceId == Guid.Empty || microphoneState.Revision < 0 || microphones.NoiseDevices is null ||
                microphones.NoiseDevices.Any(device => device is null)) throw new InvalidDataException("麦克风配置回执不完整。");
            var cameras = await HostClient.RequestAsync(pipe, "study.devices.refresh-microphone-policy", token);
            if (generation != _generation) return;
            bool legacyHost = cameras is { Outcome: "Rejected", ErrorCode: "UnknownCapability" };
            if (legacyHost) cameras = await HostClient.RequestAsync(pipe, "study.devices.refresh", token);
            if (generation != _generation) return;
            if (cameras.StudyDevices is not { } cameraState ||
                cameraState.InstanceId == Guid.Empty || cameraState.Revision < 0 || cameraState.Cameras is null ||
                cameraState.Cameras.Any(device => device is null) || cameraState.MicrophoneChangeToken == Guid.Empty ||
                cameraState.MicrophoneChangeToken.HasValue && (legacyHost || !cameraState.Locked))
                throw new InvalidDataException("摄像头与设备锁定状态未确认。");
            // A camera-only read failure can still carry an explicit current microphone grant.
            bool cameraOnlyFailure = !legacyHost && cameraState.Locked && cameraState.MicrophoneChangeToken.HasValue &&
                cameras is { Outcome: "Rejected", ErrorCode: "CameraDevicesUnavailable" or "CameraConfigurationUnavailable" } &&
                cameras.ErrorCode == cameraState.Error && (cameraState.CameraId is null || NoiseContract.ValidDeviceId(cameraState.CameraId)) &&
                cameraState.Cameras.All(device => NoiseContract.ValidDeviceId(device.Id) &&
                    !string.IsNullOrWhiteSpace(device.Name) && device.Name.Length <= 512 && !device.Name.Any(char.IsControl));
            if (cameras.Outcome != "Succeeded" && !cameraOnlyFailure)
                throw new InvalidDataException("后台未确认本机设备策略。");
            _noise = microphoneState; _microphones = DistinguishMicrophones(microphones.NoiseDevices); _camera = cameraState;
            var calibration = NoiseCalibrationPresentation.Describe(microphones.NoiseCalibrationStatus, microphones.Noise,
                System.Diagnostics.Stopwatch.GetElapsedTime(calibrationAt).TotalSeconds < 3.5);
            CalibrationStatus.Text = calibration.Text; CalibrationStatus.ToolTip = calibration.Detail;
            _connected = true; _rendering = true;
            var micList = _microphones.ToList();
            if (_noise.SelectedDeviceId is { } saved && !micList.Any(d => d.Id == saved)) micList.Add(new(saved, "已保存的麦克风 · 当前不可用"));
            StudyMicrophone.ItemsSource = micList;
            StudyMicrophone.SelectedValue = draftMic is not null && micList.Any(d => d.Id == draftMic) ? draftMic : _noise.SelectedDeviceId;
            var camList = new List<StudyCameraDevice> { new("", "暂不使用摄像头") };
            var catalog = _camera?.Cameras ?? [];
            foreach (var device in catalog)
            {
                var same = catalog.Where(d => d.Name == device.Name).ToArray();
                camList.Add(device with { Name = same.Length > 1 ? $"{device.Name}（{Array.FindIndex(same, d => d.Id == device.Id) + 1}）" : device.Name });
            }
            if (_camera?.CameraId is { } cameraId && !camList.Any(d => d.Id == cameraId)) camList.Add(new(cameraId, "已保存的摄像头 · 当前不可用"));
            StudyCamera.ItemsSource = camList;
            StudyCamera.SelectedValue = draftCamera is not null && camList.Any(d => d.Id == draftCamera) ? draftCamera : _camera?.CameraId ?? "";
            _rendering = false; RenderStatus();
            if (!cameraOnlyFailure && _camera?.Error is { } error) DeviceMessage.Text = DeviceError(error);
        }
        catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException or OperationCanceledException or TimeoutException or System.Text.Json.JsonException)
        {
            if (generation != _generation) return;
            _connected = false; DeviceMessage.Text = "设备状态暂不可用，请检查后台连接后刷新；旧列表不能用于保存。";
            CalibrationStatus.Text = "校准状态待确认"; CalibrationStatus.ToolTip = null;
            MicrophoneStatus.Text = "麦克风状态待重新读取"; CameraStatus.Text = "摄像头状态待重新读取";
        }
        finally { if (generation == _generation) { _rendering = false; _busy = false; UpdateButtons(); } }
    }
    private void RenderStatus()
    {
        MicrophoneStatus.Text = _noise?.SelectedDeviceId is not { } id ? "尚未保存监测麦克风" :
            _microphones.FirstOrDefault(d => d.Id == id) is { } mic ? "已保存：" + mic.Name : "已保存的麦克风当前不可用；原选择保留";
        CameraStatus.Text = CameraReadError ?? (_camera is null ? "当前后台尚不支持摄像头选择" : _camera.CameraId is null ? "摄像头未选择 · 不影响基本自习监测" :
            _camera.CameraAvailable == true ? "已保存：" + _camera.CameraName + " · 未开启捕获" : "摄像头选择已保存，可用性未确认或设备已断开");
        if (_camera?.Locked == true) CameraStatus.Text += " · 学校已锁定";
    }
    private string? CameraReadError => _camera?.Error switch
    {
        "CameraConfigurationUnavailable" => "无法读取摄像头配置",
        "CameraDevicesUnavailable" => "无法读取摄像头列表",
        _ => null
    };
    private bool Idle => _noise?.State is "Idle" or "Stopped" or "Faulted";
    private void UpdateButtons()
    {
        if (DeviceRefresh is null) return;
        DeviceRefresh.IsEnabled = !_busy;
        bool locked = _camera?.Locked == true;
        bool allowed = _camera?.MicrophoneChangeToken is { } policyToken && policyToken != Guid.Empty;
        bool editable = !_busy && _connected && Idle;
        bool alternative = _microphones.Any(d => d.Id != _noise?.SelectedDeviceId);
        bool chosen = StudyMicrophone.SelectedValue is string target && target != _noise?.SelectedDeviceId &&
            _microphones.Any(d => d.Id == target);
        EquipmentLockPanel.Visibility = locked ? Visibility.Visible : Visibility.Collapsed;
        SaveMicrophone.Visibility = locked ? Visibility.Collapsed : Visibility.Visible;
        StudyMicrophone.IsEnabled = editable && (!locked || allowed && alternative);
        StudyCamera.IsEnabled = editable && !locked;
        SchoolMicrophoneForm.Visibility = locked && _connected && allowed && Idle && chosen ? Visibility.Visible : Visibility.Collapsed;
        EquipmentSecret.IsEnabled = ConfirmSchoolMicrophone.IsEnabled = editable && locked && allowed && chosen;
        if (locked)
            SchoolMicrophoneHint.Text = !_connected ? "学校设备策略待刷新。"
                : !allowed ? "学校已锁定采集设备，当前策略未开放本班现场更换麦克风。"
                : !Idle ? "当前正在采集或释放设备。结束后请刷新，再选择麦克风。"
                : !alternative ? "学校已锁定采集设备，当前没有其他可用麦克风。"
                : chosen ? "核对所选麦克风后，输入班级管理口令确认本次更换。新麦克风可能需要单独校准。"
                : "请选择另一支可用麦克风，再用班级管理口令确认更换。摄像头继续锁定。";
        SaveMicrophone.IsEnabled = !_busy && _connected && Idle && !locked && StudyMicrophone.SelectedValue is string mic && _microphones.Any(d => d.Id == mic);
        SaveCamera.IsEnabled = !_busy && _connected && Idle && !locked && _camera is not null && _camera.Error is null &&
            StudyCamera.SelectedValue is string cam && (cam.Length == 0 || _camera.Cameras.Any(d => d.Id == cam));
        if (!_connected)
        {
            CalibrationStatus.Text = "校准状态待确认"; CalibrationStatus.ToolTip = null;
            MicrophoneStatus.Text = "麦克风状态待重新读取";
            CameraStatus.Text = "摄像头状态待重新读取";
        }
        StatusChanged?.Invoke(this, EventArgs.Empty);
    }
    public async Task<bool> CheckReadyAsync()
    {
        await RefreshAsync();
        if (_busy || !_connected || _noise?.SelectedDeviceId is not { } id || !_microphones.Any(d => d.Id == id))
        { DeviceMessage.Text = "请保存当前可用的监测麦克风，或选择跳过此项。"; return false; }
        // A read-only draft cannot be saved and must not block OOBE.
        if ((_camera?.Locked != true || _camera.MicrophoneChangeToken.HasValue) && StudyMicrophone.SelectedValue as string != id ||
            _camera is { Locked: false } && StudyCamera.SelectedValue as string != (_camera.CameraId ?? ""))
        { DeviceMessage.Text = _camera?.Locked == true ? "请先确认更换麦克风，或重新选择已保存的麦克风。" : "设备选择尚未保存，请先点击对应保存按钮；摄像头也可以暂不使用。"; return false; }
        if (_camera?.Locked == true) DeviceMessage.Text = "学校已锁定设备，将按后台已保存的当前配置完成准备。";
        return true;
    }
    private async void SaveMicrophoneClicked(object sender, RoutedEventArgs e)
    {
        if (!SaveMicrophone.IsEnabled || _pipe is null || _noise is null || StudyMicrophone.SelectedValue is not string id) return;
        var command = new NoiseCommand("select", _noise.InstanceId, _noise.Revision, id);
        await SaveAsync(new(1, Guid.NewGuid(), "study.devices.microphone", Noise: command), false);
    }
    private async void SaveCameraClicked(object sender, RoutedEventArgs e)
    {
        if (!SaveCamera.IsEnabled || _pipe is null || _camera is null || StudyCamera.SelectedValue is not string id) return;
        var command = new StudyCameraCommand(id.Length == 0 ? "clear" : "select", _camera.InstanceId, _camera.Revision, id.Length == 0 ? null : id);
        await SaveAsync(new(1, Guid.NewGuid(), "study.devices.select", StudyCamera: command), true);
    }
    private async Task SaveAsync(HostRequest request, bool camera)
    {
        var token = Begin(); long generation = _generation;
        try
        {
            var response = await HostClient.RequestAsync(_pipe!, request, token);
            if (generation != _generation) return;
            if (response.Outcome == "Succeeded")
            {
                bool confirmed = camera
                    ? request.StudyCamera is { } cameraCommand && response.StudyDevices is { Error: null } cameraResult &&
                        cameraResult.InstanceId == cameraCommand.InstanceId && cameraResult.Revision >= cameraCommand.Revision &&
                        cameraResult.CameraId == cameraCommand.DeviceId
                    : request.Noise is { } microphoneCommand && response.Noise is { } microphoneResult &&
                        microphoneResult.InstanceId == microphoneCommand.InstanceId && microphoneResult.Revision >= microphoneCommand.Revision &&
                        microphoneResult.SelectedDeviceId == microphoneCommand.DeviceId;
                if (!confirmed) throw new InvalidDataException("后台尚未确认本次设备选择。");
            }
            if (response.Noise is { } noise) _noise = noise;
            if (response.StudyDevices is { } devices) _camera = devices;
            if (response.Outcome != "Succeeded")
            {
                // A rejection may carry only one device snapshot. A new lock,
                // capture owner or Host revision invalidates the combined editability claim.
                if (response.ErrorCode is "EQUIPMENT_LOCKED" or "CameraStateChanged" or "NoiseStateChanged" or "StudyDevicesBusy" or "NoiseBusy" or "NoiseCleanupRequired" or "NoiseStopping")
                    _connected = false;
                DeviceMessage.Text = DeviceError(response.ErrorCode);
            }
            else
            {
                DeviceMessage.Text = camera ? "摄像头选择已保存，未开始捕获。" : "监测麦克风已保存，未开始采集。";
                if (!camera) { CalibrationStatus.Text = "校准状态待刷新"; CalibrationStatus.ToolTip = "麦克风选择已保存，请刷新核对校准状态。"; }
                DeviceMessage.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty, "TeacherSuccessBrush");
                SelectionSaved?.Invoke(this, EventArgs.Empty);
            }
            RenderStatus();
        }
        catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException or OperationCanceledException or TimeoutException or System.Text.Json.JsonException)
        { if (generation == _generation) { _connected = false; DeviceMessage.Text = "保存结果未确认，请刷新核对；不会自动重新保存或开始采集。"; } }
        finally { if (generation == _generation) { _busy = false; UpdateButtons(); } }
    }
    private static string DeviceError(string? code) => code switch
    {
        "CameraStateChanged" or "NoiseStateChanged" => "设备配置已变化，请刷新核对后再保存。",
        "CameraDeviceUnavailable" => "所选摄像头已断开，请刷新；没有自动切换设备。",
        "NoiseDeviceUnavailable" => "所选麦克风已断开，请刷新；没有自动切换设备。",
        "CameraConfigurationUnavailable" or "NoiseConfigurationUnavailable" => "设备配置无法读取或保存，请检查本机配置目录；原文件保留。",
        "CameraDevicesUnavailable" => "无法枚举摄像头，请检查 Windows 设备状态后刷新。",
        "EQUIPMENT_LOCKED" => "学校已锁定采集设备。请刷新核对本班现场更换权限。",
        "EQUIPMENT_POLICY_CHANGED" => "学校策略已变化，请刷新后核对本次更换权限和设备。",
        "EQUIPMENT_PENDING" => "设备正在采集或切换，请等待结束后刷新。",
        "EQUIPMENT_UNAVAILABLE" => "所选设备已断开或清单不可用，请刷新后重新选择。",
        "EQUIPMENT_STORE_UNAVAILABLE" => "本机无法保存设备设置，请刷新核对后再试。",
        "EQUIPMENT_CHANGE_UNCONFIRMED" => "更换请求已记录，当前麦克风仍待确认，请刷新核对。",
        "MANAGEMENT_SECRET_INVALID" or "INVALID_MANAGEMENT_SECRET" => "班级管理口令不正确，请核对后重新输入。",
        "MANAGEMENT_RATE_LIMITED" => "验证次数过多，请稍后再试。",
        "MANAGEMENT_NOT_CONFIGURED" or "MANAGEMENT_STORE_UNAVAILABLE" or "SCHOOL_MANAGEMENT_REQUIRED" => "本班管理口令暂不可用，请核对学校互联和班级配置。",
        "StudyDevicesBusy" or "NoiseBusy" => "当前有采集或设备尚未释放，不能更换设备；请等待结束后刷新。",
        "NoiseCleanupRequired" => "上次麦克风释放未确认；请重启后台后刷新，再设置设备。",
        "NoiseStopping" => "后台正在退出；请等待重新连接后刷新，当前设备选择保留。",
        "STUDY_UNSUPPORTED" or "UnknownCapability" => "请更新并重启 Host，以使用原生摄像头选择。",
        _ => "设备设置未确认，请刷新后重试。"
    };
    private static NoiseDevice[] DistinguishMicrophones(IReadOnlyList<NoiseDevice> devices) => devices.Select(device =>
    {
        var same = devices.Where(d => d.Name == device.Name).ToArray();
        return device with { Name = same.Length > 1 ? $"{device.Name}（{Array.FindIndex(same, d => d.Id == device.Id) + 1}）" : device.Name };
    }).ToArray();
    private void ChoiceChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_rendering) return;
        EquipmentSecret?.Clear();
        UpdateButtons();
    }
    private async void RefreshClicked(object sender, RoutedEventArgs e) => await RefreshAsync();
    private async void ConfirmSchoolMicrophoneClicked(object sender, RoutedEventArgs e)
    {
        if (!ConfirmSchoolMicrophone.IsEnabled || _pipe is null || _noise is null ||
            _camera?.MicrophoneChangeToken is not { } policyToken || StudyMicrophone.SelectedValue is not string target) return;
        string secret = EquipmentSecret.Password;
        EquipmentSecret.Clear();
        if (!NoiseManagementContract.SecretValid(secret))
        {
            DeviceMessage.Text = "请输入 8 至 128 位班级管理口令。"; EquipmentSecret.Focus(); return;
        }
        var instance = _noise.InstanceId;
        var token = Begin(); long generation = _generation;
        var command = new HostRequest(1, Guid.NewGuid(), "noise.management.command",
            NoiseManagement: new("replace-microphone", Secret: secret, DeviceId: target, PolicyToken: policyToken));
        try
        {
            var reply = await HostClient.RequestAsync(_pipe, command, token);
            if (generation != _generation) return;
            if (reply.Outcome == "Rejected")
            {
                if (reply.ErrorCode is not ("MANAGEMENT_SECRET_INVALID" or "INVALID_MANAGEMENT_SECRET" or "MANAGEMENT_RATE_LIMITED")) _connected = false;
                DeviceMessage.Text = reply.ErrorCode == "UnknownCapability" ? "当前后台不支持受管麦克风更换，请更新后台后刷新。" : DeviceError(reply.ErrorCode);
                return;
            }
            if (reply.Outcome != "Succeeded" || reply.Noise is not { } current || current.InstanceId != instance ||
                current.SelectedDeviceId != target) throw new InvalidDataException();
            _noise = current;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or OperationCanceledException or TimeoutException or System.Text.Json.JsonException)
        {
            if (generation == _generation) { _connected = false; DeviceMessage.Text = "本次更换结果未确认，请刷新核对当前麦克风。"; }
            return;
        }
        finally { if (generation == _generation) { _busy = false; UpdateButtons(); } }
        if (generation != _generation) return;
        var refresh = RefreshAsync(); long refreshGeneration = _generation;
        await refresh;
        if (refreshGeneration != _generation) return;
        if (!_connected || _noise?.InstanceId != instance || _noise.SelectedDeviceId != target || !_microphones.Any(m => m.Id == target))
        { DeviceMessage.Text = "后台已受理本次更换，最新可用麦克风待核对，请刷新。"; return; }
        DeviceMessage.Text = _camera?.Locked == true ? "已更换监测麦克风，学校设备锁定仍生效。" : "已更换监测麦克风。";
        DeviceMessage.SetResourceReference(TextBlock.ForegroundProperty, "TeacherSuccessBrush");
        SelectionSaved?.Invoke(this, EventArgs.Empty);
    }
}

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
        (_camera is null ? "摄像头状态待确认" : _camera.CameraId is null ? "未使用摄像头" : _camera.CameraAvailable == true ? "摄像头已配置" : "已选摄像头待确认") +
        (_camera?.Locked == true ? " · 学校已锁定" : "");
    private string? _pipe;
    private NoiseState? _noise;
    private StudyDeviceState? _camera;
    private CancellationTokenSource? _operation;
    private long _generation;
    private bool _busy, _rendering, _connected;
    private NoiseDevice[] _microphones = [];
    public StudyDevicePicker()
    {
        InitializeComponent();
        IsVisibleChanged += (_, _) => { if (!IsVisible) CancelPending(); };
        Unloaded += (_, _) => CancelPending();
        UpdateButtons();
    }
    public void Bind(string pipe) { if (_pipe != pipe) { CancelPending(); _pipe = pipe; _noise = null; _camera = null; _connected = false; } }
    public void CancelPending()
    {
        if (_busy) { _connected = false; DeviceMessage.Text = "设备操作结果待确认，请刷新核对。"; }
        _generation++; _operation?.Cancel(); _operation?.Dispose(); _operation = null; _busy = false; UpdateButtons();
    }
    private CancellationToken Begin()
    {
        _operation?.Cancel(); _operation?.Dispose(); _operation = new(); _operation.CancelAfter(6000);
        _busy = true; _generation++; DeviceMessage.Text = "";
        DeviceMessage.Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(170, 83, 55));
        UpdateButtons(); return _operation.Token;
    }
    public async Task RefreshAsync()
    {
        if (_pipe is null || _busy) return;
        var token = Begin(); long generation = _generation;
        string? draftMic = StudyMicrophone.SelectedValue as string, draftCamera = StudyCamera.SelectedValue as string;
        try
        {
            var microphones = await HostClient.RequestAsync(_pipe, "noise.devices", token);
            if (generation != _generation) return;
            if (microphones.Outcome != "Succeeded" || microphones.Noise is null || microphones.NoiseDevices is null) throw new IOException();
            var cameras = await HostClient.RequestAsync(_pipe, "study.devices.refresh", token);
            if (generation != _generation) return;
            _noise = microphones.Noise; _microphones = DistinguishMicrophones(microphones.NoiseDevices); _camera = cameras.StudyDevices;
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
            if (cameras.Outcome != "Succeeded" || _camera?.Error is not null) DeviceMessage.Text = DeviceError(cameras.ErrorCode ?? _camera?.Error);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or OperationCanceledException or TimeoutException or System.Text.Json.JsonException)
        {
            if (generation != _generation) return;
            _connected = false; DeviceMessage.Text = "设备状态暂不可用，请检查后台连接后刷新；旧列表不能用于保存。";
            MicrophoneStatus.Text = "麦克风状态待重新读取"; CameraStatus.Text = "摄像头状态待重新读取";
        }
        finally { if (generation == _generation) { _rendering = false; _busy = false; UpdateButtons(); } }
    }
    private void RenderStatus()
    {
        MicrophoneStatus.Text = _noise?.SelectedDeviceId is not { } id ? "尚未保存监测麦克风" :
            _microphones.FirstOrDefault(d => d.Id == id) is { } mic ? "已保存：" + mic.Name : "已保存的麦克风当前不可用；原选择保留";
        CameraStatus.Text = _camera is null ? "当前后台尚不支持摄像头选择" : _camera.CameraId is null ? "摄像头未选择 · 不影响基本自习监测" :
            _camera.CameraAvailable == true ? "已保存：" + _camera.CameraName + " · 未开启捕获" : "摄像头选择已保存，可用性未确认或设备已断开";
    }
    private bool Idle => _noise?.State is "Idle" or "Stopped" or "Faulted";
    private void UpdateButtons()
    {
        if (DeviceRefresh is null) return;
        DeviceRefresh.IsEnabled = !_busy;
        bool locked=_camera?.Locked==true;
        EquipmentLockPanel.Visibility=locked?Visibility.Visible:Visibility.Collapsed;
        StudyMicrophone.IsEnabled = StudyCamera.IsEnabled = !_busy && _connected && Idle && !locked;
        SaveMicrophone.IsEnabled = !_busy && _connected && Idle && !locked && StudyMicrophone.SelectedValue is string mic && _microphones.Any(d => d.Id == mic);
        SaveCamera.IsEnabled = !_busy && _connected && Idle && !locked && _camera is not null && _camera.Error is null &&
            StudyCamera.SelectedValue is string cam && (cam.Length == 0 || _camera.Cameras.Any(d => d.Id == cam));
        StatusChanged?.Invoke(this, EventArgs.Empty);
    }
    public async Task<bool> CheckReadyAsync()
    {
        await RefreshAsync();
        if (_busy || !_connected || _noise?.SelectedDeviceId is not { } id || !_microphones.Any(d => d.Id == id))
        { DeviceMessage.Text = "请保存当前可用的监测麦克风，或选择跳过此项。"; return false; }
        if (StudyMicrophone.SelectedValue as string != id || _camera is not null && StudyCamera.SelectedValue as string != (_camera.CameraId ?? ""))
        { DeviceMessage.Text = "设备选择尚未保存，请先点击对应保存按钮；摄像头也可以暂不使用。"; return false; }
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
            if (response.Noise is { } noise) _noise = noise;
            if (response.StudyDevices is { } devices) _camera = devices;
            if (response.Outcome != "Succeeded") DeviceMessage.Text = DeviceError(response.ErrorCode);
            else
            {
                DeviceMessage.Text = camera ? "摄像头选择已保存，未开始捕获。" : "监测麦克风已保存，未开始采集。";
                DeviceMessage.Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(20, 125, 104));
                SelectionSaved?.Invoke(this, EventArgs.Empty);
            }
            RenderStatus();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or OperationCanceledException or TimeoutException or System.Text.Json.JsonException)
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
        "StudyDevicesBusy" or "NoiseBusy" => "正在监测，不能更换设备；请在监测结束后设置。",
        "STUDY_UNSUPPORTED" or "UnknownCapability" => "请更新并重启 Host，以使用原生摄像头选择。",
        _ => "设备设置未确认，请刷新后重试。"
    };
    private static NoiseDevice[] DistinguishMicrophones(IReadOnlyList<NoiseDevice> devices) => devices.Select(device =>
    {
        var same = devices.Where(d => d.Name == device.Name).ToArray();
        return device with { Name = same.Length > 1 ? $"{device.Name}（{Array.FindIndex(same, d => d.Id == device.Id) + 1}）" : device.Name };
    }).ToArray();
    private void ChoiceChanged(object sender, SelectionChangedEventArgs e) { if (!_rendering) UpdateButtons(); }
    private async void RefreshClicked(object sender, RoutedEventArgs e) => await RefreshAsync();
    private async void UnlockClicked(object sender,RoutedEventArgs e)
    {
        if(_pipe is null||_busy)return;
        string secret=EquipmentSecret.Password;EquipmentSecret.Clear();
        if(!NoiseManagementContract.SecretValid(secret)){DeviceMessage.Text="请输入8至128位学校管理口令。";return;}
        var token=Begin();long generation=_generation;
        try
        {
            var reply=await HostClient.RequestAsync(_pipe,new HostRequest(1,Guid.NewGuid(),"noise.management.command",NoiseManagement:new("unlock",Secret:secret)),token);
            if(generation!=_generation)return;
            DeviceMessage.Text=reply.Outcome=="Succeeded"?"设备已解锁，学校再次锁定前可在监测结束后更换。":reply.Message;
        }
        catch(Exception ex)when(ex is IOException or UnauthorizedAccessException or OperationCanceledException or TimeoutException or System.Text.Json.JsonException)
        {if(generation==_generation)DeviceMessage.Text="解锁结果未确认，请刷新检查。";}
        finally{if(generation==_generation){_busy=false;UpdateButtons();}}
        if(generation==_generation)await RefreshAsync();
    }
}

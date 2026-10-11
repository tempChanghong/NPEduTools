using System.Text.Json;
using NPEduTools.Contracts;

namespace NPEduTools.Host;

/// <summary>Local camera preferences only. Enumeration never activates a media source.</summary>
public sealed class StudyDeviceService(Func<IReadOnlyList<StudyCameraDevice>> enumerate, string directory, Func<bool>? captureBusy = null)
{
    private readonly object _sync = new();
    private readonly Guid _instance = Guid.NewGuid();
    private long _revision;
    private readonly string _path = Path.Combine(directory, "study-camera.json");
    private string? _selected;
    private StudyCameraDevice[] _devices = [];
    private bool _enumerated;
    private string? _error;
    private bool _loaded;
    private bool _configurationError;
    private void RefreshCatalog()
    {
        int bytes = 0;
        _devices = enumerate().Take(128).Where(d => NoiseContract.ValidDeviceId(d.Id) &&
            !string.IsNullOrWhiteSpace(d.Name) && d.Name.Length <= 512 && !d.Name.Any(char.IsControl))
            .DistinctBy(d => d.Id).TakeWhile(d => (bytes += System.Text.Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(d))) < 24000).ToArray();
        _enumerated = true;
        if (_error == "CameraDevicesUnavailable") _error = null;
    }
    private void Load()
    {
        if (_loaded) return;
        _loaded = true;
        try
        {
            if (!File.Exists(_path)) return;
            if (new FileInfo(_path).Length > 16384) throw new InvalidDataException();
            _selected = JsonSerializer.Deserialize<string?>(File.ReadAllText(_path));
            if (_selected is not null && !NoiseContract.ValidDeviceId(_selected)) throw new InvalidDataException();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        { _selected = null; _configurationError = true; }
    }
    public StudyDeviceState Snapshot()
    {
        lock (_sync)
        {
            Load();
            var device = _devices.FirstOrDefault(d => d.Id == _selected);
            return new(_instance, _revision, _selected, device?.Name,
                _selected is null || !_enumerated || _error is not null ? null : device is not null, _devices,
                _configurationError ? "CameraConfigurationUnavailable" : _error);
        }
    }
    public HostResponse Handle(HostRequest request)
    {
        lock (_sync)
        {
            Load();
            HostResponse Reply(string? error = null, string message = "自习设备状态。") =>
                new(1, request.RequestId, error is null ? "Succeeded" : "Rejected", error, message, StudyDevices: Snapshot());
            if (Protocol.Validate(request) is { } invalid) return Reply(invalid);
            if (request.Capability == "study.devices.status") return Reply();
            if (request.Capability == "study.devices.refresh")
            {
                try
                {
                    RefreshCatalog();
                    return Reply(_configurationError ? "CameraConfigurationUnavailable" : _error, "摄像头列表已刷新；未打开摄像头。");
                }
                catch (Exception e) when (e is not OutOfMemoryException)
                { _devices = []; _enumerated = false; _error = "CameraDevicesUnavailable"; return Reply(_error, "摄像头列表暂不可用；原选择保留。"); }
            }
            if (request.Capability != "study.devices.select" || request.StudyCamera is not { } c) return Reply("UnknownCapability");
            if (c.InstanceId != _instance || c.Revision != _revision) return Reply("CameraStateChanged", "设备设置已变化，请刷新后再保存。");
            if (_configurationError) return Reply("CameraConfigurationUnavailable", "摄像头配置文件无法读取，原文件已保留；请检查配置目录。");
            if (captureBusy?.Invoke() == true) return Reply("StudyDevicesBusy", "正在监测，设备选择未更改；请在结束后设置。");
            // Re-enumerate at the save boundary; a previously listed device may have been unplugged.
            if (c.Action == "select")
            {
                try
                {
                    RefreshCatalog();
                    if (!_devices.Any(d => d.Id == c.DeviceId)) return Reply("CameraDeviceUnavailable", "所选摄像头已不可用，请刷新；没有切换其他设备。");
                }
                catch (Exception e) when (e is not OutOfMemoryException)
                { _devices = []; _enumerated = false; _error = "CameraDevicesUnavailable"; return Reply(_error, "无法核对摄像头，请刷新后重试。"); }
            }
            string? id = c.Action == "clear" ? null : c.DeviceId;
            if (id == _selected) return Reply(message: "摄像头选择已经保存；未开始捕获。");
            string temporary = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                Directory.CreateDirectory(directory);
                File.WriteAllText(temporary, JsonSerializer.Serialize(id));
                File.Move(temporary, _path, true);
                _selected = id; _revision++; _error = null;
                return Reply(message: id is null ? "已设为暂不使用摄像头。" : "摄像头选择已保存；未开始捕获。");
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            { return Reply("CameraConfigurationUnavailable", "未能保存摄像头；原选择保留。"); }
            finally { try { if (File.Exists(temporary)) File.Delete(temporary); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
        }
    }
}

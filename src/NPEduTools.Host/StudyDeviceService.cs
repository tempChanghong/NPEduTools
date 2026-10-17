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
    internal bool EquipmentLocked {get;set;}
    private bool _schoolSelection;
    internal string? SelectSchoolCamera(string? id)
    {
        lock(_sync)
        {
            // The first school policy can arrive before any inventory/status read.
            // Compare persisted preferences, and never certify unreadable settings as a no-op.
            Load();
            if(!_configurationError && id==_selected)return null;
            _schoolSelection=true;
            try
            {
                var state=Snapshot();
                var reply=Handle(new(1,Guid.NewGuid(),"study.devices.select",StudyCamera:new(id is null?"clear":"select",state.InstanceId,state.Revision,id)));
                return reply.Outcome=="Succeeded"?null:reply.ErrorCode is "StudyDevicesBusy"?"EQUIPMENT_PENDING":"EQUIPMENT_UNAVAILABLE";
            }
            finally{_schoolSelection=false;}
        }
    }
    private StudyCameraDevice[] RefreshCatalog()
    {
        var candidates = enumerate().Where(d => NoiseContract.ValidDeviceId(d.Id) &&
            !string.IsNullOrWhiteSpace(d.Name) && d.Name.Length <= 512 && !d.Name.Any(char.IsControl))
            .DistinctBy(d => d.Id).ToArray();
        _devices = DeviceInventory.Limit(candidates, _selected, d => d.Id);
        _enumerated = true;
        if (_error == "CameraDevicesUnavailable") _error = null;
        return candidates;
    }
    private void Load(bool retry = false)
    {
        if (_loaded && (!retry || !_configurationError)) return;
        _loaded = true;
        try
        {
            string? selected;
            try
            {
                using var file = File.OpenRead(_path);
                if (file.Length > 16384) throw new InvalidDataException();
                using var reader = new StreamReader(file);
                selected = JsonSerializer.Deserialize<string?>(reader.ReadToEnd());
            }
            catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException) { selected = null; }
            if (selected is not null && !NoiseContract.ValidDeviceId(selected)) throw new InvalidDataException();
            _selected = selected;
            // A validated reread or missing preference (unselected) can clear the fault. Fence commands
            // formed from the earlier unreadable snapshot, without rewriting the file.
            if (_configurationError) _revision++;
            _configurationError = false;
        }
        catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException or JsonException)
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
                _configurationError ? "CameraConfigurationUnavailable" : _error,EquipmentLocked);
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
                    Load(retry: true);
                    RefreshCatalog();
                    return Reply(_configurationError ? "CameraConfigurationUnavailable" : _error, "摄像头列表已刷新；未打开摄像头。");
                }
                catch (Exception e) when (e is not OutOfMemoryException)
                { _devices = []; _enumerated = false; _error = "CameraDevicesUnavailable"; return Reply(_error, "摄像头列表暂不可用；原选择保留。"); }
            }
            if (request.Capability != "study.devices.select" || request.StudyCamera is not { } c) return Reply("UnknownCapability");
            if(EquipmentLocked && !_schoolSelection)return Reply("EQUIPMENT_LOCKED","学校已锁定采集设备，请先通过学校管理口令解锁。");
            if (c.InstanceId != _instance || c.Revision != _revision) return Reply("CameraStateChanged", "设备设置已变化，请刷新后再保存。");
            if (_configurationError) return Reply("CameraConfigurationUnavailable", "摄像头配置文件无法读取，原文件已保留；请检查配置目录。");
            if (captureBusy?.Invoke() == true) return Reply("StudyDevicesBusy", "正在监测，设备选择未更改；请在结束后设置。");
            // Re-enumerate at the save boundary; a previously listed device may have been unplugged.
            StudyCameraDevice[]? candidates = null;
            if (c.Action == "select")
            {
                try
                {
                    candidates = RefreshCatalog();
                    // Validate against the complete fresh catalog, not the bounded
                    // display prefix which still prioritizes the old saved device.
                    if (!candidates.Any(d => d.Id == c.DeviceId)) return Reply("CameraDeviceUnavailable", "所选摄像头已不可用，请刷新；没有切换其他设备。");
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
                // Publish a bounded snapshot containing the newly saved current
                // camera only after the durable write succeeds.
                if (candidates is not null) _devices = DeviceInventory.Limit(candidates, _selected, d => d.Id);
                return Reply(message: id is null ? "已设为暂不使用摄像头。" : "摄像头选择已保存；未开始捕获。");
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            { return Reply("CameraConfigurationUnavailable", "未能保存摄像头；原选择保留。"); }
            finally { try { if (File.Exists(temporary)) File.Delete(temporary); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
        }
    }
}

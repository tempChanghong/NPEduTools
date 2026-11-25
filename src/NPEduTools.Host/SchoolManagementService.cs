using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text;
using System.Globalization;
using NPEduTools.Contracts;
using NPEduTools.Integrations.Npep;

namespace NPEduTools.Host;

/// <summary>Inventory never opens devices. Each local microphone choice belongs to one school policy revision.</summary>
public sealed partial class SchoolManagementService : INpepSchoolManagement
{
    private readonly object _sync=new();
    private readonly NoiseService _noise;
    private readonly StudyDeviceService _cameras;
    private readonly string _path;
    private readonly ClassroomPolicyStore _classroomPolicy;
    private bool _tracksRegistration;
    private string? _registeredScope;
    private Saved _saved=new(null,0,false,0,null,null,false);
    private string? _error;
    private string? _credentialError;
    private bool _savePending;
    private bool _extendedReceipt;
    private Guid? _microphoneChangeToken;
    private sealed record Saved(string? Scope,long Revision,bool Locked,long Applied,string? Microphone,string? Camera,bool Unlocked,
        bool AllowLocalMicrophoneChange = false, string? LocalMicrophoneOverride = null);
    public SchoolManagementService(NoiseService noise,StudyDeviceService cameras,string directory)
        : this(noise, cameras, directory, null) { }

    internal SchoolManagementService(NoiseService noise,StudyDeviceService cameras,string directory, EdgeWebsitePolicy? edgeWebsitePolicy)
    {
        _edgeWebsitePolicy = edgeWebsitePolicy;
        _noise=noise;_cameras=cameras;_path=Path.Combine(directory,"school-equipment.json");
        _classroomPolicy = new ClassroomPolicyStore(directory, noise);
        try
        {
            if(File.Exists(_path))
            {
                // Three opaque endpoint IDs plus the school identity can each require JSON escaping.
                if(new FileInfo(_path).Length>65536)throw new InvalidDataException();
                var saved=JsonSerializer.Deserialize<Saved>(File.ReadAllText(_path))??throw new InvalidDataException();
                if(saved.Revision<0||saved.Revision>NpepProtocol.MaxInteger||saved.Applied<0||saved.Applied>saved.Revision||saved.Scope is {Length:>4096}||
                    saved.Microphone is not null && !NoiseContract.ValidDeviceId(saved.Microphone)||saved.Camera is not null && !NoiseContract.ValidDeviceId(saved.Camera) ||
                    saved.LocalMicrophoneOverride is { } local && (!NoiseContract.ValidDeviceId(local) || !saved.AllowLocalMicrophoneChange ||
                        !saved.Locked || saved.Scope is null || saved.Revision == 0))throw new InvalidDataException();
                // Do not publish a partially parsed policy when validation fails.
                _saved=saved;
                if (saved.Unlocked)
                {
                    // Old clients could leave both devices unlocked across restarts.
                    _saved = saved with { Unlocked = false, Applied = 0, LocalMicrophoneOverride = null };
                    Save();
                }
                else if (saved.LocalMicrophoneOverride is { } target)
                    _error = _noise.SelectSchoolMicrophone(target);
            }
        }
        catch(Exception e)when(e is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
        { _error="EQUIPMENT_STORE_UNAVAILABLE";_saved=_saved with{Locked=true,Unlocked=false}; }
        SetLocks();_noise.ReplaceLocalMicrophone=ReplaceMicrophone;
    }
    private void SetLocks(bool renewToken = false)
    {
        bool canChange = _saved.Locked && _saved.AllowLocalMicrophoneChange && _saved.Scope is not null &&
            _saved.Revision > 0 && !_savePending && _credentialError is null;
        if (!canChange) _microphoneChangeToken = null;
        else if (renewToken || _microphoneChangeToken is null) _microphoneChangeToken = Guid.NewGuid();
        _noise.EquipmentLocked = _saved.Locked;
        _cameras.SetEquipmentPolicy(_saved.Locked, _microphoneChangeToken);
    }
    private static string InventoryCaption(string? value, int maxLength = 512)
    {
        // KV permits visible Unicode names, but rejects control/format characters.
        // Project a compatible caption; endpoint IDs and local preferences stay opaque.
        var caption = new StringBuilder();
        foreach (var rune in (value ?? "").EnumerateRunes())
        {
            if (Rune.GetUnicodeCategory(rune) is UnicodeCategory.Control or UnicodeCategory.Format) continue;
            if (caption.Length + rune.Utf16SequenceLength > maxLength) break;
            caption.Append(rune.ToString());
        }
        return caption.Length == 0 || string.IsNullOrWhiteSpace(caption.ToString()) ? "未命名设备" : caption.ToString();
    }
    private static int ReserveCurrentDevice<T>(T[] catalog, T? selected, Func<T,T> shortenCaption) where T : class
    {
        if (selected is null) return 2; // Empty JSON array.
        var compact = shortenCaption(selected);
        catalog[Array.IndexOf(catalog, selected)] = compact;
        return JsonSerializer.SerializeToUtf8Bytes(compact, Protocol.Json).Length + 4;
    }
    private bool Save()
    {
        _savePending=true;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            using(var stream=new FileStream(_path+".tmp",FileMode.Create,FileAccess.Write,FileShare.None,4096,FileOptions.WriteThrough))
            {JsonSerializer.Serialize(stream,_saved);stream.Flush(true);}
            File.Move(_path+".tmp",_path,true);_savePending=false;_error=null;return true;
        }
        catch(Exception e)when(e is IOException or UnauthorizedAccessException){_error="EQUIPMENT_STORE_UNAVAILABLE";return false;}
    }
    public void BindSchool(string? scope)
    {
        lock(_sync)
        {
            if (_tracksRegistration && scope is not null && _registeredScope != scope) return;
            _classroomPolicy.Bind(scope);
            _noise.BindSchoolManagement(scope);
            if(_saved.Scope==scope){if(_savePending){Save();SetLocks();}return;}
            _websitePolicyReceipt = null;
            _credentialError=null;
            _extendedReceipt=false;
            _saved=new(scope,0,scope is not null,0,null,null,false);
            Save();SetLocks();
        }
    }
    public void ObserveActionBinding(string? scope)
    {
        lock (_sync)
        {
            if (_registeredScope != scope) _websitePolicyReceipt = null;
            _tracksRegistration = true;
            _registeredScope = scope;
            _classroomPolicy.Bind(scope);
        }
    }
    public void ApplySchool(JsonObject policy)
    {
        lock(_sync)
        {
            try
            {
                if(_saved.Scope is null || !JsonNode.DeepEquals(policy["identity"],JsonNode.Parse(_saved.Scope)))throw new NpepException("SESSION_SUPERSEDED");
                if (_tracksRegistration && _saved.Scope != _registeredScope) throw new NpepException("SESSION_SUPERSEDED");
                ReceiveWebsitePolicy(_saved.Scope, policy["websitePolicy"]);
                // Credential and equipment acknowledgements are independent; a durable
                // equipment write cannot erase a rejected credential update.
                _error = null;
                try { _credentialError = _noise.ApplySchoolManagement(_saved.Scope, policy["credential"]); }
                catch (Exception e) when (policy.ContainsKey("actionPolicy") &&
                    e is InvalidOperationException or FormatException or NullReferenceException or JsonException)
                {
                    // A bad verifier blocks password use and equipment updates. A valid
                    // action-policy revocation from the same binding still takes effect.
                    _credentialError = "INVALID_RESPONSE";
                    _classroomPolicy.Apply(_saved.Scope, policy);
                    return;
                }
                _classroomPolicy.Apply(_saved.Scope, policy);
                var p=policy["equipment"]!.AsObject();long revision=p["revision"]!.Deserialize<long>();bool locked=p["locked"]!.GetValue<bool>();
                string? mic=p["microphoneId"]?.GetValue<string>(),cam=p["cameraId"]?.GetValue<string>();
                bool extended = p.ContainsKey("allowLocalMicrophoneChange");
                bool allow = extended && p["allowLocalMicrophoneChange"]!.GetValue<bool>();
                long micRevision = p.ContainsKey("microphonePolicyRevision") ? p["microphonePolicyRevision"]!.Deserialize<long>() : revision;
                if(revision<0 || revision>NpepProtocol.MaxInteger || revision<_saved.Revision || mic is not null && !NoiseContract.ValidDeviceId(mic) || cam is not null && !NoiseContract.ValidDeviceId(cam))throw new NpepException("INVALID_RESPONSE");
                if (micRevision < 0 || micRevision > revision) throw new NpepException("INVALID_RESPONSE");
                _extendedReceipt = extended;
                bool legacyFallback = !extended && _saved.AllowLocalMicrophoneChange;
                if (revision == _saved.Revision && revision != 0 && !legacyFallback &&
                    (locked != _saved.Locked || mic != _saved.Microphone || cam != _saved.Camera || allow != _saved.AllowLocalMicrophoneChange))
                    throw new NpepException("INVALID_RESPONSE");
                bool changed = revision != _saved.Revision || legacyFallback ||
                    revision == 0 && (locked != _saved.Locked || mic != _saved.Microphone || cam != _saved.Camera || allow != _saved.AllowLocalMicrophoneChange);
                if(changed)
                {
                    // Camera edits can span several offline revisions. Only a microphone
                    // decision (including reselecting the same school target) supersedes B.
                    string? retained = locked && allow && mic == _saved.Microphone && micRevision <= _saved.Revision
                        ? _saved.LocalMicrophoneOverride : null;
                    _saved=new(_saved.Scope,revision,locked,legacyFallback ? 0 : _saved.Applied,mic,cam,false,allow,retained);
                    if(!Save()){SetLocks();return;}
                }
                // A matching revision does not prove the previous write succeeded.
                // Retry the durable policy before switching devices or skipping as already applied.
                if(_savePending && !Save()){SetLocks();return;}
                SetLocks(changed);
                string? local = _saved.LocalMicrophoneOverride;
                if(revision==0 || _saved.Applied==revision && (local is null || _noise.UsesMicrophone(local)))return;
                if(_noise.CaptureBusy){_error="EQUIPMENT_PENDING";return;}
                _error=_noise.SelectSchoolMicrophone(local ?? mic)??_cameras.SelectSchoolCamera(cam);
                if(_error is null)
                {
                    var old=_saved;_saved=_saved with{Applied=revision};
                    if(!Save())_saved=old;
                }
            }
            catch(Exception e)when(e is InvalidOperationException or FormatException or NullReferenceException or JsonException)
            {throw new NpepException("INVALID_RESPONSE");}
        }
    }
    private string? ReplaceMicrophone(NoiseManagementCommand change)
    {
        lock(_sync)
        {
            if (_microphoneChangeToken is null || _saved.Scope is null) return "EQUIPMENT_LOCKED";
            if (change.PolicyToken != _microphoneChangeToken) return "EQUIPMENT_POLICY_CHANGED";
            bool committed = false;
            bool SaveChoice()
            {
                var previous = _saved;
                _saved = previous with { LocalMicrophoneOverride = change.DeviceId };
                if (Save()) { committed = true; return true; }
                _saved = previous;
                return false;
            }
            var result = _noise.ChangeAuthorizedMicrophone(_saved.Scope, change.Secret!, change.DeviceId!, SaveChoice);
            SetLocks(renewToken: committed);
            if (committed && result is not null)
            {
                // The authorized target is durable. Startup or the next policy read retries promotion.
                _error = result;
                return "EQUIPMENT_CHANGE_UNCONFIRMED";
            }
            return result;
        }
    }
    public JsonObject ObserveSchool()
    {
        lock(_sync)
        {
            var microphones=_noise.Handle(new(1,Guid.NewGuid(),"noise.devices"));
            var cameraReply=_cameras.Handle(new(1,Guid.NewGuid(),"study.devices.refresh"));
            var cameras=cameraReply.StudyDevices!;
            var noise=microphones.Noise!;
            // Empty inventory after a failed read is unknown, not a successful no-device result.
            // Keep operation/credential errors primary; a later healthy refresh clears only this read error.
            string? inventoryError=cameras.Error=="CameraConfigurationUnavailable"?"EQUIPMENT_STORE_UNAVAILABLE":
                microphones.Outcome!="Succeeded" || cameraReply.Outcome!="Succeeded" || cameras.Error is not null?"EQUIPMENT_UNAVAILABLE":null;
            var micCatalog=microphones.NoiseDevices?.Select(d=>d with{Name=InventoryCaption(d.Name)}).ToArray()??[];
            var camCatalog=cameras.Cameras.Select(d=>d with{Name=InventoryCaption(d.Name)}).ToArray();
            var receipt=new JsonObject(){["microphones"]=JsonSerializer.SerializeToNode(micCatalog,Protocol.Json),
                ["cameras"]=JsonSerializer.SerializeToNode(camCatalog,Protocol.Json),
                ["unlocked"]=false,["appliedRevision"]=_saved.Applied,["credentialRevision"]=_noise.SchoolCredentialRevision,
                ["selectedMicrophoneId"]=noise.SelectedDeviceId,["selectedCameraId"]=cameras.CameraId,["error"]=_error??_credentialError??_noise.Protection().ErrorCode??inventoryError};
            if (_extendedReceipt)
            {
                receipt["supportsLocalMicrophoneChange"] = true;
                receipt["localMicrophoneOverride"] = _saved.LocalMicrophoneOverride == _saved.Microphone ? null : _saved.LocalMicrophoneOverride;
            }
            if (_classroomPolicy.Observe() is { } actionReceipt) receipt["actionPolicyReceipt"] = actionReceipt;
            if (_websitePolicyReceipt is { } websiteReceipt)
            {
                var data = (JsonObject)websiteReceipt.DeepClone();
                if (_websitePolicyApplied && _edgeWebsitePolicy is { } edge)
                {
                    edge.RefreshTiming();
                    data["status"] = edge.Status;
                    if (data["schemaVersion"]?.GetValue<int>() == 3)
                    {
                        data["schedulePhase"] = edge.SchedulePhase;
                        if (edge.SchedulePhaseHeld) data["phaseHeld"] = true;
                    }
                }
                receipt["websitePolicyReceipt"] = data;
            }
            // Inventory budgets alone omit the selected IDs repeated in this receipt.
            // Keep the whole observation under 60,000 bytes, reserving >5 KiB for
            // the fixed-schema context, request ID and sequence added by the runtime.
            // Ordinary receipts keep their original lists and order.
            const int observationBudget=60000;
            if(Encoding.UTF8.GetByteCount(receipt.ToJsonString())>observationBudget)
            {
                receipt["microphones"]=new JsonArray();receipt["cameras"]=new JsonArray();
                int catalogBudget=(observationBudget-Encoding.UTF8.GetByteCount(receipt.ToJsonString())+4)/2;
                var mics = DeviceInventory.Limit(micCatalog,noise.SelectedDeviceId,d=>d.Id,catalogBudget);
                var cams = DeviceInventory.Limit(camCatalog,cameras.CameraId,d=>d.Id,catalogBudget);
                if (receipt["localMicrophoneOverride"] is not null)
                {
                    var currentMic = micCatalog.FirstOrDefault(d => d.Id == noise.SelectedDeviceId);
                    var currentCam = camCatalog.FirstOrDefault(d => d.Id == cameras.CameraId);
                    if (currentMic is not null && !mics.Contains(currentMic) || currentCam is not null && !cams.Contains(currentCam))
                    {
                        // With three long escaped IDs already in the header, an equal split can
                        // drop the actual device. Reserve its entry before spending bytes on alternatives.
                        int micMinimum = ReserveCurrentDevice(micCatalog, currentMic, d => d with { Name = InventoryCaption(d.Name, 48) });
                        int camMinimum = ReserveCurrentDevice(camCatalog, currentCam, d => d with { Name = InventoryCaption(d.Name, 48) });
                        // Even five maximal escaped endpoint IDs plus the short captions fit
                        // in 64,000 bytes, leaving over 1.5 KiB for the fixed NPEP context.
                        int spare = (64000 - Encoding.UTF8.GetByteCount(receipt.ToJsonString()) - micMinimum - camMinimum) / 2;
                        mics = DeviceInventory.Limit(micCatalog, noise.SelectedDeviceId, d => d.Id, micMinimum + spare);
                        cams = DeviceInventory.Limit(camCatalog, cameras.CameraId, d => d.Id, camMinimum + spare);
                    }
                }
                receipt["microphones"]=JsonSerializer.SerializeToNode(mics,Protocol.Json);
                receipt["cameras"]=JsonSerializer.SerializeToNode(cams,Protocol.Json);
            }
            return receipt;
        }
    }
}

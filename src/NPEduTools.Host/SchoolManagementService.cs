using System.Text.Json;
using System.Text.Json.Nodes;
using NPEduTools.Contracts;
using NPEduTools.Integrations.Npep;

namespace NPEduTools.Host;

/// <summary>Inventory never opens devices. School revisions fence lock/unlock and delayed selection.</summary>
public sealed class SchoolManagementService : INpepSchoolManagement
{
    private readonly object _sync=new();
    private readonly NoiseService _noise;
    private readonly StudyDeviceService _cameras;
    private readonly string _path;
    private Saved _saved=new(null,0,false,0,null,null,false);
    private string? _error;
    private bool _savePending;
    private sealed record Saved(string? Scope,long Revision,bool Locked,long Applied,string? Microphone,string? Camera,bool Unlocked);
    public SchoolManagementService(NoiseService noise,StudyDeviceService cameras,string directory)
    {
        _noise=noise;_cameras=cameras;_path=Path.Combine(directory,"school-equipment.json");
        try
        {
            if(File.Exists(_path))
            {
                if(new FileInfo(_path).Length>16384)throw new InvalidDataException();
                _saved=JsonSerializer.Deserialize<Saved>(File.ReadAllText(_path))??throw new InvalidDataException();
                if(_saved.Revision<0||_saved.Applied<0||_saved.Applied>_saved.Revision||_saved.Scope is {Length:>4096}||
                    _saved.Microphone is not null && !NoiseContract.ValidDeviceId(_saved.Microphone)||_saved.Camera is not null && !NoiseContract.ValidDeviceId(_saved.Camera))throw new InvalidDataException();
            }
        }
        catch(Exception e)when(e is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
        { _error="EQUIPMENT_STORE_UNAVAILABLE";_saved=_saved with{Locked=true,Unlocked=false}; }
        SetLocks();_noise.EquipmentUnlock=Unlock;
    }
    private void SetLocks(){_noise.EquipmentLocked=_cameras.EquipmentLocked=_saved.Locked&&!_saved.Unlocked;}
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
            _noise.BindSchoolManagement(scope);
            if(_saved.Scope==scope){if(_savePending)Save();return;}
            _saved=new(scope,0,scope is not null,0,null,null,false);
            Save();SetLocks();
        }
    }
    public void ApplySchool(JsonObject policy)
    {
        lock(_sync)
        {
            try
            {
                if(_saved.Scope is null || !JsonNode.DeepEquals(policy["identity"],JsonNode.Parse(_saved.Scope)))throw new NpepException("SESSION_SUPERSEDED");
                _error=_noise.ApplySchoolManagement(_saved.Scope!,policy["credential"]?.AsObject());
                var p=policy["equipment"]!.AsObject();long revision=p["revision"]!.Deserialize<long>();bool locked=p["locked"]!.GetValue<bool>();
                string? mic=p["microphoneId"]?.GetValue<string>(),cam=p["cameraId"]?.GetValue<string>();
                if(revision<0 || revision<_saved.Revision || mic is not null && !NoiseContract.ValidDeviceId(mic) || cam is not null && !NoiseContract.ValidDeviceId(cam))throw new NpepException("INVALID_RESPONSE");
                if(revision!=_saved.Revision || revision==0 && (locked!=_saved.Locked || mic!=_saved.Microphone || cam!=_saved.Camera))
                {
                    _saved=new(_saved.Scope,revision,locked,_saved.Applied,mic,cam,false);
                    if(!Save()){SetLocks();return;}
                }
                // A matching revision does not prove the previous write succeeded.
                // Retry the durable policy before switching devices or skipping as already applied.
                if(_savePending && !Save()){SetLocks();return;}
                SetLocks();
                if(_saved.Applied==revision || revision==0 || _saved.Unlocked)return;
                if(_noise.CaptureBusy){_error="EQUIPMENT_PENDING";return;}
                _error=_noise.SelectSchoolMicrophone(mic)??_cameras.SelectSchoolCamera(cam);
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
    private string? Unlock(string secret)
    {
        lock(_sync)
        {
            if(_noise.VerifyEquipmentSecret(secret) is {} denied)return denied;
            var old=_saved;_saved=_saved with{Unlocked=true};
            if(!Save()){_saved=old;return _error;}
            SetLocks();return "UNLOCKED";
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
            return new(){["microphones"]=JsonSerializer.SerializeToNode(microphones.NoiseDevices??[],Protocol.Json),["cameras"]=JsonSerializer.SerializeToNode(cameras.Cameras,Protocol.Json),
                ["unlocked"]=_saved.Unlocked,["appliedRevision"]=_saved.Applied,["credentialRevision"]=_noise.SchoolCredentialRevision,
                ["selectedMicrophoneId"]=noise.SelectedDeviceId,["selectedCameraId"]=cameras.CameraId,["error"]=_error??_noise.Protection().ErrorCode??inventoryError};
        }
    }
}

using System.Text.Json.Nodes;
using NPEduTools.Contracts;

namespace NPEduTools.Host;

public sealed partial class NoiseService
{
    internal string? VerifyCalibrationSecret(string secret) { lock (_sync) return management?.VerifyCalibration(secret) ?? (management is null ? "MANAGEMENT_NOT_CONFIGURED" : null); }
    internal bool EquipmentLocked {get;set;}
    internal Func<string,string?>? EquipmentUnlock {get;set;}
    internal string? BindSchoolManagement(string? scope) {lock(_sync)return management?.BindSchool(scope);}
    internal string? ApplySchoolManagement(string scope,JsonObject? credential){lock(_sync)return management?.ApplySchool(scope,credential);}
    internal long SchoolCredentialRevision {get{lock(_sync)return management?.SchoolRevision??0;}}
    internal string? VerifyEquipmentSecret(string secret){lock(_sync)return management?.Source=="School"?management.VerifyCalibration(secret):"SCHOOL_MANAGEMENT_REQUIRED";}
    internal string? SelectSchoolMicrophone(string? id)
    {
        lock(_sync)
        {
            if(id is null || id==_selected)return null;
            if(CaptureBusy)return "EQUIPMENT_PENDING";
            // Use the same recoverable enumeration boundary as local device refresh.
            // Endpoint/COM failures are device evidence, not policy or storage failures.
            if(!IsMicrophonePresent(id))return "EQUIPMENT_UNAVAILABLE";
            try
            {
                Select(id);_revision++;return null;
            }
            catch(Exception e)when(e is IOException or UnauthorizedAccessException){return "EQUIPMENT_STORE_UNAVAILABLE";}
        }
    }
}

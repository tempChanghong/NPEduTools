using System.Text.Json.Nodes;
using NPEduTools.Contracts;

namespace NPEduTools.Host;

public sealed partial class NoiseService
{
    internal string? VerifyCalibrationSecret(string secret) { lock (_sync) return management?.VerifyCalibration(secret) ?? (management is null ? "MANAGEMENT_NOT_CONFIGURED" : null); }
    internal volatile bool EquipmentLocked = false;
    internal Func<NoiseManagementCommand, string?>? ReplaceLocalMicrophone {get;set;}
    internal string? BindSchoolManagement(string? scope) {lock(_sync)return management?.BindSchool(scope);}
    internal string? ApplySchoolManagement(string scope,JsonObject? credential){lock(_sync)return management?.ApplySchool(scope,credential);}
    internal long SchoolCredentialRevision {get{lock(_sync)return management?.SchoolRevision??0;}}
    internal bool UsesMicrophone(string id) { lock (_sync) return _selected == id; }
    internal string? ChangeAuthorizedMicrophone(string scope, string secret, string id, Func<bool> commit)
    {
        lock (_sync)
        {
            if (management is null || !management.MatchesSchool(scope)) return "SCHOOL_MANAGEMENT_REQUIRED";
            if (management.VerifyCalibration(secret) is { } denied) return denied;
            if (CaptureBusy || _disposed || _shuttingDown || ExitPending || _cleanupFailed) return "EQUIPMENT_PENDING";
            if (!IsMicrophonePresent(id)) return "EQUIPMENT_UNAVAILABLE";
            try
            {
                if (!Select(id, commit)) return "EQUIPMENT_STORE_UNAVAILABLE";
                _revision++;
                return null;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return "EQUIPMENT_STORE_UNAVAILABLE"; }
        }
    }
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

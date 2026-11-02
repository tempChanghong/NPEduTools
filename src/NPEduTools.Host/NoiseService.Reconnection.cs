using System.Text.Json;
using NPEduTools.Contracts;

namespace NPEduTools.Host;

public sealed partial class NoiseService
{
    private sealed record ReconnectMarker(int Version, string ProfileId, string Reason);
    private bool _referenceReconnectPending;
    private bool _referenceMarkerRecoverable;
    internal bool RecoverableDeviceFailure(Guid? session)
    { lock (_sync) return session is not null && _session is { DeviceDisconnected: true, Work.IsCompleted: true } s &&
        s.Id == session && !_cleanupFailed && _state == "Faulted"; }

    private void DeviceDisconnected(Session s)
    {
        s.DeviceDisconnected = true;
        s.Failure = "所选麦克风已断开，等待原设备重新连接；未删除已保存的校准配置。";
        s.CalibrationInterrupted = s.Calibration is not null || s.Reference is not null;
        s.EstimateCurrent = null;
        if (s.EstimateProfile is not null) s.EstimateReason ??= "INPUT_SETTINGS_UNAVAILABLE";
        _lastSignature = _lastReferenceSignature = null; _referenceSignatureAt = null;
        _studyThresholds.Configure(null, null);
        s.Stop.Cancel();
    }

    private bool ReconnectMarkerMatches(string path, NoiseReferenceProfile profile)
    {
        try
        {
            if (new FileInfo(path).Length > 4096) return false;
            var marker = JsonSerializer.Deserialize<ReconnectMarker>(File.ReadAllText(path), Protocol.Json);
            return marker is { Version: 1, Reason: "DeviceDisconnected" } && marker.ProfileId == ProfileId(profile);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) { return false; }
    }

    private bool ReplaceReferenceMarker(bool reconnect)
    {
        if (_referenceError is not null || _referenceProfile is not { } profile || ReferenceUsePath is not { } path) return false;
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            {
                JsonSerializer.Serialize(stream, new ReconnectMarker(1, ProfileId(profile), reconnect ? "DeviceDisconnected" : "InUse"), Protocol.Json);
                stream.Flush(true);
            }
            File.Move(temporary, path, true); _referenceMarkerArmed = true; _referenceMarkerRecoverable = reconnect; return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        { _referenceError = "REFERENCE_STORE_UNAVAILABLE"; return false; }
        finally { try { File.Delete(temporary); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
    }
}

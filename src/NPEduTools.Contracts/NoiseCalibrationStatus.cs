namespace NPEduTools.Contracts;

// Compact, read-only observation. Saved does not certify the next capture's input settings.
public sealed record NoiseCalibrationStatus(Guid InstanceId, long NoiseRevision, string State,
    string Method, string? Reason = null, DateTimeOffset? ConfirmedAt = null);

public static class NoiseCalibrationStatusContract
{
    public static string? BaseCapability(string capability) => capability switch
    {
        "study.status.calibration" => "study.status.photos",
        "manual.study.status.calibration" => "manual.study.status",
        "noise.status.calibration" => "noise.status",
        "noise.devices.calibration" => "noise.devices",
        _ => null
    };
}

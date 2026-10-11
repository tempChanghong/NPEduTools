namespace NPEduTools.Contracts;

public sealed record StudyCameraDevice(string Id, string Name);
public sealed record StudyCameraCommand(string Action, Guid InstanceId, long Revision, string? DeviceId = null);
public sealed record StudyDeviceState(Guid InstanceId, long Revision, string? CameraId, string? CameraName,
    bool? CameraAvailable, IReadOnlyList<StudyCameraDevice> Cameras, string? Error = null, bool Locked = false);
public static class StudyDeviceContract
{
    public static bool Valid(StudyCameraCommand c) => c.InstanceId != Guid.Empty && c.Revision >= 0 &&
        (c.Action == "select" ? NoiseContract.ValidDeviceId(c.DeviceId) : c.Action == "clear" && c.DeviceId is null);
}

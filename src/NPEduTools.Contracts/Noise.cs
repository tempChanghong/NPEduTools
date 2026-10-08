namespace NPEduTools.Contracts;

public sealed record NoiseDevice(string Id, string Name);
// Instance + revision fence delayed commands, including an old UI reconnecting to a new Host.
public sealed record NoiseCommand(string Action, Guid InstanceId, long Revision, string? DeviceId = null);
public sealed record NoisePoint(double Seconds, double? Dbfs, string Quality);
public sealed record NoiseSummary(double ElapsedSeconds, double SampledSeconds, double Coverage,
    double? EnergyMeanDbfs, double? PeakDbfs, double ClippedPercent, long Frames);
public sealed record NoiseState(Guid InstanceId, long Revision, string State, string Message,
    string? DeviceId, string? DeviceName, DateTimeOffset? StartedAt, double? CurrentDbfs,
    string Quality, NoiseSummary? Summary, IReadOnlyList<NoisePoint> Trend,
    string Algorithm = "pcm-energy-v1", Guid? SessionId = null, string? SelectedDeviceId = null);

public static class NoiseContract
{
    public static bool ValidDeviceId(string? id) => !string.IsNullOrWhiteSpace(id) && id.Length <= 2048 && !id.Any(char.IsControl);
    public static bool Valid(NoiseCommand command) => command.InstanceId != Guid.Empty && command.Revision >= 0 &&
        (command.Action is "start" or "select"
            ? ValidDeviceId(command.DeviceId)
            : command.Action == "stop" && command.DeviceId is null);
}

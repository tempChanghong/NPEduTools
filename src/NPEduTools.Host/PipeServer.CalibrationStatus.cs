using NPEduTools.Contracts;

namespace NPEduTools.Host;

public sealed partial class PipeServer
{
    private HostResponse WithCalibrationStatus(bool requested, HostResponse response)
    {
        if (!requested || response.Outcome != "Succeeded" || response.Noise is null || noise is null) return response;
        var context = study?.CachedCalibrationContext;
        var status = noise.CalibrationStatusSnapshot(context?.Scope, context?.State);
        // Do not attach an observation from another revision if a concurrent device edit intervened.
        return status.InstanceId == response.Noise.InstanceId && status.NoiseRevision == response.Noise.Revision
            ? response with { NoiseCalibrationStatus = status } : response;
    }
}

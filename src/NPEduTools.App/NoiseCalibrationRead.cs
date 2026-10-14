using NPEduTools.Contracts;

namespace NPEduTools.App;

// Opt in to compact metadata on the existing status read. Older strict Hosts keep their wire shape.
internal sealed class NoiseCalibrationRead(TimeProvider? clock = null)
{
    private readonly TimeProvider _time = clock ?? TimeProvider.System;
    private readonly Dictionary<string, long> _unsupported = [];
    public Task<HostResponse> RequestAsync(string pipe, HostRequest request, CancellationToken token) =>
        RequestAsync(request, next => HostClient.RequestAsync(pipe, next, token));
    public async Task<HostResponse> RequestAsync(HostRequest request, Func<HostRequest, Task<HostResponse>> send)
    {
        string? capability = request.Capability switch
        {
            "study.status" or "study.status.photos" => "study.status.calibration",
            "manual.study.status" => "manual.study.status.calibration",
            "noise.status" => "noise.status.calibration",
            "noise.devices" => "noise.devices.calibration",
            _ => null
        };
        if (capability is null || _unsupported.TryGetValue(capability, out long at) && _time.GetElapsedTime(at).TotalSeconds < 60)
            return await send(request);
        var reply = await send(request with { Capability = capability });
        if (reply.Outcome != "Rejected" || reply.ErrorCode != "UnknownCapability") return reply;
        _unsupported[capability] = _time.GetTimestamp();
        // Unsupported metadata does not mean uncalibrated, nor invalidate the ordinary status.
        return await send(request);
    }
}

using NPEduTools.Contracts;

namespace NPEduTools.Core;

/// <summary>Negotiates local observation recycling without changing the display's lifetime.</summary>
public sealed class StudyPresentationReporter
{
    private Guid _instance;
    private Guid? _epoch;
    private bool _legacy, _negotiated;
    private int _busy;

    public async Task<HostResponse?> ReportAsync(Func<StudyPresentationPulse?> sample,
        Func<HostRequest, CancellationToken, Task<HostResponse>> send, bool manual, CancellationToken token)
    {
        if (Interlocked.Exchange(ref _busy, 1) != 0) return null;
        try
        {
            if (sample() is not { } pulse) return null;
            if (_instance != pulse.InstanceId)
            {
                _instance = pulse.InstanceId;
                _epoch = null;
                _legacy = false;
            }
            string capability = manual ? "manual.study.presentation" : "study.presentation";
            if (_epoch is null && !_legacy)
            {
                // No new payload on the probe: old strict Hosts can return UnknownCapability.
                var reply = await send(new(1, Guid.NewGuid(), capability + ".epoch"), token);
                if (reply.Outcome == "Succeeded" && reply.PresentationEpoch is { } registration &&
                    registration.InstanceId == _instance && registration.Epoch != Guid.Empty)
                {
                    _epoch = registration.Epoch;
                    _negotiated = true;
                }
                else if (!_negotiated && reply is { Outcome: "Rejected", ErrorCode: "UnknownCapability" })
                    _legacy = true;
                else return reply;
                // Pipe negotiation may outlive a frame or stage; never reuse the pre-probe pulse.
                if (sample() is not { } fresh || fresh.InstanceId != _instance) return null;
                pulse = fresh;
            }
            var response = await send(new(1, Guid.NewGuid(), capability,
                StudyPresentation: pulse with { ObservationEpoch = _epoch }), token);
            if (response.ErrorCode == "STUDY_OBSERVATION_CHANGED")
            {
                _epoch = null;
                _legacy = false;
            }
            return response;
        }
        finally { Volatile.Write(ref _busy, 0); }
    }
}

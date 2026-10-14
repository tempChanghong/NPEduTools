using NPEduTools.Contracts;

namespace NPEduTools.Host;

public sealed partial class PipeServer
{
    private HostResponse WithStudyConnection(HostRequest request, HostResponse response)
    {
        if (response.Outcome != "Succeeded" || response.Study is null ||
            request.Capability is not ("study.status" or "study.status.seating" or "study.status.photos" or "manual.study.status")) return response;
        // Piggyback the existing local snapshot; no HTTP request, new capability or response field.
        // Do not expose pairing/catalogue metadata to the fullscreen surface.
        var state = npep?.Snapshot();
        return response with { Npep = state is null ? null : new(state.Revision, state.State, state.Connection,
            "学校互联状态。", state.Busy, ReportingPaused: state.ReportingPaused, LastReceivedAt: state.LastReceivedAt, Error: state.Error) };
    }
}

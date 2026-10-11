using System.Diagnostics;
using System.Text.Json.Nodes;

namespace NPEduTools.Integrations.Npep;

public sealed partial class NpepRuntime
{
    private Task _studyPresentationLoop = Task.CompletedTask;
    private long _studyPresentationSequence;
    private void InitializeStudyPresentation(INpepStudySettings study)
    {
        if (!study.SupportsPresentation) return;
        _studyPresentationLoop = Task.Run(async () =>
        {
            long? unsupportedAt = null;
            try
            {
                while (!_lifetime.IsCancellationRequested)
                {
                    try
                    {
                        if ((unsupportedAt is null || Stopwatch.GetElapsedTime(unsupportedAt.Value).TotalSeconds >= 60) &&
                            Snapshot() is { State: "ACTIVE", Connection: "ONLINE", ReportingPaused: false, Busy: false })
                        { await StudyPresentationCycleAsync(study, _lifetime.Token); unsupportedAt = null; }
                    }
                    catch (NpepException e) when (e.Code == "STUDY_PRESENTATION_UNSUPPORTED") { unsupportedAt = Stopwatch.GetTimestamp(); }
                    catch (Exception e) when (Handled(e) || e is InvalidDataException) { } // Observation failure never grants/stops capture.
                    await Task.Delay(TimeSpan.FromSeconds(1), _lifetime.Token);
                }
            }
            catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        });
    }
    internal async Task StudyPresentationCycleAsync(INpepStudySettings study, CancellationToken token)
    {
        var context = await StudyContextAsync(token);
        await _device!.StudyPresentationAsync(() =>
        {
            if (Snapshot() is not { State: "ACTIVE", Connection: "ONLINE", ReportingPaused: false, Busy: false } ||
                study.ObserveStudyPresentation() is not { } observation) return null;
            return new JsonObject { ["requestId"] = NpepProtocol.Id(), ["context"] = context.Copy(), ["sequence"] = ++_studyPresentationSequence,
                ["version"] = observation["version"]!.DeepClone(), ["status"] = observation["status"]!.DeepClone() };
        }, token);
        await RequireStudyContextAsync(context, token);
    }
}

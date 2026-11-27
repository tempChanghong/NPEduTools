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
            StudyRetry? retry = null;
            try
            {
                while (!_lifetime.IsCancellationRequested)
                {
                    var state = Snapshot();
                    string key = state.Revision.ToString(System.Globalization.CultureInfo.InvariantCulture);
                    if (state is { State: "ACTIVE", Connection: "ONLINE", ReportingPaused: false, Busy: false } && !StudyRetryPending(retry, key))
                    {
                        try
                        {
                            await StudyPresentationCycleAsync(study, _lifetime.Token);
                            retry = null;
                        }
                        catch (NpepException e) when (e.Code == "STUDY_PRESENTATION_UNSUPPORTED")
                        { retry = new(key, _receiptTime.GetTimestamp(), TimeSpan.FromSeconds(60)); }
                        catch (NpepException e) when (e.Status is 429 or >= 500)
                        { retry = NewStudyRetry(key, e); }
                        catch (NpepException e) when (e.Status == 401 && Snapshot().Revision == state.Revision)
                        { Publish("STOPPED", "设备授权已失效，自习展示回执已停止。", e.Code, requireSuspended: true); }
                        catch (Exception e) when (Handled(e) || e is InvalidDataException) { } // An observation cannot grant or stop capture.
                    }
                    await Task.Delay(TimeSpan.FromSeconds(1), _lifetime.Token);
                }
            }
            catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        });
    }
    internal async Task StudyPresentationCycleAsync(INpepStudySettings study, CancellationToken token)
    {
        long connectionRevision = Snapshot().Revision;
        var context = await StudyContextAsync(token);
        void RequireOriginalConnection()
        {
            token.ThrowIfCancellationRequested();
            if (Snapshot() is not { State: "ACTIVE", Connection: "ONLINE", ReportingPaused: false, Busy: false } current ||
                current.Revision != connectionRevision)
                throw new NpepException("SESSION_SUPERSEDED");
        }
        await _device!.StudyPresentationAsync(context, () =>
        {
            RequireOriginalConnection();
            if (study.ObserveStudyPresentation() is not { } observation) return null;
            RequireOriginalConnection();
            return new JsonObject { ["requestId"] = NpepProtocol.Id(), ["context"] = context.Copy(), ["sequence"] = ++_studyPresentationSequence,
                ["version"] = observation["version"]!.DeepClone(), ["status"] = observation["status"]!.DeepClone() };
        }, token);
        RequireOriginalConnection();
    }
}

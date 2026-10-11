using System.Text.Json.Nodes;

namespace NPEduTools.Integrations.Npep;

public sealed partial class NpepRuntime
{
    private Task _studyLoop = Task.CompletedTask;
    private INpepStudySettings? _study;
    private long _studySequence, _studyExecutionSequence, _studyResumeSequence;
    private void InitializeStudy(INpepStudySettings? study)
    {
        _study = study;
        if (study is null || _device is null) return;
        InitializeStudySeating(study);
        InitializeStudyHomework(study);
        InitializeStudyPresentation(study);
        InitializeStudyPhotos(study);
        InitializeStudyExit(study);
        _studyLoop = Task.Run(async () =>
        {
            try
            {
                while (!_lifetime.IsCancellationRequested)
                {
                    try
                    {
                        if (Snapshot() is { State: "ACTIVE", Connection: "ONLINE", ReportingPaused: false, Busy: false })
                            await StudyCycleAsync(study, _lifetime.Token);
                        else if (Snapshot() is { State: "UNPAIRED" or "SUSPENDED" } or { ReportingPaused: true } or { Busy: true }) study.BindStudy(null);
                        else study.StudyFailed("STUDY_OFFLINE");
                    }
                    catch (Exception e) when (Handled(e) || e is InvalidDataException)
                    { study.StudyFailed(e is NpepException n ? n.Code : "STUDY_OFFLINE"); }
                    await Task.Delay(TimeSpan.FromSeconds(3), _lifetime.Token);
                }
            }
            catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
            finally { study.BindStudy(null); }
        });
    }
    private async Task<JsonObject> StudyContextAsync(CancellationToken token)
    {
        var context = await _device!.ControlContextAsync(Guid.Empty, token);
        context.Remove("controlEpoch"); return context;
    }
    private async Task RequireStudyContextAsync(JsonObject context, CancellationToken token)
    {
        if (Snapshot() is not { State: "ACTIVE", Connection: "ONLINE", ReportingPaused: false, Busy: false })
            throw new NpepException("SESSION_SUPERSEDED");
        JsonObject current;
        try { current = await StudyContextAsync(token); }
        catch (NpepException e) when (e.Code == "DEVICE_SUSPENDED" && Snapshot() is not { State: "ACTIVE", Connection: "ONLINE", ReportingPaused: false, Busy: false })
        { throw new NpepException("SESSION_SUPERSEDED"); }
        if (!JsonNode.DeepEquals(context, current) || Snapshot() is not { State: "ACTIVE", Connection: "ONLINE", ReportingPaused: false, Busy: false })
            throw new NpepException("SESSION_SUPERSEDED");
    }
    internal async Task StudyCycleAsync(INpepStudySettings study, CancellationToken token)
    {
        var context = await StudyContextAsync(token);
        study.BindStudy(context["identity"]!.ToJsonString());
        var configuration = await _device!.StudyAsync(null, token);
        await RequireStudyContextAsync(context, token);
        if (!JsonNode.DeepEquals(context["identity"], configuration["identity"])) throw new NpepException("SESSION_SUPERSEDED");
        study.ApplyStudy(configuration);
        var status = study.ObserveStudy();
        if (status is null) return;
        var body = new JsonObject { ["requestId"] = NpepProtocol.Id(), ["context"] = context.Copy(), ["sequence"] = ++_studySequence, ["status"] = status };
        var reply = await _device.StudyAsync(body, token);
        await RequireStudyContextAsync(context, token);
        if (!reply["confirmed"]!.GetValue<bool>() || reply.Text("version") != configuration.Text("version")) throw new NpepException("POLICY_CHANGED");
        if (study.SupportsExecution && configuration["effective"]!.AsObject().Text("source") != "None")
        {
            var execution = study.ObserveStudyExecution();
            if (execution is null) throw new NpepException("STUDY_NOT_APPLIED");
            var sentAt = System.Diagnostics.Stopwatch.GetTimestamp();
            var grant = await _device.StudyExecutionAsync(new JsonObject { ["requestId"] = NpepProtocol.Id(), ["context"] = context.Copy(),
                ["sequence"] = ++_studyExecutionSequence, ["version"] = configuration["version"]!.DeepClone(), ["status"] = execution }, token);
            await RequireStudyContextAsync(context, token);
            study.ConfirmStudyExecution(configuration, grant, System.Diagnostics.Stopwatch.GetElapsedTime(sentAt));
            if (study.SupportsResume)
            {
                try
                {
                    sentAt = System.Diagnostics.Stopwatch.GetTimestamp();
                    var resume = await _device.StudyResumeAsync(new JsonObject { ["requestId"] = NpepProtocol.Id(), ["context"] = context.Copy(),
                        ["sequence"] = ++_studyResumeSequence, ["version"] = configuration["version"]!.DeepClone(),
                        ["receipt"] = study.ObserveStudyResumeReceipt() }, token);
                    await RequireStudyContextAsync(context, token);
                    if (resume["confirmed"]!.GetValue<bool>() && resume.Text("version") == configuration.Text("version") && resume["command"] is JsonObject command)
                        study.ApplyStudyResume(configuration, command, System.Diagnostics.Stopwatch.GetElapsedTime(sentAt));
                }
                catch (NpepException e) when (e.Code == "STUDY_RESUME_UNSUPPORTED") { } // A 0.10 server retains capture and display compatibility.
            }
        }
    }
}

using System.Text.Json.Nodes;

namespace NPEduTools.Integrations.Npep;

public sealed partial class NpepRuntime
{
    private Task _studyHomeworkLoop = Task.CompletedTask;
    private void InitializeStudyHomework(INpepStudySettings study)
    {
        _studyHomeworkLoop = Task.Run(async () =>
        {
            string? lastRequest = null;
            long lastAt = 0;
            try
            {
                while (!_lifetime.IsCancellationRequested)
                {
                    var request = study.StudyHomeworkRequest();
                    if (Snapshot() is { State: "ACTIVE", Connection: "ONLINE", ReportingPaused: false, Busy: false } && request is not null)
                    {
                        string key = request.ToJsonString();
                        if (key != lastRequest || System.Diagnostics.Stopwatch.GetElapsedTime(lastAt).TotalSeconds >= 30)
                        {
                            lastRequest = key; lastAt = System.Diagnostics.Stopwatch.GetTimestamp();
                            try { await StudyHomeworkCycleAsync(study, request, _lifetime.Token); }
                            catch (Exception e) when (Handled(e) || e is InvalidDataException or FormatException)
                            { study.StudyHomeworkFailed(e is NpepException n ? n.Code : "HOMEWORK_OFFLINE"); }
                        }
                    }
                    else { lastRequest = null; study.StudyHomeworkFailed("HOMEWORK_OFFLINE"); }
                    await Task.Delay(TimeSpan.FromSeconds(3), _lifetime.Token);
                }
            }
            catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        });
    }
    internal async Task StudyHomeworkCycleAsync(INpepStudySettings study, JsonObject request, CancellationToken token)
    {
        var context = await StudyContextAsync(token);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token); deadline.CancelAfter(TimeSpan.FromSeconds(30));
        using var buffer = new MemoryStream(); string? snapshot = null; int count = 1;
        for (int index = 0; index < count; index++)
        {
            var body = new JsonObject { ["requestId"] = NpepProtocol.Id(), ["context"] = context.Copy(),
                ["version"] = request["version"]!.DeepClone(), ["date"] = request["date"]!.DeepClone(), ["snapshotId"] = snapshot, ["index"] = index };
            if (request["purpose"] is { } purpose) body["purpose"] = purpose.DeepClone();
            var reply = await _device!.StudyHomeworkAsync(body, deadline.Token);
            await RequireStudyContextAsync(context, deadline.Token);
            if (reply["index"]!.GetValue<int>() != index || snapshot is not null && (reply.Text("snapshotId") != snapshot || reply["count"]!.GetValue<int>() != count))
                throw new NpepException("INVALID_RESPONSE");
            snapshot = reply.Text("snapshotId"); count = reply["count"]!.GetValue<int>();
            var bytes = Convert.FromBase64String(reply.Text("data"));
            if (bytes.Length is <= 0 or > 16384 || index < count - 1 && bytes.Length != 16384 || buffer.Length + bytes.Length > 2097152)
                throw new NpepException("INVALID_RESPONSE");
            buffer.Write(bytes);
        }
        var complete = buffer.ToArray();
        if (Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(complete)) != snapshot) throw new NpepException("INVALID_RESPONSE");
        var payload = NpepProtocol.Parse(complete); NpepStudyProtocol.Validate("homeworkSnapshot", payload);
        if (payload.Text("version") != request.Text("version") || payload.Text("date") != request.Text("date")) throw new NpepException("INVALID_RESPONSE");
        study.ApplyStudyHomework(payload, complete, snapshot!);
    }
}

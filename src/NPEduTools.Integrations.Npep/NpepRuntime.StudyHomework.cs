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
            double delay = 30;
            try
            {
                while (!_lifetime.IsCancellationRequested)
                {
                    var request = study.StudyHomeworkRequest();
                    if (Snapshot() is { State: "ACTIVE", Connection: "ONLINE", ReportingPaused: false, Busy: false } state && request is not null)
                    {
                        string key = state.Revision + ":" + request.ToJsonString();
                        if (key != lastRequest || _receiptTime.GetElapsedTime(lastAt).TotalSeconds >= delay)
                        {
                            lastRequest = key; lastAt = _receiptTime.GetTimestamp(); delay = 30;
                            try { await StudyHomeworkCycleAsync(study, request, _lifetime.Token); }
                            catch (Exception e) when (!_lifetime.IsCancellationRequested && (Handled(e) || e is InvalidDataException or FormatException))
                            {
                                lock (_sync)
                                {
                                    if (_state.Revision == state.Revision)
                                    {
                                        bool temporary = TransientStudyExchangeFailure(e);
                                        if (temporary || e is NpepException { Status: 409, Code: "HOMEWORK_CHANGED" })
                                        {
                                            lastAt = _receiptTime.GetTimestamp();
                                            delay = Math.Max(6, (e as NpepException)?.RetryAfterSeconds ?? 0);
                                        }
                                        study.StudyHomeworkFailed(temporary ? "HOMEWORK_REFRESH_UNAVAILABLE" :
                                            e is NpepException n ? n.Code : "HOMEWORK_OFFLINE");
                                    }
                                }
                            }
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
        long revision = Snapshot().Revision;
        var context = await StudyContextAsync(token);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token); deadline.CancelAfter(TimeSpan.FromSeconds(30));
        async Task RequireOriginalConnectionAsync()
        {
            await RequireStudyContextAsync(context, deadline.Token);
            if (Snapshot().Revision != revision) throw new NpepException("SESSION_SUPERSEDED");
        }

        bool canRestart = true;
        while (true)
        {
            using var buffer = new MemoryStream();
            string? snapshot = null;
            int count = 1;
            try
            {
                await RequireOriginalConnectionAsync();
                for (int index = 0; index < count; index++)
                {
                    var body = new JsonObject { ["requestId"] = NpepProtocol.Id(), ["context"] = context.Copy(),
                        ["version"] = request["version"]!.DeepClone(), ["date"] = request["date"]!.DeepClone(), ["snapshotId"] = snapshot, ["index"] = index };
                    if (request["purpose"] is { } purpose) body["purpose"] = purpose.DeepClone();
                    var reply = await _device!.StudyHomeworkAsync(body, deadline.Token);
                    await RequireOriginalConnectionAsync();
                    if (reply.Number("index") != index || snapshot is not null && (reply.Text("snapshotId") != snapshot || reply.Number("count") != count))
                        throw new NpepException("INVALID_RESPONSE");
                    snapshot = reply.Text("snapshotId"); count = reply["count"]!.GetValue<int>();
                    var bytes = Convert.FromBase64String(reply.Text("data"));
                    if (bytes.Length is <= 0 or > 16384 || index < count - 1 && bytes.Length != 16384 || buffer.Length + bytes.Length > 2097152)
                        throw new NpepException("INVALID_RESPONSE");
                    buffer.Write(bytes);
                }
            }
            catch (NpepException e) when (canRestart && snapshot is not null && e is { Status: 409, Code: "HOMEWORK_CHANGED" })
            {
                // The teacher changed the source during the read. Discard the old fragments and use the same deadline.
                canRestart = false;
                continue;
            }

            var complete = buffer.ToArray();
            if (Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(complete)) != snapshot) throw new NpepException("INVALID_RESPONSE");
            var payload = NpepProtocol.Parse(complete); NpepStudyProtocol.Validate("homeworkSnapshot", payload);
            if (payload.Text("version") != request.Text("version") || payload.Text("date") != request.Text("date")) throw new NpepException("INVALID_RESPONSE");
            lock (_sync)
            {
                deadline.Token.ThrowIfCancellationRequested();
                if (Snapshot() is not { State: "ACTIVE", Connection: "ONLINE", ReportingPaused: false, Busy: false } current || current.Revision != revision)
                    throw new NpepException("SESSION_SUPERSEDED");
                study.ApplyStudyHomework(payload, complete, snapshot!);
            }
            return;
        }
    }
}

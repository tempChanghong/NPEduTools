using System.Text.Json.Nodes;
namespace NPEduTools.Integrations.Npep;
public sealed partial class NpepRuntime
{
    private Task _studySeatingLoop = Task.CompletedTask;
    private void InitializeStudySeating(INpepStudySettings study)
    {
        _studySeatingLoop = Task.Run(async () => {
            long? unsupportedAt = null;
            try {
                while (!_lifetime.IsCancellationRequested) {
                    try {
                        if (Snapshot() is { State: "ACTIVE", Connection: "ONLINE", ReportingPaused: false, Busy: false } && study.StudySeatingRequest() is { } request &&
                            (unsupportedAt is null || System.Diagnostics.Stopwatch.GetElapsedTime(unsupportedAt.Value).TotalSeconds >= 60))
                        { await SeatingCycleAsync(study, request, _lifetime.Token); unsupportedAt = null; }
                    } catch (Exception e) when (Handled(e) || e is InvalidDataException or FormatException) {
                        study.SeatingFailed(e is NpepException n ? n.Code : "SEATING_OFFLINE");
                        if (e is NpepException { Code: "STUDY_SEATING_UNSUPPORTED" }) unsupportedAt = System.Diagnostics.Stopwatch.GetTimestamp();
                    }
                    await Task.Delay(TimeSpan.FromSeconds(3), _lifetime.Token);
                }
            } catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        });
    }
    internal async Task SeatingCycleAsync(INpepStudySettings study, JsonObject request, CancellationToken token)
    {
        var context = await StudyContextAsync(token);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token); deadline.CancelAfter(TimeSpan.FromSeconds(30));
        var manifest = await _device!.SeatingAsync(() => new JsonObject { ["requestId"] = NpepProtocol.Id(), ["context"] = context.Copy(),
            ["version"] = request["version"]!.DeepClone(), ["observation"] = study.ObserveSeating(), ["downloaded"] = study.ObserveSeatingDownload() }, false, deadline.Token);
        await RequireStudyContextAsync(context, deadline.Token);
        if (manifest.Text("version") != request.Text("version")) throw new NpepException("POLICY_CHANGED");
        if (manifest["asset"] is not JsonObject asset) { study.ApplySeating(manifest, null); return; }
        string hash = asset.Text("hash"); int revision = manifest["revision"]!.GetValue<int>();
        if (study.HasSeating(request.Text("version"), hash, revision)) { study.ApplySeating(manifest, null); return; }
        using var buffer = new MemoryStream(); int count = asset["chunks"]!.GetValue<int>();
        for (int index = 0; index < count; index++) {
            var body = new JsonObject { ["requestId"] = NpepProtocol.Id(), ["context"] = context.Copy(), ["version"] = request["version"]!.DeepClone(),
                ["hash"] = hash, ["revision"] = revision, ["index"] = index };
            var reply = await _device.SeatingAsync(() => body, true, deadline.Token);
            await RequireStudyContextAsync(context, deadline.Token);
            if (reply.Text("hash") != hash || reply["revision"]!.GetValue<int>() != revision || reply["index"]!.GetValue<int>() != index || reply["count"]!.GetValue<int>() != count)
                throw new NpepException("INVALID_RESPONSE");
            var bytes = Convert.FromBase64String(reply.Text("data"));
            if (bytes.Length is <= 0 or > 16384 || index < count - 1 && bytes.Length != 16384 || buffer.Length + bytes.Length > asset["byteCount"]!.GetValue<int>())
                throw new NpepException("INVALID_RESPONSE");
            buffer.Write(bytes);
        }
        var complete = buffer.ToArray();
        if (complete.Length != asset["byteCount"]!.GetValue<int>() || Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(complete)) != hash) throw new NpepException("INVALID_RESPONSE");
        study.ApplySeating(manifest, complete);
    }
}

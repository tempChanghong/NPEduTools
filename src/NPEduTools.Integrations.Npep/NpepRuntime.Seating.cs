using System.Text.Json.Nodes;
namespace NPEduTools.Integrations.Npep;
public sealed partial class NpepRuntime
{
    private Task _studySeatingLoop = Task.CompletedTask;
    // At most one bounded image is staged, never reported as downloaded or shown
    // before all bytes and the final hash have been verified.
    private sealed class SeatingDownload(JsonObject context, JsonObject manifest, int byteCount)
    {
        public JsonObject Context { get; } = context.Copy();
        public JsonObject Manifest { get; } = manifest.Copy();
        public byte[] Bytes { get; } = new byte[byteCount];
        public int Next;
    }
    private SeatingDownload? _seatingDownload;
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
                        else if (Snapshot() is { State: "UNPAIRED" or "SUSPENDED" } or { ReportingPaused: true } || Snapshot().Busy && !ContinuingStudyResume) _seatingDownload = null;
                    } catch (Exception e) when (Handled(e) || e is InvalidDataException or FormatException) {
                        study.SeatingFailed(e is NpepException n ? n.Code : "SEATING_OFFLINE");
                        if (e is NpepException { Code: "STUDY_SEATING_UNSUPPORTED" }) unsupportedAt = System.Diagnostics.Stopwatch.GetTimestamp();
                    }
                    await Task.Delay(TimeSpan.FromSeconds(3), _lifetime.Token);
                }
            } catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
            finally { _seatingDownload = null; }
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
        if (manifest["asset"] is not JsonObject asset) { _seatingDownload = null; study.ApplySeating(manifest, null); return; }
        string hash = asset.Text("hash"); int revision = manifest["revision"]!.GetValue<int>();
        if (study.HasSeating(request.Text("version"), hash, revision)) { _seatingDownload = null; study.ApplySeating(manifest, null); return; }
        int count = asset["chunks"]!.GetValue<int>(), byteCount = asset["byteCount"]!.GetValue<int>();
        if (_seatingDownload is not { } existing || !JsonNode.DeepEquals(existing.Context, context) || !JsonNode.DeepEquals(existing.Manifest, manifest))
            _seatingDownload = new(context, manifest, byteCount);
        var download = _seatingDownload;
        for (int index = download.Next; index < count; index++) {
            var body = new JsonObject { ["requestId"] = NpepProtocol.Id(), ["context"] = context.Copy(), ["version"] = request["version"]!.DeepClone(),
                ["hash"] = hash, ["revision"] = revision, ["index"] = index };
            var reply = await _device.SeatingAsync(() => body, true, deadline.Token);
            await RequireStudyContextAsync(context, deadline.Token);
            if (reply.Text("hash") != hash || reply["revision"]!.GetValue<int>() != revision || reply["index"]!.GetValue<int>() != index || reply["count"]!.GetValue<int>() != count)
                throw new NpepException("INVALID_RESPONSE");
            var bytes = Convert.FromBase64String(reply.Text("data"));
            if (bytes.Length != Math.Min(16384, byteCount - index * 16384))
                throw new NpepException("INVALID_RESPONSE");
            bytes.CopyTo(download.Bytes, index * 16384);
            download.Next = index + 1;
        }
        if (Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(download.Bytes)) != hash)
        { _seatingDownload = null; throw new NpepException("INVALID_RESPONSE"); }
        study.ApplySeating(manifest, download.Bytes);
        _seatingDownload = null;
    }
}

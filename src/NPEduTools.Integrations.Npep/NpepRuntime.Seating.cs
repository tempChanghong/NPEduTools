using System.Text.Json.Nodes;
namespace NPEduTools.Integrations.Npep;
public sealed partial class NpepRuntime
{
    private Task _studySeatingLoop = Task.CompletedTask;
    // At most one bounded image is staged, never reported as downloaded or shown
    // before all bytes and the final hash have been verified.
    private sealed class SeatingDownload(long connectionRevision, JsonObject context, JsonObject manifest, int byteCount)
    {
        private readonly JsonObject _context = context.Copy();
        private readonly JsonObject _manifest = manifest.Copy();
        public byte[] Bytes { get; } = new byte[byteCount];
        public int Next;

        // Optional display telemetry negotiation is not part of the image identity.
        public bool Matches(long revision, JsonObject connection, JsonObject current) =>
            revision == connectionRevision && JsonNode.DeepEquals(_context, connection) &&
            _manifest.Text("version") == current.Text("version") && _manifest.Text("classId") == current.Text("classId") &&
            _manifest.Number("revision") == current.Number("revision") && JsonNode.DeepEquals(_manifest["asset"], current["asset"]);
    }
    private SeatingDownload? _seatingDownload;
    private JsonObject? _seatingSequenceContext;
    private bool _seatingSupportsHostSequence;
    private void InitializeStudySeating(INpepStudySettings study)
    {
        _studySeatingLoop = Task.Run(async () =>
        {
            StudyRetry? retry = null;
            try
            {
                while (!_lifetime.IsCancellationRequested)
                {
                    var state = Snapshot();
                    if (state is { State: "ACTIVE", Connection: "ONLINE", ReportingPaused: false, Busy: false } && study.StudySeatingRequest() is { } request)
                    {
                        string key = state.Revision + ":" + request.Text("version");
                        if (!StudyRetryPending(retry, key))
                        {
                            try { await SeatingCycleAsync(study, request, _lifetime.Token); retry = null; }
                            catch (Exception e) when (!_lifetime.IsCancellationRequested && (Handled(e) || e is InvalidDataException or FormatException))
                            {
                                lock (_sync)
                                {
                                    if (_state.Revision == state.Revision)
                                    {
                                        if (e is NpepException { Code: "STUDY_SEATING_UNSUPPORTED" })
                                            retry = new(key, _receiptTime.GetTimestamp(), TimeSpan.FromSeconds(60));
                                        else if (e is NpepException { Status: 429 or >= 500 }) retry = NewStudyRetry(key, e);
                                        study.SeatingFailed(e is NpepException n ? n.Code : "SEATING_OFFLINE");
                                    }
                                }
                                if (e is NpepException { Status: 401 } refusal && Snapshot().Revision == state.Revision)
                                    Publish("STOPPED", "设备授权已失效，座位表同步已停止。", refusal.Code, requireSuspended: true);
                            }
                        }
                    }
                    else if (state is { State: "UNPAIRED" or "SUSPENDED" } or { ReportingPaused: true } || state.Busy && !ContinuingStudyResume)
                        _seatingDownload = null;
                    await Task.Delay(TimeSpan.FromSeconds(3), _lifetime.Token);
                }
            }
            catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
            finally { _seatingDownload = null; }
        });
    }
    internal async Task SeatingCycleAsync(INpepStudySettings study, JsonObject request, CancellationToken token)
    {
        long connectionRevision = Snapshot().Revision;
        var context = await StudyContextAsync(token);
        if (!JsonNode.DeepEquals(_seatingSequenceContext, context)) {
            _seatingSequenceContext = context.Copy(); _seatingSupportsHostSequence = false;
        }
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token); deadline.CancelAfter(TimeSpan.FromSeconds(30));
        void RequireOriginalConnection()
        {
            deadline.Token.ThrowIfCancellationRequested();
            if (Snapshot() is not { State: "ACTIVE", Connection: "ONLINE", ReportingPaused: false, Busy: false } current || current.Revision != connectionRevision)
                throw new NpepException("SESSION_SUPERSEDED");
        }
        void Apply(JsonObject manifest, byte[]? bytes)
        {
            lock (_sync)
            {
                RequireOriginalConnection();
                study.ApplySeating(manifest, bytes);
            }
        }
        async Task SyncOnceAsync()
        {
            JsonObject Body()
            {
                RequireOriginalConnection();
                var observation = study.ObserveSeating();
                if (!_seatingSupportsHostSequence) observation?.Remove("hostSequence");
                return new() { ["requestId"] = NpepProtocol.Id(), ["context"] = context.Copy(),
                    ["version"] = request["version"]!.DeepClone(), ["observation"] = observation, ["downloaded"] = study.ObserveSeatingDownload() };
            }
            JsonObject manifest;
            try { manifest = await _device!.ExchangeSeatingManifestAsync(context, Body, deadline.Token); }
            catch (NpepException e) when (_seatingSupportsHostSequence && e is { Status: 400, Code: "INVALID_REQUEST" })
            {
                // Next poll renegotiates without the optional field, using a fresh observation.
                RequireOriginalConnection();
                _seatingSupportsHostSequence = false; throw;
            }
            await RequireStudyContextAsync(context, deadline.Token);
            RequireOriginalConnection();
            if (manifest.Text("version") != request.Text("version")) throw new NpepException("POLICY_CHANGED");
            _seatingSupportsHostSequence = manifest["supportsHostSequence"]?.GetValue<bool>() == true;
            if (manifest["asset"] is not JsonObject asset) { _seatingDownload = null; Apply(manifest, null); return; }
            string hash = asset.Text("hash"); int revision = manifest["revision"]!.GetValue<int>();
            if (study.HasSeating(request.Text("version"), hash, revision)) { _seatingDownload = null; Apply(manifest, null); return; }
            int count = asset["chunks"]!.GetValue<int>(), byteCount = asset["byteCount"]!.GetValue<int>();
            if (_seatingDownload is not { } existing || !existing.Matches(connectionRevision, context, manifest))
                _seatingDownload = new(connectionRevision, context, manifest, byteCount);
            var download = _seatingDownload;
            for (int index = download.Next; index < count; index++)
            {
                RequireOriginalConnection();
                var body = new JsonObject { ["requestId"] = NpepProtocol.Id(), ["context"] = context.Copy(), ["version"] = request["version"]!.DeepClone(),
                    ["hash"] = hash, ["revision"] = revision, ["index"] = index };
                JsonObject reply;
                try { reply = await _device.ReadSeatingChunkAsync(body, deadline.Token); }
                catch (NpepException e) when (e is { Status: 409, Code: "SEATING_CHANGED" })
                {
                    _seatingDownload = null;
                    throw;
                }
                await RequireStudyContextAsync(context, deadline.Token);
                RequireOriginalConnection();
                if (reply.Text("hash") != hash || reply.Number("revision") != revision || reply.Number("index") != index || reply.Number("count") != count)
                    throw new NpepException("INVALID_RESPONSE");
                var bytes = Convert.FromBase64String(reply.Text("data"));
                if (bytes.Length != Math.Min(16384, byteCount - index * 16384)) throw new NpepException("INVALID_RESPONSE");
                bytes.CopyTo(download.Bytes, index * 16384);
                download.Next = index + 1;
            }
            if (Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(download.Bytes)) != hash)
            { _seatingDownload = null; throw new NpepException("INVALID_RESPONSE"); }
            Apply(manifest, download.Bytes);
            _seatingDownload = null;
        }
        try { await SyncOnceAsync(); }
        catch (NpepException e) when (e is { Status: 409, Code: "SEATING_CHANGED" })
        {
            // The server confirmed that this image is no longer current. Re-sample the manifest once under the same deadline.
            await SyncOnceAsync();
        }
    }
}

using System.Text.Json.Nodes;
using NPEduTools.Contracts;
using NPEduTools.Integrations.Npep;
namespace NPEduTools.Host;
public sealed partial class StudySettingsService
{
    private byte[]? _seatingBytes;
    private string? _seatingClassId, _seatingRevisionVersion;
    private int _highestSeatingRevision;
    private StudySeatingState? _seating;
    private TimeSpan? _seatingAt;
    private sealed record SeatingObservation(StudySeatingPulse Pulse, TimeSpan At);
    private string _seatingError = "WAITING";
    private readonly Dictionary<Guid, SeatingObservation> _seatingPresentations = [];
    private void ClearSeating() { _seatingBytes = null; _seating = null; _seatingAt = null; _seatingPresentations.Clear(); }
    private StudySeatingState SeatingState()
    {
        bool eligible = _state is { PolicyState: "APPLIED", ClockReady: true, Phase: "Preparing" or "Active" };
        if (!eligible || _seating?.Version != _version || _seatingAt is { } at && (Elapsed < at || Elapsed - at >= TimeSpan.FromSeconds(15))) ClearSeating();
        return _seating ?? new(!eligible ? "WAITING" : _seatingError, _version, 0, null, 0, 0, 0, 0);
    }
    public JsonObject? StudySeatingRequest() { var s = Snapshot(); return s is { PolicyState: "APPLIED", ClockReady: true, Phase: "Preparing" or "Active", Version: { } v } ? new() { ["version"] = v } : null; }
    public bool HasSeating(string version, string hash, int revision) { lock (_sync) { var s = SeatingState(); return s.Version == version && s.Hash == hash && s.Revision == revision && _seatingBytes is not null; } }
    public void ApplySeating(JsonObject manifest, byte[]? bytes)
    {
        NpepSeatingProtocol.Validate("manifest", manifest);
        lock (_sync) {
            Tick();
            if (StudySeatingRequest() is null || _version != manifest.Text("version") || _seatingClassId != manifest.Text("classId")) throw new NpepException("POLICY_CHANGED");
            int revision = manifest["revision"]!.GetValue<int>();
            if (_seatingRevisionVersion == _version && revision < _highestSeatingRevision) throw new NpepException("SEATING_CHANGED");
            if (manifest["asset"] is not JsonObject a) { _seatingRevisionVersion = _version; _highestSeatingRevision = revision; ClearSeating(); _seatingError = "NOT_PUBLISHED"; return; }
            string hash = a.Text("hash");
            if (bytes is null && !HasSeating(_version!, hash, revision)) throw new NpepException("SEATING_CHANGED");
            if (bytes is not null && (bytes.Length != a["byteCount"]!.GetValue<int>() || bytes.Length > 2097152 || Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes)) != hash)) throw new NpepException("INVALID_RESPONSE");
            if (bytes is not null) _seatingBytes = bytes.ToArray();
            _seating = new("READY", _version, revision, hash, a["chunks"]!.GetValue<int>(), a["byteCount"]!.GetValue<int>(), a["width"]!.GetValue<int>(), a["height"]!.GetValue<int>());
            _seatingRevisionVersion = _version; _highestSeatingRevision = revision;
            _seatingAt = Elapsed; _seatingError = "WAITING";
        }
    }
    public void SeatingFailed(string code) { lock (_sync) { _seatingError = code == "STUDY_SEATING_UNSUPPORTED" ? "UNSUPPORTED" : "UNAVAILABLE"; } }
    public HostResponse ReadSeating(HostRequest request)
    {
        lock (_sync) {
            Snapshot();
            if (request.StudySeating is not { } read || _seating is not { Hash: { } hash } s || _seatingBytes is null || read.Version != s.Version || read.Revision != s.Revision || read.Hash != hash || read.Index * 16384 >= _seatingBytes.Length)
                return new(1, request.RequestId, "Rejected", "SEATING_CHANGED", "座位表已更新或当前不可用。");
            int offset = read.Index * 16384;
            return new(1, request.RequestId, "Succeeded", null, "座位表片段。", SeatingChunk: new(s.Revision, hash, read.Index, s.Chunks, Convert.ToBase64String(_seatingBytes, offset, Math.Min(16384, _seatingBytes.Length - offset))));
        }
    }
    public HostResponse ReceiveSeating(HostRequest request, StudyHostState display)
    {
        lock (_sync) {
            var s = SeatingState(); var p = request.SeatingPresentation!;
            if (s.State != "READY" || display.Display?.InstanceId != p.InstanceId || display.Version != p.Version ||
                _seatingPresentations.TryGetValue(p.AppInstanceId, out var prior) && p.Sequence <= prior.Pulse.Sequence)
                return new(1, request.RequestId, "Rejected", "SEATING_CHANGED", "座位表观测已过期。");
            if (!_seatingPresentations.ContainsKey(p.AppInstanceId) && _seatingPresentations.Count >= 16) return new(1, request.RequestId, "Rejected", "TOO_MANY_APPS", "展示端实例过多。");
            _seatingPresentations[p.AppInstanceId] = new(p, Elapsed);
            return new(1, request.RequestId, "Succeeded", null, "已记录展示端座位表观测。");
        }
    }
    public JsonObject? ObserveSeatingDownload() { lock (_sync) { var s = SeatingState(); return s.State == "READY" ? new() { ["revision"] = s.Revision, ["hash"] = s.Hash } : null; } }
    public JsonObject? ObserveSeating()
    {
        lock (_sync) {
            SeatingState();
            var now = Elapsed;
            int? RenderAge(SeatingObservation seen) => seen.Pulse.RenderAgeMs is { } a ? a + (int)(now - seen.At).TotalMilliseconds : null;
            // A hidden or stale App cannot overwrite another App's fresh visible observation.
            // Keep sequence tombstones after receipt expiry, bounded to the existing 16-client limit.
            var seen = _seatingPresentations.Values.Where(s => now >= s.At && now - s.At < TimeSpan.FromSeconds(1))
                .OrderByDescending(s => s.Pulse.Visible && RenderAge(s) is >= 0 and < 1000)
                .ThenByDescending(s => s.At).FirstOrDefault();
            if (seen is null) return null;
            var p = seen.Pulse; int? age = RenderAge(seen);
            return new() { ["appInstanceId"] = p.AppInstanceId.ToString(), ["sequence"] = p.Sequence, ["revision"] = p.Revision,
                ["appliedHash"] = p.AppliedHash, ["visible"] = p.Visible && age is >= 0 and < 1000, ["renderAgeMs"] = age is >= 0 and < 1000 ? age : null };
        }
    }
}

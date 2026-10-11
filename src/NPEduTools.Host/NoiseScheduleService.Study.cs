using System.Text.Json;
using System.Text.Json.Nodes;
using NPEduTools.Contracts;
using NPEduTools.Core;
using NPEduTools.Integrations.Npep;

namespace NPEduTools.Host;

// Both protocols feed this one scheduled owner. A lost study lease cannot revive a legacy lease.
public sealed partial class NoiseScheduleService
{
    private JsonObject? _studyResumeReceipt;
    public bool SupportsResume => true;
    public JsonObject? ObserveStudyResumeReceipt()
    { lock (_sync) return _studyResumeReceipt?.DeepClone().AsObject(); }
    public void ApplyStudyResume(JsonObject configuration, JsonObject command, TimeSpan roundTrip = default)
    {
        NpepStudyProtocol.Validate("resumeCommand", command);
        lock (_sync)
        {
            if (!_eligible || !_studySelected || _scope is null || _version != configuration.Text("version")
                || !JsonNode.DeepEquals(JsonNode.Parse(_scope), configuration["identity"])) throw new NpepException("SESSION_SUPERSEDED");
            Tick();
            // Existing persisted consumption is returned without clearing a later manual stop.
            bool permitted = LeaseRemaining(Elapsed) > 0 && roundTrip >= TimeSpan.Zero && roundTrip < TimeSpan.FromSeconds(30)
                && _owner == "None" && _reason is "WINDOW_SKIPPED" or "WINDOW_FAILED" && !_exam();
            Resume(command, permitted, currentWindowOnly: true);
            _studyResumeReceipt = _receipt?.DeepClone().AsObject();
            Tick();
        }
    }
    private bool _studySelected, _studyProbeRequired, _studyProbeComplete;
    public void BindStudyCapture(string? scope)
    {
        lock (_sync)
        {
            _studyProbeRequired = true;
            if (scope is null) { Bind(_scope, false); InvalidateStudy(); }
            else Bind(scope, true);
        }
    }
    public void StudyUnavailable(string code)
    {
        lock (_sync)
        {
            InvalidateStudy();
            // Only an explicit unsupported reply allows an unconfigured old server through the startup fence.
            if (code == "STUDY_UNSUPPORTED" && !_studySelected) _studyProbeComplete = true;
        }
    }
    private StudySettings? _studySettings;
    private double _studyConfirmedAt = -1;
    private const double StudyLeaseMs = NoiseScheduleLease.MaxLifetimeMs;
    private double _studyLeaseMs = StudyLeaseMs;
    private double LeaseRemaining(double elapsed)
    {
        double at = _studySelected ? _studyConfirmedAt : _confirmedAt;
        double duration = _studySelected ? _studyLeaseMs : _leaseMs;
        return at >= 0 && elapsed >= at ? Math.Max(0, duration - (elapsed - at)) : 0;
    }
    private SchoolNoiseWindows ScheduleWindows(DateTimeOffset now)
    {
        try { return _studySelected && _studySettings is not null ? SchoolStudySchedule.Windows(_studySettings, now) : SchoolNoiseSchedule.Windows(_policy, now); }
        catch (ArgumentException) { return new(null, null); }
    }
    public void SelectStudy(JsonObject configuration, string scope)
    {
        NpepStudyProtocol.Validate("configuration", configuration);
        lock (_sync)
        {
            if (!JsonNode.DeepEquals(JsonNode.Parse(scope), configuration["identity"])) throw new NpepException("SESSION_SUPERSEDED");
            if (_scope is not null && !JsonNode.DeepEquals(JsonNode.Parse(_scope), configuration["identity"]))
                throw new NpepException("SESSION_SUPERSEDED");
            Bind(scope, true);
            _studyProbeComplete = true;
            string source = configuration["effective"]!.AsObject().Text("source");
            string version = configuration.Text("version");
            if (source == "None")
            {
                if (_studySelected)
                {
                    _studySelected = false; _studySettings = null; _studyConfirmedAt = -1;
                    _confirmedAt = -1; _policy = new("None", []); _version = null;
                    if (_owned is { } id) _noise.StopScheduled(id);
                }
                return; // Unconfigured schools retain the established legacy protocol, with a fresh confirmation.
            }
            if (!_studySelected || _version != version) _studyConfirmedAt = -1;
            _confirmedAt = -1; _studySelected = true; _version = version;
            _studySettings = configuration["effective"]!["settings"]?.Deserialize<StudySettings>(Protocol.Json);
            _policy = new(_studySettings?.Noise.Enabled == true ? source : "Disabled", _studySettings?.Schedule.Rules ?? []);
            Tick();
        }
    }
    public void InvalidateStudy()
    {
        lock (_sync)
        {
            _studyConfirmedAt = -1;
            if (_studySelected && _owned is { } id) _noise.StopScheduled(id);
            // Retain the selected source until explicit, authenticated None or a new binding.
        }
    }
    public void ConfirmStudyExecution(JsonObject configuration, JsonObject response, TimeSpan roundTrip = default)
    {
        NpepStudyProtocol.Validate("executionResponse", response);
        lock (_sync)
        {
            if (!_eligible || !_studySelected || _version != configuration.Text("version") || response.Text("version") != _version ||
                _scope is null || !JsonNode.DeepEquals(JsonNode.Parse(_scope), configuration["identity"])) throw new NpepException("SESSION_SUPERSEDED");
            if (!response["confirmed"]!.GetValue<bool>()) throw new NpepException("SEQUENCE_CONFLICT");
            if (roundTrip < TimeSpan.Zero || roundTrip.TotalMilliseconds >= StudyLeaseMs) throw new NpepException("POLICY_EXPIRED");
            _studyConfirmedAt = Elapsed;
            _studyLeaseMs = StudyLeaseMs - roundTrip.TotalMilliseconds;
            Tick();
        }
    }
    public JsonObject? ObserveStudyExecution()
    {
        lock (_sync)
        {
            if (!_studySelected) return null;
            Tick(); var schedule = Observe(); var state = _noise.Snapshot();
            bool fresh = _owner == "Schedule" && state.State == "Active" && _reason == "WINDOW_ACTIVE" && LeaseRemaining(Elapsed) > 0
                && (state.Quality == "DigitalSilence" || state.CurrentDbfs is not null && state.Quality is "Good" or "Clipping");
            return new() { ["owner"] = _owner, ["state"] = state.State, ["sessionId"] = _owner == "Schedule" ? state.SessionId?.ToString("D") : null,
                ["reason"] = _reason, ["clockReady"] = schedule["clockReady"]!.DeepClone(), ["schoolNow"] = schedule["schoolNow"]?.DeepClone(),
                ["window"] = schedule["window"]?.DeepClone(), ["signalFresh"] = fresh };
        }
    }
}

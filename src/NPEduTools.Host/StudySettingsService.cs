using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using NPEduTools.Contracts;
using NPEduTools.Core;
using NPEduTools.Integrations.Npep;

namespace NPEduTools.Host;

/// <summary>Computes phases and hands confirmed execution to the sole scheduled owner. Owns no microphone or display.</summary>
public sealed partial class StudySettingsService : INpepStudySettings, INpepStudyPhotos, INpepStudyExitEvents, IAsyncDisposable
{
    public static readonly TimeSpan MaxConfigurationAge = TimeSpan.FromSeconds(15);
    private readonly object _sync = new();
    private readonly Func<SchoolClockFrame> _clock;
    private readonly Func<bool> _exam;
    private readonly INpepStudyCapture? _capture;
    private readonly TimeProvider _time;
    private readonly long _origin;
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _loop;
    private SchoolClockTracker _tracker = new();
    private string? _scope, _version;
    private string _source = "None", _error = "WAITING_CONFIGURATION";
    private StudySettings? _settings;
    private TimeSpan? _confirmedAt;
    private StudyHostState _state = Empty("WAITING_CONFIGURATION");
    public INpepStudyPhotos? Photos { get; set; }
    public bool SupportsPhotos => Photos is not null;
    public void BindPhotos(string? scope) => Photos?.BindPhotos(scope);
    public JsonObject? PhotoLeaseRequest() => Photos?.PhotoLeaseRequest();
    public void ConfirmPhotoLease(JsonObject response, TimeSpan roundTrip) => Photos?.ConfirmPhotoLease(response, roundTrip);
    public StudyPhotoPacket? PendingPhoto() => Photos?.PendingPhoto();
    public void PhotoStored(Guid eventId) => Photos?.PhotoStored(eventId);
    public void PhotoFailed(string code, Guid? eventId = null) => Photos?.PhotoFailed(code, eventId);
    public INpepStudyExitEvents? ExitEvents {get;set;}
    public bool SupportsExitEvents=>ExitEvents is not null;
    public void BindExit(string? scope)=>ExitEvents?.BindExit(scope);
    public StudyExitPacket? PendingExit()=>ExitEvents?.PendingExit();
    public void ExitStored(Guid id)=>ExitEvents?.ExitStored(id);
    public void ExitFailed(string code,Guid? id=null)=>ExitEvents?.ExitFailed(code,id);
    private static StudyHostState Empty(string reason) => new(null, "UNAVAILABLE", "NOT_INTEGRATED", "NOT_INTEGRATED",
        "Blocked", reason, false, null, null, null, null);

    public StudySettingsService(Func<SchoolClockFrame> clock, Func<bool> exam, TimeProvider? time = null, bool background = true, INpepStudyCapture? capture = null)
    {
        _clock = clock; _exam = exam; _capture = capture; _time = time ?? TimeProvider.System; _origin = _time.GetTimestamp();
        _capture?.BindStudyCapture(null);
        _loop = background ? Task.Run(LoopAsync) : Task.CompletedTask;
    }
    private TimeSpan Elapsed => _time.GetElapsedTime(_origin);
    // A transport outage must not revive the old webpage fallback while a study lease survives.
    public (string? Scope, bool Selected) DisplaySelection
    { get { lock (_sync) return (_scope, _scope is not null && _source is "Grade" or "Class" or "Disabled"); } }
    public (string? Scope, bool Selected, StudyHostState State) DisplaySnapshot()
    { lock (_sync) { var state = Snapshot(); var selection = DisplaySelection; return (selection.Scope, selection.Selected, state); } }
    public void BindStudy(string? scope)
    {
        lock (_sync)
        {
            if (_scope == scope) return;
            _capture?.BindStudyCapture(scope);
            _scope = scope; _settings = null; _version = null; _confirmedAt = null; _source = "None";
            ClearHomework(); ClearSeating(); _seatingRevisionVersion = null; _highestSeatingRevision = 0;
            _tracker = new(); _error = scope is null ? "NOT_ELIGIBLE" : "WAITING_CONFIGURATION"; _state = Empty(_error);
        }
        // Photo callbacks must stay outside _sync: capture samples this service under its own lock.
        Photos?.BindPhotos(scope);
        ExitEvents?.BindExit(scope);
    }
    public void ApplyStudy(JsonObject configuration)
    {
        NpepStudyProtocol.Validate("configuration", configuration);
        lock (_sync)
        {
            if (_scope is null || !JsonNode.DeepEquals(JsonNode.Parse(_scope), configuration["identity"]))
                throw new NpepException("SESSION_SUPERSEDED");
            if (_version != configuration.Text("version")) ClearHomework();
            _seatingClassId = configuration["scope"]!.AsObject().Text("classId");
            _source = configuration["effective"]!.AsObject().Text("source");
            _settings = configuration["effective"]!["settings"]?.Deserialize<StudySettings>(Protocol.Json);
            _version = configuration.Text("version"); _confirmedAt = Elapsed; _error = "";
            _capture?.SelectStudy(configuration, _scope);
        }
        Tick();
        if (configuration["effective"]?["settings"]?["photos"]?["enabled"]?.GetValue<bool>() != true) Photos?.PhotoFailed("PHOTOS_DISABLED");
    }
    public void StudyFailed(string code)
    {
        lock (_sync)
        {
            // Transport loss withdraws the applied display claim, not an already granted offline capture lease.
            // Explicit policy/auth/protocol rejection must still fence execution immediately.
            if (code is not ("STUDY_OFFLINE" or "TEMPORARILY_UNAVAILABLE" or "RATE_LIMITED")) _capture?.StudyUnavailable(code);
            _confirmedAt = null; _error = code; _state = Empty(code);
        }
    }
    public void Tick()
    {
        lock (_sync)
        {
            var elapsed = Elapsed;
            if (_scope is null || _confirmedAt is not { } received || elapsed < received || elapsed - received >= MaxConfigurationAge)
            { _state = Empty(_error.Length > 0 ? _error : "CONFIGURATION_STALE"); return; }
            _tracker.Accept(_clock(), elapsed, 0);
            var reading = _tracker.Read(elapsed);
            _state = new(_version, "APPLIED", "NOT_INTEGRATED", "NOT_INTEGRATED", "Blocked",
                "SCHOOL_CLOCK_UNAVAILABLE", reading.CanStart, reading.Now, null, null, _settings);
            if (_source is "None" or "Disabled")
            { _state = _state with { Phase = "Disabled", Reason = "DISABLED" }; return; }
            if (_exam()) { _state = _state with { Reason = "EXAM_PAUSED" }; return; }
            if (!reading.CanStart || reading.Now is not { Year: >= 2000 and <= 9998 } now) return;
            try
            {
                var windows = SchoolStudySchedule.Windows(_settings!, now);
                bool prepare = windows.Current is null && windows.Next is { } next && now >= next.Start.AddSeconds(-120);
                _state = _state with { Phase = windows.Current is not null ? "Active" : prepare ? "Preparing" : "Outside",
                    Reason = windows.Current is not null ? "WINDOW_ACTIVE" : prepare ? "PREPARING" : "OUTSIDE_WINDOW",
                    Window = windows.Current ?? (prepare ? windows.Next : null), Next = windows.Next };
            }
            catch (ArgumentException) { _state = _state with { Phase = "Blocked", Reason = "INVALID_STUDY_CALENDAR" }; }
        }
    }
    public bool SupportsExecution => _capture is not null;
    public Func<JsonObject?>? PresentationObservation { get; set; }
    public bool SupportsPresentation => PresentationObservation is not null;
    public JsonObject? ObserveStudyPresentation() => PresentationObservation?.Invoke();
    public bool SupportsResume => _capture?.SupportsResume == true;
    public JsonObject? ObserveStudyResumeReceipt() => _capture?.ObserveStudyResumeReceipt();
    public void ApplyStudyResume(JsonObject configuration, JsonObject command, TimeSpan roundTrip = default)
    {
        lock (_sync)
        {
            if (_scope is null || _version != configuration.Text("version") || _confirmedAt is not { } at || Elapsed - at >= MaxConfigurationAge)
                throw new NpepException("SESSION_SUPERSEDED");
            _capture?.ApplyStudyResume(configuration, command, roundTrip);
        }
    }
    public JsonObject? ObserveStudyExecution() => _capture?.ObserveStudyExecution();
    public void ConfirmStudyExecution(JsonObject configuration, JsonObject response, TimeSpan roundTrip = default)
    {
        lock (_sync)
        {
            if (_scope is null || _version != configuration.Text("version") || _confirmedAt is not { } at || Elapsed - at >= MaxConfigurationAge)
                throw new NpepException("SESSION_SUPERSEDED");
            _capture?.ConfirmStudyExecution(configuration, response, roundTrip);
        }
    }
    public StudyHostState Snapshot()
    {
        Tick();
        lock (_sync)
        {
            var execution = _capture?.ObserveStudyExecution()?.Deserialize<StudyCaptureState>(Protocol.Json);
            return _state with { Execution = execution, CaptureState = execution is null ? "NOT_INTEGRATED"
                : execution.Owner == "Manual" ? "MANUAL" : execution.SignalFresh ? "ACTIVE"
                : execution.State == "Active" ? "WAITING_FRESH_SIGNAL" : execution.State.ToUpperInvariant(), Homework = HomeworkState(), Seating = SeatingState() };
        }
    }
    private static string Stamp(DateTimeOffset value) => value.ToString("yyyy-MM-dd'T'HH:mm:ss.fff", CultureInfo.InvariantCulture);
    public JsonObject? ObserveStudy()
    {
        var state = Snapshot();
        if (state.PolicyState != "APPLIED" || state.Version is null) return null;
        return new() { ["version"] = state.Version, ["policyState"] = state.PolicyState,
            // The 0.10 configuration-only receipt keeps its reserved placeholder for old parsers.
            // Actual capture facts are sent through the independent execution exchange and local Execution field.
            ["presentationState"] = state.PresentationState, ["captureState"] = "NOT_INTEGRATED",
            ["phase"] = state.Phase, ["reason"] = state.Reason, ["clockReady"] = state.ClockReady,
            ["schoolNow"] = state.SchoolNow is { Year: >= 2000 and <= 9998 } now ? Stamp(now) : null,
            ["window"] = state.Window is { } window ? new JsonObject { ["start"] = Stamp(window.Start), ["end"] = Stamp(window.End) } : null };
    }
    private async Task LoopAsync()
    {
        try
        {
            while (!_stop.IsCancellationRequested)
            { Tick(); await Task.Delay(TimeSpan.FromMilliseconds(500), _time, _stop.Token); }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
    }
    public async ValueTask DisposeAsync() { _stop.Cancel(); await _loop; BindStudy(null); _stop.Dispose(); }
}

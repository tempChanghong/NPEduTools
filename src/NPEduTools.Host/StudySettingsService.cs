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
    private readonly Func<string> _modeSource;
    private string _mode = "Daily";
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
    private sealed record RunningStudy(SchoolNoiseWindow Window, TimeSpan EndAt, bool Completed = false);
    private RunningStudy? _running;
    private bool ConfigurationFresh => _confirmedAt is { } at && Elapsed >= at && Elapsed - at < MaxConfigurationAge;
    private bool ContinuingOffline => !ConfigurationFresh && _running is not null;
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

    public StudySettingsService(Func<SchoolClockFrame> clock, Func<bool> exam, TimeProvider? time = null, bool background = true, INpepStudyCapture? capture = null, Func<string>? mode = null)
    {
        _modeSource = mode ?? (() => "Daily");
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
    // Auxiliary noise metadata must not run scheduling, probe other software or stop an expired owner.
    internal (string? Scope, StudyHostState State) CachedCalibrationContext
    {
        get
        {
            lock (_sync) return (_scope, ConfigurationFresh || _running is not null ? _state :
                _state with { PolicyState = "UNAVAILABLE" });
        }
    }
    public void BindStudy(string? scope)
    {
        lock (_sync)
        {
            if (_scope == scope) return;
            _capture?.BindStudyCapture(scope);
            _scope = scope; _settings = null; _version = null; _confirmedAt = null; _source = "None"; _running = null;
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
            if (_version != configuration.Text("version")) { ClearHomework(); _running = null; }
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
            if (code is "STUDY_OFFLINE" or "TEMPORARILY_UNAVAILABLE" or "RATE_LIMITED")
            {
                // Remember only existing execution. A failure-time read must not
                // start a due window or preempt a manual owner through the scheduler.
                Tick(false);
            }
            else { _running = null; _capture?.StudyUnavailable(code); }
            _confirmedAt = null; _error = code; _state = Empty(code);
            Tick(false);
        }
    }
    public void Tick() => Tick(true);
    private void Tick(bool refreshCapture)
    {
        lock (_sync)
        {
            var mode = _modeSource() == "Exam" ? "Exam" : "Daily";
            if (_mode != mode) { _mode = mode; _tracker = new(); ClearHomework(); _running = null; }
            var elapsed = Elapsed;
            if (_scope is null || !ConfigurationFresh && _running is null)
            {
                if (_scope is not null && !ConfigurationFresh) _capture?.InvalidateStudy();
                _state = Empty(_error.Length > 0 ? _error : "CONFIGURATION_STALE"); return;
            }
            _tracker.Accept(_clock(), elapsed, 0);
            var reading = _tracker.Read(elapsed);
            _state = new(_version, "APPLIED", "NOT_INTEGRATED", "NOT_INTEGRATED", "Blocked",
                "SCHOOL_CLOCK_UNAVAILABLE", reading.CanStart, reading.Now, null, null, _settings);
            if (_source is "None" or "Disabled")
            { _running = null; _state = _state with { Phase = "Disabled", Reason = _source == "None" ? "NOT_CONFIGURED" : "DISABLED" }; return; }
            if (!reading.CanStart || reading.Now is not { Year: >= 2000 and <= 9998 } now) return;
            if (!ConfigurationFresh)
            {
                var execution = (refreshCapture ? _capture?.ObserveStudyExecution() : _capture?.PeekStudyExecution())?.Deserialize<StudyCaptureState>(Protocol.Json);
                if (_running is not { } held || elapsed > held.EndAt + TimeSpan.FromSeconds(305) ||
                    (!held.Completed && execution?.Reason is "WINDOW_SKIPPED" or "POLICY_EXPIRED" or "NOT_ELIGIBLE" or "EXAM_PAUSED") || _exam())
                { _running = null; _capture?.InvalidateStudy(); _state = Empty(_error.Length > 0 ? _error : "CONFIGURATION_STALE"); return; }
                // Only this already executing window survives transport loss. No cached next window is started or advertised.
                bool ended = now >= held.Window.End || elapsed >= held.EndAt;
                if (ended && !held.Completed)
                {
                    _running = held with { Completed = true };
                    _capture?.InvalidateStudy(); // The surviving grant must not silently start a later offline window.
                }
                _state = _state with { Phase = ended ? "Outside" : "Active", Reason = "OFFLINE_CONTINUING",
                    SchoolNow = ended && now < held.Window.End ? held.Window.End : now, Window = ended ? null : held.Window };
                return;
            }
            try
            {
                var windows = SchoolStudySchedule.Windows(_settings!.ForMode(_mode), now);
                if (_exam()) { _state = _state with { Reason = "EXAM_PAUSED", Next = windows.Next }; return; }
                bool prepare = windows.Current is null && windows.Next is { } next && now >= next.Start.AddSeconds(-StudyPresentationTiming.ScheduledDisplayLeadSeconds);
                _state = _state with { Phase = windows.Current is not null ? "Active" : prepare ? "Preparing" : "Outside",
                    Reason = windows.Current is not null ? "WINDOW_ACTIVE" : prepare ? "PREPARING" : "OUTSIDE_WINDOW",
                    Window = windows.Current ?? (prepare ? windows.Next : null), Next = windows.Next };
                if (_state is { Phase: "Active", Window: { } active } &&
                    (refreshCapture ? _capture?.ObserveStudyExecution() : _capture?.PeekStudyExecution())?.Deserialize<StudyCaptureState>(Protocol.Json) is
                        { Owner: "Schedule", State: "Starting" or "Active", SessionId: not null, Reason: "WINDOW_ACTIVE" or "CAPTURE_STARTING", ClockReady: true } started &&
                    started.Mode == _mode && started.Window == new StudyExecutionWindow(Stamp(active.Start), Stamp(active.End)))
                    _running = _running?.Window == active ? _running : new(active, elapsed + (active.End - now));
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
            Tick();
        }
    }
    public StudyHostState Snapshot()
    {
        Tick();
        lock (_sync)
        {
            var execution = _capture?.ObserveStudyExecution()?.Deserialize<StudyCaptureState>(Protocol.Json);
            return _state with { Mode = _mode, ClockSource = _mode == "Exam" ? "Windows" : "ClassIsland", Execution = execution, CaptureState = execution is null ? "NOT_INTEGRATED"
                : execution.Owner == "Manual" ? "MANUAL" : execution.SignalFresh ? "ACTIVE"
                : execution.State == "Active" ? "WAITING_FRESH_SIGNAL" : execution.State.ToUpperInvariant(), Homework = HomeworkState(), Seating = SeatingState() };
        }
    }
    private static string Stamp(DateTimeOffset value) => value.ToString("yyyy-MM-dd'T'HH:mm:ss.fff", CultureInfo.InvariantCulture);
    public JsonObject? ObserveStudy()
    {
        lock (_sync)
        {
            var state = Snapshot();
            if (!ConfigurationFresh) return null;
            if (state.PolicyState != "APPLIED" || state.Version is null) return null;
            var observation = new JsonObject { ["version"] = state.Version, ["policyState"] = state.PolicyState,
                // The 0.10 configuration-only receipt keeps its reserved placeholder for old parsers.
                // Actual capture facts are sent through the independent execution exchange and local Execution field.
                ["presentationState"] = state.PresentationState, ["captureState"] = "NOT_INTEGRATED",
                ["phase"] = state.Phase, ["reason"] = state.Reason, ["clockReady"] = state.ClockReady,
                ["schoolNow"] = state.SchoolNow is { Year: >= 2000 and <= 9998 } now ? Stamp(now) : null,
                ["window"] = state.Window is { } window ? new JsonObject { ["start"] = Stamp(window.Start), ["end"] = Stamp(window.End) } : null };
            if (_mode == "Exam") observation["mode"] = _mode;
            return observation;
        }
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

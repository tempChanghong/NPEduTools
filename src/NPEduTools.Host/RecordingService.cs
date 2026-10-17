using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using NPEduTools.Contracts;
using NPEduTools.Core;

namespace NPEduTools.Host;

/// <summary>One owner for manual/automatic sessions. All mutations and start intents are serialized.</summary>
public sealed class RecordingService : IAsyncDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Func<SchoolClockFrame> _snapshot;
    private readonly Func<bool> _modePaused;
    private readonly Func<bool> _remoteExamPaused;
    private readonly RuntimeOperationGate _runtimeGate;
    private int _runtimeReserved;
    private readonly SchoolClockTracker _clock = new();
    private readonly RecordingExecutionLedger _ledger;
    private readonly string _bookPath, _executable;
    private readonly string? _fixture;
    private readonly Task _loop;
    private RecorderProcess? _worker;
    private RecordingOptions? _options;
    private PlannedRecording? _activePlan;
    private Guid _activeProfile;
    private bool _enabled, _valid = true, _stopping;
    private Guid _client;
    private long _appLease;
    private long _enabledAt;
    private Guid _manualClient;
    private long _manualLease;
    private sealed record UpcomingSnapshot(PlannedRecording? Plan, Guid Profile, string State)
    { public PlannedRecording[] Plans { get; init; } = []; }
    private UpcomingSnapshot _upcoming = new(null, Guid.Empty, "Pending");
    private string _message = "自动录制未启用", _error = "";
    private RecordingState _last = new("Idle", "准备录制");
    private static TimeSpan Elapsed => Stopwatch.GetElapsedTime(0);
    public RecordingService(string pipe, string directory, Func<SchoolClockFrame> snapshot, Func<bool>? modePaused = null,
        Func<bool>? remoteExamPaused = null, RuntimeOperationGate? runtimeGate = null)
    {
        _snapshot = snapshot;
        _modePaused = modePaused ?? (() => false);
        _remoteExamPaused = remoteExamPaused ?? (() => false);
        _runtimeGate = runtimeGate ?? new();
        string hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(pipe)))[..24];
        _bookPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NPEduTools", "ui", hash + ".recording-plans.json");
        _executable = Path.Combine(AppContext.BaseDirectory, "Recorder", "NPEduTools.Recorder.exe");
        _fixture = pipe.StartsWith("NPEduTools.Test.", StringComparison.Ordinal) ? Environment.GetEnvironmentVariable("NPEEDUTOOLS_RECORDING_FIXTURE") : null;
        _ledger = new(Path.Combine(directory, "recording-executions.json"));
        try { _ledger.Load(); } catch (Exception error) when (error is not OutOfMemoryException) { _valid = false; _error = error.Message; }
        _loop = RunAsync();
    }
    public RecordingState State => _worker?.State ?? _last;
    public AutomaticRecordingState Automatic
    {
        get
        {
            bool localPause = _modePaused(), remotePause = _remoteExamPaused();
            var upcoming = ReadUpcoming(localPause || remotePause);
            var value = new AutomaticRecordingState(_enabled, _client,
                remotePause ? "远程考试状态已暂停自动录制；原计划保留。" : localPause ? "课堂模式已暂停自动录制；原计划保留。" : _message, _ledger.Document.SkipDate,
                _ledger.Document.Entries.TakeLast(20).Reverse().ToArray(), string.IsNullOrEmpty(_error) ? null : _error, localPause || remotePause, upcoming.Next, upcoming.State);
            // Long Unicode paths/reasons must not make the entire status endpoint exceed its frame limit.
            while (value.Recent.Length > 0 && JsonSerializer.SerializeToUtf8Bytes(value, Protocol.Json).Length > 32000)
                value = value with { Recent = value.Recent[..^1] };
            return value;
        }
    }
    private (UpcomingRecording? Next, string State) ReadUpcoming(bool suspended)
    {
        if (!_enabled) return (null, "Disabled");
        if (suspended) return (null, "Suspended");
        if (Volatile.Read(ref _runtimeReserved) != 0 || _runtimeGate.ExamRequested) return (null, "Pending");
        var clock = _clock.Read(Elapsed);
        if (!clock.CanStart || clock.Now is not { } now) return (null, "ClockPending");
        if (_ledger.Document.SkipDate == DateOnly.FromDateTime(now.Date)) return (null, "SkippedToday");
        var snapshot = _upcoming;
        if (snapshot.Plan is not { } next) return (null, snapshot.State);
        if (next.Start <= now || _ledger.Contains(snapshot.Profile, next, snapshot.Plans)) return (null, "Pending");
        return (new(next.Lesson.Subject, next.Start, next.End), "Ready");
    }
    private RecordingPlanBook ReadBook()
    {
        if (!File.Exists(_bookPath)) throw new InvalidDataException("请先在计划页保存录课规则。");
        return new RecordingPlanBookStore(_bookPath).Read();
    }
    public async Task<HostResponse> HandleAsync(HostRequest request)
    {
        if (request.Capability == "recording.status" || request.Capability == "recording.automatic" && request.Automatic?.Action == "lease")
            return await HandleCoreAsync(request);
        using var reservation = _runtimeGate.TryEnterMutation();
        if (reservation is null) return new(Protocol.Version, request.RequestId, "Rejected", "RuntimeOperationBusy",
            "正在切换运行环境，请等待操作结束。", Recording: State, Automatic: Automatic);
        return await HandleCoreAsync(request);
    }

    private async Task<HostResponse> HandleCoreAsync(HostRequest request)
    {
        if (request.Capability == "recording.status") return Reply(request);
        await _gate.WaitAsync();
        try
        {
            if (_stopping) throw new InvalidOperationException("后台正在停止。");
            if (request.Capability == "recording.command")
            {
                var command = request.Recording!;
                if (command.Action == "start")
                {
                    if (State.Control?.SessionId == request.RequestId) return Reply(request);
                    if (Volatile.Read(ref _runtimeReserved) != 0)
                        return new(Protocol.Version, request.RequestId, "Rejected", "RuntimeOperationBusy",
                            "正在切换运行环境，请等待操作结束后再开始录制。", Recording: State, Automatic: Automatic);
                    if (State.Active) throw new InvalidOperationException("已有录制会话，请先结束当前会话。");
                    await RetireAsync();
                    var control = new RecorderControl("Manual", request.RequestId, "", 0, RecorderDeadline.After(TimeSpan.FromSeconds(8)));
                    _manualClient = command.ClientId!.Value; _manualLease = RecorderDeadline.After(TimeSpan.FromSeconds(12));
                    await StartAsync(command.Options!, control);
                }
                else
                {
                    var expected = command.Control!;
                    if (_worker is null || !State.Active || State.Control?.Matches(expected) != true) throw new InvalidOperationException("会话已变化，旧操作不会影响新的录制。");
                    if (command.Action == "stop" && expected.Owner == "Automatic")
                        MarkAutomaticFinalizing(expected, "用户提前结束本节；不再重复开始。");
                    await _worker.CommandAsync(command.Action, expected);
                }
            }
            else
            {
                var command = request.Automatic!;
                if (_enabled && command.Action is not ("enable" or "lease") && command.ClientId != _client)
                    throw new InvalidOperationException("自动录制的管理会话已变化，请重新连接。");
                switch (command.Action)
                {
                    case "enable":
                        if (!_valid) throw new InvalidDataException(_error);
                        if (_enabled && _client != command.ClientId) throw new InvalidOperationException("自动录制已由另一窗口管理。");
                        _ = ReadBook();
                        _clock.ConfirmDate();
                        if (!_clock.Read(Elapsed).CanStart) throw new InvalidOperationException("请等待学校时间校验完成，核对日期后再启用。");
                        await PreflightAsync(command.Options!);
                        _options = command.Options; _client = command.ClientId; _enabled = true; _error = "";
                        _enabledAt = Stopwatch.GetTimestamp();
                        _appLease = RecorderDeadline.After(TimeSpan.FromSeconds(12)); _message = "自动录制已启用，按学校时间等待计划";
                        break;
                    case "lease":
                        if (_enabled && _client == command.ClientId) _appLease = RecorderDeadline.After(TimeSpan.FromSeconds(12));
                        if (_manualClient == command.ClientId) _manualLease = RecorderDeadline.After(TimeSpan.FromSeconds(12));
                        break;
                    case "disable":
                        _enabled = false; _message = "自动录制已停用"; await StopAutomaticAsync("用户关闭自动录制"); break;
                    case "skip-day":
                    case "resume-day":
                        var reading = _clock.Read(Elapsed);
                        if (!reading.CanStart || reading.Now is not { } now) throw new InvalidOperationException("学校时间不可用，不能确定今天。");
                        _ledger.SkipDate(command.Action == "skip-day" ? DateOnly.FromDateTime(now.Date) : null);
                        if (command.Action == "skip-day") await StopAutomaticAsync("今天不再录制");
                        break;
                    case "skip-upcoming":
                    case "skip-next":
                        if (command.Action == "skip-next" && State is { Active: true, Control.Owner: "Automatic" }) await StopAutomaticAsync("用户结束本节");
                        else
                        {
                            var clock = _clock.Read(Elapsed);
                            if (!clock.CanStart || clock.Now is not { } current) throw new InvalidOperationException("等待学校时间。");
                            var day = DateOnly.FromDateTime(current.Date); var source = _clock.Schedule;
                            var candidates = CalendarRecordingPlanner.Build(ReadBook(), day, source?.Date == day ? source : null);
                            var next = candidates.FirstOrDefault(p => p.Selected && !p.Conflict && p.End > current && !_ledger.Contains(source?.ProfileId ?? Guid.Empty, p, candidates));
                            if (next is not null) _ledger.Add(source?.ProfileId ?? Guid.Empty, day, next, "Skipped", "用户跳过本日下一项任务", candidates);
                        }
                        break;
                }
            }
            return Reply(request);
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        { return new(Protocol.Version, request.RequestId, "Rejected", "RecordingOperationFailed", error.Message, Recording: State, Automatic: Automatic); }
        finally { _gate.Release(); }
    }
    private HostResponse Reply(HostRequest request) => new(Protocol.Version, request.RequestId, "Succeeded", null, "录制状态", Recording: State, Automatic: Automatic);
    private async Task PreflightAsync(RecordingOptions options)
    {
        if (RecordingContract.Validate(options) is { } error) throw new InvalidDataException(error);
        await using var probe = new RecorderProcess(_executable);
        var environment = await probe.ProbeAsync();
        if (!environment.Ready || !environment.Displays.Any(d => d.Id == options.Display)) throw new InvalidOperationException(environment.Error ?? "录制屏幕已断开。");
        if (options.Microphone && !environment.Microphones.Any(d => options.MicrophoneId == "default" || d.Id == options.MicrophoneId) ||
            options.SystemAudio && !environment.Speakers.Any(d => options.SpeakerId == "default" || d.Id == options.SpeakerId)) throw new InvalidOperationException("所选音源不可用，请重新配置录制设置。");
        Directory.CreateDirectory(options.OutputDirectory);
        string test = Path.Combine(options.OutputDirectory, ".npedutools-write-test-" + Guid.NewGuid().ToString("N"));
        using (var file = new FileStream(test, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.DeleteOnClose)) { file.WriteByte(0); file.Flush(true); }
        if (new DriveInfo(Path.GetPathRoot(Path.GetFullPath(options.OutputDirectory))!).AvailableFreeSpace < 512L * 1024 * 1024)
            throw new IOException("保存位置可用空间不足 512 MB。");
    }
    private async Task StartAsync(RecordingOptions options, RecorderControl control)
    {
        _worker = new(_executable, _fixture);
        try { await _worker.StartAsync(options, control); }
        catch (Exception error) when (error is not OutOfMemoryException) { _worker.Fail(error.Message); throw; }
    }
    private async Task RetireAsync()
    {
        if (_worker is null) return;
        await _worker.DisposeAsync();
        if (_worker.Alive) throw new InvalidOperationException("上一录制仍在收尾，请稍后重试。");
        _last = _worker.State;
        // A manual start may retire a just-finished automatic worker before
        // the next scheduler tick. Commit its final receipt before replacing
        // the worker, otherwise the execution stays Recording until restart.
        ObserveAutomaticState(_last);
        _worker = null;
    }
    private void ObserveAutomaticState(RecordingState state)
    {
        if (_activePlan is not { } active || state.Control is not { Owner: "Automatic" } control || control.OccurrenceId != active.Key) return;
        string phase = state.Phase switch { "Starting" => "Starting", "Recording" => "Recording", "Paused" or "Pausing" => "Paused",
            "Saving" => "Finalizing", "Saved" => "Recorded", _ => "Failed" };
        var prior = _ledger.Document.Entries.Single(e => e.SessionId == control.SessionId);
        string? reason = phase == "Recorded" && prior.Phase != "Recorded" ? prior.Reason + "；会话已结束，MP4 已保存并校验" : state.Phase == "Failed" ? state.Error :
            prior.Phase == "Starting" && phase == "Recording" ? prior.Reason + "；录制器已确认开始" : null;
        _ledger.Update(control.SessionId, phase, reason, state);
        if (!state.Active) _activePlan = null;
    }
    private async Task RunAsync()
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(250));
        long previous = Stopwatch.GetTimestamp();
        try
        {
            while (await timer.WaitForNextTickAsync(_lifetime.Token))
            {
                // Command handlers legitimately own this gate while preparing or
                // finalizing a recorder. Their wait is not a pause of this loop.
                long observedTick = Stopwatch.GetTimestamp();
                await _gate.WaitAsync(_lifetime.Token);
                try
                {
                    if (_enabled && _enabledAt < previous && Stopwatch.GetElapsedTime(previous, observedTick) > TimeSpan.FromSeconds(5))
                    {
                        _enabled = false; _message = "后台经历暂停或长时间停顿，请核对学校时间后重新启用";
                        await StopAutomaticAsync(_message);
                    }
                    await TickAsync();
                }
                catch (Exception error) when (error is not OutOfMemoryException)
                {
                    _enabled = false; _error = error.Message; _message = "自动录制已停止，请核对记录后重新启用";
                    try { await StopAutomaticAsync("调度或持久化失败"); } catch (Exception e) when (e is not OutOfMemoryException) { }
                }
                finally { previous = Stopwatch.GetTimestamp(); _gate.Release(); }
            }
        }
        catch (OperationCanceledException) { }
    }
    private async Task TickAsync()
    {
        _upcoming = new(null, Guid.Empty, "Pending");
        _clock.Accept(_snapshot(), Elapsed, 0);
        var reading = _clock.Read(Elapsed); var now = reading.Now;
        var state = State;
        ObserveAutomaticState(state);
        if (_worker is not null && !State.Active) await RetireAsync();
        if (_worker is not null && State is { Active: true, Control: { Owner: "Manual" } manual } && Stopwatch.GetTimestamp() >= _manualLease)
            await _worker.CommandAsync("stop", manual);
        if (_enabled && (Stopwatch.GetTimestamp() >= _appLease || reading.DateNeedsReview))
        { _enabled = false; _message = reading.DateNeedsReview ? reading.Message : "管理窗口失联，自动录制已停用"; await StopAutomaticAsync(_message); }
        // Tighten an active deadline even while school clock continuity is being revalidated.
        if (_activePlan is { } active && _worker is not null && State.Control is { } currentControl && reading.Fresh && now is { } schoolNow)
        {
            var deadline = RecorderDeadline.After(TimeSpan.FromMilliseconds(Math.Max(0, (active.End - schoolNow).TotalMilliseconds - reading.AgeMs)));
            if (deadline < currentControl.HardDeadline)
            {
                if (deadline <= Stopwatch.GetTimestamp()) await StopAutomaticAsync("到达计划截止时间");
                else await _worker.CommandAsync("shorten", currentControl with { HardDeadline = deadline });
            }
        }
        if (_modePaused()) { await StopAutomaticAsync("课堂模式暂停自动录制"); return; }
        // N3 reserves an idle recorder before setting this pause. It only blocks new starts;
        // it never sends a stop command and never clears the user's enabled/plan configuration.
        if (_remoteExamPaused() || _runtimeGate.ExamRequested || Volatile.Read(ref _runtimeReserved) != 0) return;
        if (!_enabled || !reading.CanStart || now is null) { if (_enabled) _message = reading.Message; return; }
        DateOnly date = DateOnly.FromDateTime(now.Value.Date);
        var source = _clock.Schedule; if (source?.Date != date) source = null;
        var book = ReadBook();
        var plans = CalendarRecordingPlanner.Build(book, date, source);
        if (_ledger.Document.SkipDate == date) { _message = "学校今天不再自动录制"; await StopAutomaticAsync(_message); return; }
        if (_activePlan is { } ongoing)
        {
            var replacement = RecordingPlanner.FindReplacement(plans, ongoing, source?.ProfileId == _activeProfile);
            if (replacement is null)
            {
                // The old task is no longer admitted. Retain its identity while
                // it finalizes, but do not let its former future end cause a
                // due replacement to be permanently marked as occupied/skipped.
                _activePlan = ongoing with { Selected = false, End = now.Value < ongoing.End ? now.Value : ongoing.End };
                await StopAutomaticAsync("课表、规则、暂停或档案发生变化，本节不再执行");
            }
            else if (replacement.End < ongoing.End) _activePlan = ongoing with { End = replacement.End };
        }
        foreach (var plan in plans.Where(p => p.Selected && now >= p.Start && !_ledger.Contains(source?.ProfileId ?? Guid.Empty, p, plans)))
        {
            if (!plan.Fixed && (source is null || !source.Enabled || !source.ClockVerified)) continue;
            if (now.Value.AddMilliseconds(reading.AgeMs) >= plan.Lesson.End || now.Value.AddMilliseconds(reading.AgeMs) >= plan.End)
            { _ledger.Add(source?.ProfileId ?? Guid.Empty, date, plan, plan.Conflict ? "Conflict" : "Missed", plan.Conflict ? plan.Reason : "有效录制时段已结束，不补录", plans); continue; }
            if (plan.Conflict) continue;
            if (State.Active || _worker is { Alive: true })
            {
                // A just-ended automatic lesson may still be saving its file.
                // Do not permanently skip the adjoining lesson during this handoff.
                if (CanWaitForAutomaticHandoff(State, _activePlan, plan, now.Value,
                    _activeProfile, source?.ProfileId ?? Guid.Empty)) continue;
                _ledger.Add(source?.ProfileId ?? Guid.Empty, date, plan, "Skipped", "录制器被手动或上一会话占用，本次不再补开", plans); continue;
            }
            await RetireAsync();
            var entry = _ledger.Add(source?.ProfileId ?? Guid.Empty, date, plan, "Starting", string.IsNullOrEmpty(plan.OverlapNotice)
                ? "启动意图已保存，等待录制器确认" : "启动意图已保存，等待录制器确认；" + plan.OverlapNotice, plans);
            _activePlan = plan; _activeProfile = source?.ProfileId ?? Guid.Empty;
            var limit = TimeSpan.FromMilliseconds(Math.Max(0, (plan.End - now.Value).TotalMilliseconds - reading.AgeMs));
            var owner = new RecorderControl("Automatic", entry.SessionId, entry.Key, RecorderDeadline.After(limit), RecorderDeadline.After(TimeSpan.FromSeconds(8)));
            try { await StartAsync(_options!, owner); }
            catch (Exception error) when (error is not OutOfMemoryException)
            { _ledger.Update(entry.SessionId, "Failed", error.Message, State); _activePlan = null; }
            break;
        }
        var next = plans.FirstOrDefault(p => p.Selected && !p.Conflict && p.Start > now && !_ledger.Contains(source?.ProfileId ?? Guid.Empty, p, plans));
        _upcoming = new(next, source?.ProfileId ?? Guid.Empty, next is null ? "None" : "Ready") { Plans = plans };
        _message = State is { Active: true, Control.Owner: "Automatic" } ? AutomaticRecordingMessage(State) :
            next is null ? "自动录制已启用，本日暂无待开始任务" : $"下次自动录制：{next.Lesson.Subject} · 学校时间 {next.Start:HH:mm:ss}，可跳过下一项";
    }
    internal static string AutomaticRecordingMessage(RecordingState state)
    {
        const string prefix = "正在录制；";
        return state is { Phase: "Recording", Control.Owner: "Automatic" } &&
            state.Message is { } message && message.StartsWith(prefix, StringComparison.Ordinal)
            ? "正在自动录制；" + message[prefix.Length..]
            : "正在自动录制；暂停不会延长截止";
    }
    private async Task StopAutomaticAsync(string reason)
    {
        if (_worker is not null && State is { Active: true, Control: { Owner: "Automatic" } c })
        {
            // Persist the reason first. Even if this fails, always attempt to stop this exact session.
            try { MarkAutomaticFinalizing(c, reason); }
            finally { await _worker.CommandAsync("stop", c); }
        }
    }
    private void MarkAutomaticFinalizing(RecorderControl control, string reason)
    {
        var prior = _ledger.Document.Entries.Single(e => e.SessionId == control.SessionId);
        string combined = prior.Reason.Contains(reason, StringComparison.Ordinal) ? prior.Reason :
            string.IsNullOrEmpty(prior.Reason) ? reason : prior.Reason + "；" + reason;
        _ledger.Update(control.SessionId, "Finalizing", combined, State);
    }
    // A withdrawn task is already stopping: its old ordering/profile no longer
    // defines which new plan may wait. Continuing tasks retain those guards.
    internal static bool CanWaitForAutomaticHandoff(RecordingState state, PlannedRecording? previous,
        PlannedRecording next, DateTimeOffset now, Guid previousProfile, Guid nextProfile) =>
        state.Control is { Owner: "Automatic" } && previous is not null &&
        state.Control.OccurrenceId == previous.Key && previous.Key != next.Key && previous.End <= now && now < next.Lesson.End &&
        (!previous.Selected || (previous.Fixed || next.Fixed || previousProfile == nextProfile) && previous.Lesson.Start < next.Lesson.Start);
    public async Task PauseForClassroomModeAsync()
    {
        await _gate.WaitAsync();
        try { await StopAutomaticAsync("课堂模式暂停自动录制"); }
        finally { _gate.Release(); }
    }
    public async Task<IDisposable> ReserveIdleForRuntimeAsync(CancellationToken token = default)
    {
        await _gate.WaitAsync(token);
        try
        {
            if (_stopping || Volatile.Read(ref _runtimeReserved) != 0 ||
                !CanReserveForRuntime(State, _worker is { Alive: true }))
                throw new RemoteExamException("RECORDING_BUSY");
            Volatile.Write(ref _runtimeReserved, 1);
            return new RuntimeOperationGate.Lease(() => Interlocked.Exchange(ref _runtimeReserved, 0));
        }
        finally { _gate.Release(); }
    }
    public static bool CanReserveForRuntime(RecordingState state, bool workerAlive) =>
        !workerAlive && state.Phase is "Idle" or "Saved" or "Failed";

    public async Task<IDisposable> FinishAndReserveForExamAsync(CancellationToken token)
    {
        await _gate.WaitAsync(token);
        bool saving = false;
        bool ownsReservation = false;
        try
        {
            if (_stopping || Volatile.Read(ref _runtimeReserved) != 0) throw new RemoteExamException("RECORDING_BUSY");
            Volatile.Write(ref _runtimeReserved, 1);
            ownsReservation = true;
            saving = State.Active;
            if (_worker is not null && State is { Active: true, Control: { } control } && State.Phase != "Saving")
            {
                if (control.Owner == "Automatic")
                    _ledger.Update(control.SessionId, "Finalizing", "学校切入考试，正常保存本节录制。", State);
                await _worker.CommandAsync("stop", control);
            }
        }
        catch { if (ownsReservation) Volatile.Write(ref _runtimeReserved, 0); throw; }
        finally { _gate.Release(); }
        try
        {
            var watch = Stopwatch.StartNew();
            while (watch.Elapsed < TimeSpan.FromSeconds(60))
            {
                token.ThrowIfCancellationRequested();
                if (saving && State.Phase == "Failed") throw new RemoteExamException("RECORDING_SAVE_FAILED");
                if (CanReserveForRuntime(State, _worker is { Alive: true }))
                    return new RuntimeOperationGate.Lease(() => Interlocked.Exchange(ref _runtimeReserved, 0));
                await Task.Delay(100, token);
            }
            throw new RemoteExamException("RECORDING_SAVE_TIMEOUT");
        }
        catch { Volatile.Write(ref _runtimeReserved, 0); throw; }
    }
    public async Task StopAsync()
    {
        await _gate.WaitAsync();
        try
        {
            _stopping = true; _enabled = false;
            if (_worker is not null && State.Active && State.Control is { } c)
            {
                try { await _worker.CommandAsync("stop", c); }
                catch (Exception error) when (error is IOException or OperationCanceledException or InvalidOperationException)
                { _error = "停止控制连接不可用，录制器将按原截止或控制租约自行结束。"; }
            }
        }
        finally { _gate.Release(); }
    }
    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        // Keep observing finalization to persist Recorded only after the worker confirms verified output.
        var wait = Stopwatch.StartNew();
        while (State.Active && wait.Elapsed < TimeSpan.FromMinutes(3)) await Task.Delay(100);
        _lifetime.Cancel(); await _loop;
        if (_activePlan is not null && State.Control is { Owner: "Automatic" } c)
        {
            try { _ledger.Update(c.SessionId, State.Phase == "Saved" ? "Recorded" : "Interrupted", state: State); } catch (IOException) { }
        }
        if (_worker is not null) await _worker.DisposeAsync();
        _lifetime.Dispose();
    }
}

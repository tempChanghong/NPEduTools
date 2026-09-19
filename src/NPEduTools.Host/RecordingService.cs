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
    private string _message = "自动录制未启用", _error = "";
    private RecordingState _last = new("Idle", "准备录制");
    private static TimeSpan Elapsed => Stopwatch.GetElapsedTime(0);
    public RecordingService(string pipe, string directory, Func<SchoolClockFrame> snapshot, Func<bool>? modePaused = null)
    {
        _snapshot = snapshot;
        _modePaused = modePaused ?? (() => false);
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
            var value = new AutomaticRecordingState(_enabled, _client, _modePaused() ? "课堂模式已暂停自动录制；原计划保留。" : _message, _ledger.Document.SkipDate,
                _ledger.Document.Entries.TakeLast(20).Reverse().ToArray(), string.IsNullOrEmpty(_error) ? null : _error, _modePaused());
            // Long Unicode paths/reasons must not make the entire status endpoint exceed its frame limit.
            while (value.Recent.Length > 0 && JsonSerializer.SerializeToUtf8Bytes(value, Protocol.Json).Length > 32000)
                value = value with { Recent = value.Recent[..^1] };
            return value;
        }
    }
    private RecordingPlanBook ReadBook()
    {
        if (!File.Exists(_bookPath)) throw new InvalidDataException("请先在计划页保存录课规则。");
        return new RecordingPlanBookStore(_bookPath).Read();
    }
    public async Task<HostResponse> HandleAsync(HostRequest request)
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
                        _ledger.Update(expected.SessionId, "Finalizing", "用户提前结束本节；不再重复开始。", State);
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
                    case "skip-next":
                        if (State is { Active: true, Control.Owner: "Automatic" }) await StopAutomaticAsync("用户结束本节");
                        else
                        {
                            var clock = _clock.Read(Elapsed);
                            if (!clock.CanStart || clock.Now is not { } current) throw new InvalidOperationException("等待学校时间。");
                            var day = DateOnly.FromDateTime(current.Date); var source = _clock.Schedule;
                            var next = CalendarRecordingPlanner.Build(ReadBook(), day, source?.Date == day ? source : null)
                                .FirstOrDefault(p => p.Selected && !p.Conflict && p.End > current && !_ledger.Contains(source?.ProfileId ?? Guid.Empty, p));
                            if (next is not null) _ledger.Add(source?.ProfileId ?? Guid.Empty, day, next, "Skipped", "用户跳过本日下一项任务");
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
        _last = _worker.State;
        await _worker.DisposeAsync();
        if (_worker.Alive) throw new InvalidOperationException("上一录制仍在收尾，请稍后重试。");
        _worker = null;
    }
    private async Task RunAsync()
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(250));
        long previous = Stopwatch.GetTimestamp();
        try
        {
            while (await timer.WaitForNextTickAsync(_lifetime.Token))
            {
                await _gate.WaitAsync(_lifetime.Token);
                try
                {
                    if (_enabled && _enabledAt < previous && Stopwatch.GetElapsedTime(previous) > TimeSpan.FromSeconds(5))
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
        _clock.Accept(_snapshot(), Elapsed, 0);
        var reading = _clock.Read(Elapsed); var now = reading.Now;
        var state = State;
        if (_activePlan is not null && state.Control is { Owner: "Automatic" } control)
        {
            string phase = state.Phase switch { "Starting" => "Starting", "Recording" => "Recording", "Paused" or "Pausing" => "Paused",
                "Saving" => "Finalizing", "Saved" => "Recorded", _ => "Failed" };
            var prior = _ledger.Document.Entries.Single(e => e.SessionId == control.SessionId);
            string? reason = phase == "Recorded" ? prior.Reason + "；会话已结束，MP4 已保存并校验" : state.Phase == "Failed" ? state.Error :
                prior.Phase == "Starting" && phase == "Recording" ? "录制器已确认开始" : null;
            _ledger.Update(control.SessionId, phase, reason, state);
            if (!state.Active)
            { _activePlan = null; await RetireAsync(); }
        }
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
        if (!_enabled || !reading.CanStart || now is null) { if (_enabled) _message = reading.Message; return; }
        DateOnly date = DateOnly.FromDateTime(now.Value.Date);
        var source = _clock.Schedule; if (source?.Date != date) source = null;
        var book = ReadBook();
        var plans = CalendarRecordingPlanner.Build(book, date, source);
        if (_ledger.Document.SkipDate == date) { _message = "学校今天不再自动录制"; await StopAutomaticAsync(_message); return; }
        if (_activePlan is { } ongoing)
        {
            var replacement = plans.FirstOrDefault(p => p.Selected && !p.Conflict && p.Fixed == ongoing.Fixed &&
                (p.Fixed ? p.Key == ongoing.Key : source?.ProfileId == _activeProfile && p.Lesson.Start < ongoing.Lesson.End && ongoing.Lesson.Start < p.Lesson.End));
            if (replacement is null) await StopAutomaticAsync("课表、规则、暂停或档案发生变化，本节不再执行");
            else if (replacement.End < ongoing.End) _activePlan = ongoing with { End = replacement.End };
        }
        foreach (var plan in plans.Where(p => p.Selected && now >= p.Start && !_ledger.Contains(source?.ProfileId ?? Guid.Empty, p)))
        {
            if (!plan.Fixed && (source is null || !source.Enabled || !source.ClockVerified)) continue;
            if (now.Value.AddMilliseconds(reading.AgeMs) >= plan.Lesson.End)
            { _ledger.Add(source?.ProfileId ?? Guid.Empty, date, plan, plan.Conflict ? "Conflict" : "Missed", plan.Conflict ? plan.Reason : "正式时段已结束，不补录课后余量"); continue; }
            if (plan.Conflict) continue;
            if (State.Active || _worker is { Alive: true })
            { _ledger.Add(source?.ProfileId ?? Guid.Empty, date, plan, "Skipped", "录制器被手动或上一会话占用，本次不再补开"); continue; }
            await RetireAsync();
            var entry = _ledger.Add(source?.ProfileId ?? Guid.Empty, date, plan, "Starting", "启动意图已保存，等待录制器确认");
            _activePlan = plan; _activeProfile = source?.ProfileId ?? Guid.Empty;
            var limit = TimeSpan.FromMilliseconds(Math.Max(0, (plan.End - now.Value).TotalMilliseconds - reading.AgeMs));
            var owner = new RecorderControl("Automatic", entry.SessionId, entry.Key, RecorderDeadline.After(limit), RecorderDeadline.After(TimeSpan.FromSeconds(8)));
            try { await StartAsync(_options!, owner); }
            catch (Exception error) when (error is not OutOfMemoryException)
            { _ledger.Update(entry.SessionId, "Failed", error.Message, State); _activePlan = null; }
            break;
        }
        var next = plans.FirstOrDefault(p => p.Selected && !p.Conflict && p.Start > now && !_ledger.Contains(source?.ProfileId ?? Guid.Empty, p));
        _message = State is { Active: true, Control.Owner: "Automatic" } ? "正在自动录制；暂停不会延长截止" :
            next is null ? "自动录制已启用，本日暂无待开始任务" : $"下次自动录制：{next.Lesson.Subject} · 学校时间 {next.Start:HH:mm:ss}，可跳过下一项";
    }
    private async Task StopAutomaticAsync(string reason)
    {
        if (_worker is not null && State is { Active: true, Control: { Owner: "Automatic" } c })
        {
            // Persist the reason first. Even if this fails, always attempt to stop this exact session.
            try { _ledger.Update(c.SessionId, "Finalizing", reason, State); }
            finally { await _worker.CommandAsync("stop", c); }
        }
    }
    public async Task PauseForClassroomModeAsync()
    {
        await _gate.WaitAsync();
        try { await StopAutomaticAsync("课堂模式暂停自动录制"); }
        finally { _gate.Release(); }
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

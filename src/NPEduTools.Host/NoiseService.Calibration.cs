using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using NPEduTools.Contracts;
using NPEduTools.Core;

namespace NPEduTools.Host;

public interface INoiseCaptureMetadata
{
    // Includes capture format and endpoint gain/mute. Does not claim that driver processing is disabled.
    string Signature { get; }
}

public sealed partial class NoiseService
{
    private sealed record CalibrationDraft(Guid Id, string DeviceId, string? Context, string? Signature,
        IReadOnlyList<NoiseCalibrationSample> Samples);
    private sealed record CalibrationFile(int Version, CalibrationDraft? Draft, NoiseCalibrationProfile? Profile);
    private CalibrationDraft? _calDraft;
    private NoiseCalibrationProfile? _calProfile;
    private long _calRevision;
    private bool _calLoaded;
    private string? _calError, _lastSignature;
    private static string? ContextKey(string? scope, string? version) => scope is null || version is null ? null :
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(scope + "\n" + version)));
    private string? CalibrationPath => directory is null ? null : Path.Combine(directory, "noise-quick-calibration.json");

    private static bool ValidSample(NoiseCalibrationSample? s) => s is not null && s.Id != Guid.Empty &&
        NoiseCalibrationContract.Scenes.Contains(s.Scene) && NoiseCalibrationContract.Labels.Contains(s.Label) &&
        double.IsFinite(s.Seconds) && s.Seconds is >= 0 and <= 30 && double.IsFinite(s.Coverage) && s.Coverage is >= 0 and <= 1 &&
        double.IsFinite(s.ClippedPercent) && s.ClippedPercent is >= 0 and <= 100 &&
        new[] { s.P10Dbfs, s.MedianDbfs, s.P90Dbfs }.All(v => v is null || double.IsFinite(v.Value) && v is >= -160 and <= 20) &&
        (s.Quality != "Good" || s.Seconds is >= 13.5 and <= 16 && s.Coverage >= .9 && s.ClippedPercent <= .1 &&
            s.P10Dbfs is not null && s.MedianDbfs is not null && s.P90Dbfs is not null &&
            s.P10Dbfs <= s.MedianDbfs && s.MedianDbfs <= s.P90Dbfs);
    private void LoadCalibration()
    {
        if (_calLoaded) return;
        _calLoaded = true;
        try
        {
            if (CalibrationPath is not { } path || !File.Exists(path)) return;
            if (new FileInfo(path).Length > 128 * 1024) throw new InvalidDataException();
            var saved = JsonSerializer.Deserialize<CalibrationFile>(File.ReadAllText(path), Protocol.Json);
            if (saved is not { Version: 1 }) throw new InvalidDataException();
            if (saved.Draft is { } d && (d.Id == Guid.Empty || !NoiseContract.ValidDeviceId(d.DeviceId) ||
                d.Samples is null || d.Samples.Count > 20 || d.Samples.Any(s => !ValidSample(s)) ||
                d.Context is { Length: not 64 } || d.Signature is { Length: > 4096 })) throw new InvalidDataException();
            if (saved.Profile is { } p && (p.Version != 1 || !NoiseContract.ValidDeviceId(p.DeviceId) ||
                !NoiseCalibrationContract.Thresholds(p.MediumDbfs, p.HighDbfs) || p.Context is not { Length: 64 } ||
                p.Signature is not { Length: > 0 and <= 4096 } || p.Samples is null || p.Samples.Count is < 2 or > 20 ||
                p.Samples.Any(s => !ValidSample(s)))) throw new InvalidDataException();
            _calDraft = saved.Draft; _calProfile = saved.Profile;
        }
        catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException or JsonException)
        { _calError = "CALIBRATION_STORE_UNAVAILABLE"; } // Preserve corrupt files.
    }
    private bool SaveCalibration(CalibrationDraft? draft, NoiseCalibrationProfile? profile)
    {
        if (_calError is not null) return false;
        if (CalibrationPath is not { } path) { _calError = "CALIBRATION_STORE_UNAVAILABLE"; return false; }
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            { JsonSerializer.Serialize(stream, new CalibrationFile(1, draft, profile), Protocol.Json); stream.Flush(true); }
            File.Move(temporary, path, true); _calDraft = draft; _calProfile = profile; _calRevision++; return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        { _calError = "CALIBRATION_STORE_UNAVAILABLE"; return false; }
        finally { try { File.Delete(temporary); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
    }
    public StudyNoise ResolveCalibration(string? scope, string? version, StudyNoise policy)
    {
        lock (_sync)
        {
            LoadCalibration();
            if (policy.Estimated is null && _calError is null && _calProfile is { } p && p.DeviceId == _selected &&
                p.Context == ContextKey(scope, version) && p.Signature == _lastSignature)
                return policy with { MediumLineDbfs = p.MediumDbfs, RedLineDbfs = p.HighDbfs };
            return policy;
        }
    }
    private NoiseCalibrationState CalibrationSnapshot(string? context, bool scheduled = false)
    {
        LoadCalibration();
        bool sampling = _session is { Calibration: not null, Work.IsCompleted: false };
        double progress = sampling ? Math.Min(NoiseQuickCalibration.DurationSeconds, _time.GetElapsedTime(_session!.Started).TotalSeconds) : 0;
        if (sampling && progress >= NoiseQuickCalibration.DurationSeconds)
        { _state = "Stopping"; _session!.Stop.Cancel(); }
        var samples = _calDraft?.Samples ?? [];
        bool applied = _calError is null && _calProfile is { } p && p.DeviceId == _selected &&
            p.Context == context && p.Signature == _lastSignature;
        string state = sampling ? "Sampling" : scheduled || _session is { Work.IsCompleted: false } ? "Busy" : "Ready";
        return new(_instance, _calRevision, _calDraft?.Id, _selected, context, state,
            progress, samples, NoiseQuickCalibration.Suggest(samples), _calProfile, applied, _calError,
            _session is { } selectedSession && selectedSession.DeviceId == _selected ? selectedSession.DeviceName : null);
    }
    public HostResponse CalibrationHandle(HostRequest request, string? scope = null, StudyHostState? study = null)
    {
        bool paused = CalibrationRuntimePaused();
        lock (_sync)
        {
            string? context = study?.PolicyState == "APPLIED" ? ContextKey(scope, study.Version) : null;
            LoadCalibration();
            HostResponse Reply(string? error = null, string message = "快速校准状态。", string outcome = "Succeeded") =>
                new(1, request.RequestId, error is null ? outcome : "Rejected", error, message,
                    NoiseCalibration: CalibrationSnapshot(context, paused || study?.Phase is "Preparing" or "Active" || study?.Reason == "EXAM_PAUSED"));
            if (Protocol.Validate(request) is { } invalid) return Reply(invalid);
            if (request.Capability == "noise.calibration.status") return Reply();
            if (request.Capability != "noise.calibration.command") return Reply("UnknownCapability");
            var cmd = request.NoiseCalibration!;
            if (_disposed || _shuttingDown || ExitPending) return Reply("NoiseStopping", "后台正在退出，未进行校准。");
            if (cmd.InstanceId != _instance || cmd.Revision != _calRevision) return Reply("CALIBRATION_CHANGED", "校准状态已变化，请刷新后重试。");
            if (management?.VerifyCalibration(cmd.Secret) is { } denied) return Reply(denied, "本机管理验证未通过；未更改校准。");
            if (management is null) return Reply("MANAGEMENT_NOT_CONFIGURED", "请先设置本机管理口令。");
            if (_calError is not null) return Reply(_calError, "校准文件不可用，原文件已保留。");
            if (cmd.Action == "cancel")
            {
                if (_calDraft?.Id != cmd.DraftId) return Reply("CALIBRATION_CHANGED");
                if (_session is { Calibration: not null, Work.IsCompleted: false } s)
                { s.CalibrationInterrupted = true; _state = "Stopping"; s.Stop.Cancel(); _calRevision++; }
                return Reply(message: "已取消本段采样；其他样本保留。");
            }
            if (_session is { Work.IsCompleted: false }) return Reply("NoiseBusy", "已有采集正在运行，校准不会停止或接管该会话。");
            if (paused || study?.Reason == "EXAM_PAUSED") return Reply("CALIBRATION_RUNTIME_PAUSED", "考试、暂停或运行状态未确认，暂不可操作快速校准；原配置保留。");
            if (study?.Phase is "Preparing" or "Active") return Reply("CALIBRATION_SCHEDULE_BUSY", "准备或自习阶段不能进行校准，请在时段外操作。");
            if (_selected is null) return Reply("MicrophoneNotConfigured", "请先保存监测麦克风。");
            if (cmd.Action == "begin")
            {
                var draft = new CalibrationDraft(Guid.NewGuid(), _selected, context, null, []);
                return SaveCalibration(draft, _calProfile) ? Reply(message: "已新建采样草稿；原确认阈值保留。") : Reply(_calError);
            }
            if (_calDraft is not { } current || current.Id != cmd.DraftId || current.DeviceId != _selected || current.Context != context)
                return Reply("CALIBRATION_CHANGED", "麦克风或学校配置已变化，请新建校准草稿。");
            if (cmd.Action == "clear") return SaveCalibration(current, null) ? Reply(message: "已恢复学校配置的公共阈值。") : Reply(_calError);
            if (cmd.Action is "remove" or "label")
            {
                if (!current.Samples.Any(s => s.Id == cmd.SampleId)) return Reply("CALIBRATION_CHANGED");
                var changed = current.Samples.Where(s => cmd.Action != "remove" || s.Id != cmd.SampleId)
                    .Select(s => cmd.Action == "label" && s.Id == cmd.SampleId ? s with { Label = cmd.Label! } : s).ToArray();
                return SaveCalibration(current with { Samples = changed }, _calProfile) ? Reply(message: "样本已更新。") : Reply(_calError);
            }
            if (cmd.Action == "confirm")
            {
                if (context is null) return Reply("CALIBRATION_SCHOOL_UNAVAILABLE", "请连接学校并获取有效配置，再确认阈值。");
                var good = current.Samples.Where(s => s.Quality == "Good").ToArray();
                if (current.Signature is null || current.Signature != _lastSignature || good.Length < 2 ||
                    !good.Any(s => s.Label == "Acceptable") || !good.Any(s => s.Label == "Discipline"))
                    return Reply("CALIBRATION_SAMPLES_REQUIRED", "需要同一设备设置下可接受、纪律场景的有效样本；电平重叠时可手动确认。");
                var profile = new NoiseCalibrationProfile(1, _selected, current.Signature, context, cmd.MediumDbfs!.Value,
                    cmd.HighDbfs!.Value, _time.GetUtcNow(), good);
                return SaveCalibration(current, profile) ? Reply(message: "已确认本机双阈值；学校公共设置未更改，仍使用 dBFS。") : Reply(_calError);
            }
            if (cmd.Action != "start") return Reply("UnknownCapability");
            if (current.Samples.Count >= 20) return Reply("CALIBRATION_SAMPLE_LIMIT", "最多保留 20 段，请删除不需要的样本。");
            if (current.Signature is { } draftSignature && _lastSignature is { } latest && draftSignature != latest)
                return Reply("CALIBRATION_DEVICE_SETTINGS_CHANGED", "采集设置已变化，请新建草稿重新采样；不混合不同设置的样本。");
            if (_cleanupFailed) return Reply("NoiseCleanupRequired", "麦克风未确认释放，请重启后台。");
            var listed = Handle(request with { Capability = "noise.devices", NoiseCalibration = null });
            if (listed.Outcome != "Succeeded" || listed.NoiseDevices?.Any(d => d.Id == _selected) != true)
                return Reply("NoiseDeviceUnavailable", "所选麦克风不可用；不会切换其他设备。");
            var response = Handle(new(1, request.RequestId, "noise.command",
                Noise: new("start", _instance, _revision, _selected)), null, NoiseQuickCalibration.DurationSeconds, false,
                new NoiseQuickCalibration(cmd.Scene!, cmd.Label!));
            if (response.Outcome != "Accepted") return Reply(response.ErrorCode, response.Message);
            _calRevision++;
            return Reply(message: "正在采样 15 秒；只保留电平统计。", outcome: "Accepted");
        }
    }
    private void FinishCalibration(Session session)
    {
        if (_calDraft is not { } d || session.Calibration is null) return;
        var sample = session.Calibration.Finish(session.FinishedSeconds!.Value, session.CalibrationInterrupted || session.Failure.Length > 0);
        SaveCalibration(d with { Samples = d.Samples.Append(sample).Take(20).ToArray(),
            Signature = d.Signature ?? session.Signature }, _calProfile);
    }
}

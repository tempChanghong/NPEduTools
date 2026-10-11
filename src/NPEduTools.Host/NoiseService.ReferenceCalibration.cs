using System.Text.Json;
using NPEduTools.Contracts;
using NPEduTools.Core;

namespace NPEduTools.Host;

public interface IReferenceNoiseCapture : INoiseCapture, INoiseCaptureMetadata
{
    event Action<ReferenceNoiseFrame>? ReferenceFrame;
    string ReferenceSignature => Signature;
    void EnableReference() { }
}

public sealed partial class NoiseService
{
    private sealed record ReferenceFile(int Version, NoiseReferenceDraft? Draft, NoiseReferenceProfile? Profile, string? Invalidated = null);
    private NoiseReferenceDraft? _referenceDraft;
    private NoiseReferenceProfile? _referenceProfile;
    private bool _referenceLoaded;
    private long _referenceRevision;
    private string? _referenceError, _lastReferenceSignature;
    private long? _referenceSignatureAt;
    private bool RecentReferenceSignature => _referenceSignatureAt is { } at && _time.GetElapsedTime(at).TotalSeconds is >= 0 and < 600;
    private string? ReferencePath => directory is null ? null : Path.Combine(directory, "noise-reference-calibration.json");
    private string? ReferenceUsePath => directory is null ? null : Path.Combine(directory, "noise-reference-use.pending");
    private static bool ReferenceSignature(string? value) => value is { Length: > 0 and <= 4096 } &&
        value.StartsWith(ReferenceNoiseMeter.AlgorithmVersion + "|", StringComparison.Ordinal) && !value.Any(char.IsControl);
    private static bool ReferenceSampleValid(NoiseReferenceSample? s) => s is not null && s.Id != Guid.Empty &&
        s.Role is "Primary" or "Check" && double.IsFinite(s.Seconds) && s.Seconds is >= 0 and <= 10.01 &&
        double.IsFinite(s.Coverage) && s.Coverage is >= 0 and <= 1 &&
        double.IsFinite(s.ClippedPercent) && s.ClippedPercent is >= 0 and <= 100 &&
        s.Quality is "Good" or "Interrupted" or "Invalid" or "Clipping" or "Incomplete" &&
        new[] { s.DigitalDbfs, s.MinimumDbfs, s.MaximumDbfs }.All(v => v is null || double.IsFinite(v.Value) && v is >= -160 and <= 20) &&
        s.WindowEndedAt >= s.WindowStartedAt && (s.WindowEndedAt - s.WindowStartedAt).TotalSeconds <= 21 &&
        (s.Quality != "Good" || s.Seconds >= 9.999 && s.Coverage >= .9 && s.ClippedPercent <= .1 &&
            s.MinimumDbfs > NoiseMeter.NumericalFloorDbfs && s.MaximumDbfs is not null &&
            s.DigitalDbfs >= s.MinimumDbfs - 1e-8 && s.DigitalDbfs <= s.MaximumDbfs + 1e-8 &&
            (s.WindowEndedAt - s.WindowStartedAt).TotalSeconds >= 9.7) &&
        (s.MeterEvidence is null || NoiseReferenceContract.MeterEvidenceValid(s.MeterEvidence)) &&
        (s.SameWindowConfirmed ? NoiseReferenceContract.ReferenceRange(s.ReferenceDbA, s.ReferenceMinimumDbA, s.ReferenceMaximumDbA)
            : s.ReferenceDbA is null && s.ReferenceMinimumDbA is null && s.ReferenceMaximumDbA is null);
    private static bool ReferenceDraftValid(NoiseReferenceDraft? d) => d is not null && d.Id != Guid.Empty &&
        NoiseContract.ValidDeviceId(d.DeviceId) && NoiseReferenceContract.InstrumentValid(d.Instrument) &&
        (d.MeterDevicePath is null || NoiseReferenceContract.MeterPathValid(d.MeterDevicePath)) &&
        d.TimeWeighting is "Fast" or "Slow" && NoiseReferenceContract.Limit(d.StabilityDb) && NoiseReferenceContract.Limit(d.CheckToleranceDb) &&
        (d.Signature is null || ReferenceSignature(d.Signature)) && d.Samples is not null && d.Samples.Count <= 2 &&
        d.Samples.All(s => ReferenceSampleValid(s) && (d.MeterDevicePath is null ? s.MeterEvidence is null :
            !s.SameWindowConfirmed || NoiseReferenceContract.MeterEvidenceValid(s.MeterEvidence))) &&
        d.Samples.Select(s => s.Role).Distinct().Count() == d.Samples.Count &&
        d.Samples.Select(s => s.Id).Distinct().Count() == d.Samples.Count;
    private static bool ReferenceProfileValid(NoiseReferenceProfile p)
    {
        var draft = new NoiseReferenceDraft(Guid.NewGuid(), p.DeviceId, p.Instrument, p.TimeWeighting,
            p.StabilityDb, p.CheckToleranceDb, p.Signature, p.Samples, p.MeterDevicePath);
        if (p.Samples is null || p.Version != 1 || p.Algorithm != ReferenceNoiseMeter.AlgorithmVersion ||
            !ReferenceDraftValid(draft) ||
            p.Method != (p.MeterDevicePath is null ? "ManualReference" : "UsbReferenceBeta") ||
            p.MeterDevicePath is null && p.Samples.Any(s => s.MeterEvidence is not null) ||
            p.Statistic != NoiseReferenceCalibration.Statistic || !ReferenceSignature(p.Signature) ||
            p.Samples.Count != 2 || !double.IsFinite(p.OffsetDb)) return false;
        var evaluation = NoiseReferenceCalibration.Evaluate(draft);
        return evaluation.Ready && Math.Abs(evaluation.OffsetDb!.Value - p.OffsetDb) < 1e-8;
    }
    private void LoadReference()
    {
        if (_referenceLoaded) return;
        _referenceLoaded = true;
        try
        {
            if (ReferencePath is not { } path || !File.Exists(path)) return;
            if (new FileInfo(path).Length > 64 * 1024) throw new InvalidDataException();
            var file = JsonSerializer.Deserialize<ReferenceFile>(File.ReadAllText(path), Protocol.Json);
            if (file is not { Version: 1 } || file.Draft is not null && !ReferenceDraftValid(file.Draft) ||
                file.Profile is not null && !ReferenceProfileValid(file.Profile)) throw new InvalidDataException();
            if (file.Invalidated is not null && file.Invalidated is not ("DEVICE_CHANGED" or "INPUT_SETTINGS_CHANGED" or "INPUT_SETTINGS_UNAVAILABLE")) throw new InvalidDataException();
            _referenceDraft = file.Draft; _referenceProfile = file.Profile; _referenceInvalidated = file.Invalidated;
            // This marker predates capture. A crash or a later unwritable directory cannot
            // erase the evidence that the preceding input checks did not finish durably.
            if (_referenceProfile is not null && ReferenceUsePath is { } usePath && File.Exists(usePath))
                _referenceInvalidated ??= "INPUT_SETTINGS_UNAVAILABLE";
        }
        catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException or JsonException)
        { _referenceError = "REFERENCE_STORE_UNAVAILABLE"; } // Preserve originals, never activate malformed data.
    }
    private sealed class ReferenceUseException : IOException { }
    private bool _referenceMarkerArmed;
    private void BeginReferenceUse(Session s)
    {
        LoadReference();
        if (_referenceProfile is null || _referenceInvalidated is not null) return;
        if (_referenceError is not null) throw new ReferenceUseException();
        EnsureReferenceUseMarker();
        s.ReferenceUseArmed = true;
    }
    private void EnsureReferenceUseMarker()
    {
        if (_referenceMarkerArmed) return;
        try
        {
            if (_referenceProfile is not { } profile) throw new ReferenceUseException();
            if (ReferenceUsePath is not { } path) throw new IOException();
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough);
            System.Text.Json.JsonSerializer.Serialize(stream, new { profileId = ProfileId(profile) }, Protocol.Json);
            stream.Flush(true);
            _referenceMarkerArmed = true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        { _referenceError = "REFERENCE_STORE_UNAVAILABLE"; throw new ReferenceUseException(); }
    }
    private bool ClearReferenceUse()
    {
        try { if (ReferenceUsePath is { } path) File.Delete(path); _referenceMarkerArmed = false; return true; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        { _referenceError = "REFERENCE_STORE_UNAVAILABLE"; _referenceInvalidated ??= "INPUT_SETTINGS_UNAVAILABLE"; return false; }
    }
    private void CheckFinalReferenceInput(Session s)
    {
        if (!s.ReferenceUseArmed || _referenceInvalidated is not null || _referenceProfile is not { } profile) return;
        try
        {
            if (s.Metadata is not IReferenceNoiseCapture input) InvalidateReference("INPUT_SETTINGS_UNAVAILABLE");
            else if (input.ReferenceSignature != profile.Signature) InvalidateReference("INPUT_SETTINGS_CHANGED");
        }
        catch (Exception e) when (e is not OutOfMemoryException) { InvalidateReference("INPUT_SETTINGS_UNAVAILABLE"); }
        if (s.EstimateProfile is not null) s.EstimateReason = _referenceError ?? _referenceInvalidated;
    }
    private void FinishReferenceUse(Session s)
    {
        if (!s.ReferenceUseArmed) return;
        if (s.Failure.Length > 0) return; // Preserve the marker without relabelling an audio fault as an observed input change.
        if (_referenceInvalidated is null && _referenceError is null && SaveReference(_referenceDraft, _referenceProfile)) ClearReferenceUse();
        if (s.EstimateProfile is not null) s.EstimateReason = _referenceError ?? _referenceInvalidated;
        // Any failure retains the prewritten marker, including a failed tombstone write.
    }
    private bool SaveReference(NoiseReferenceDraft? draft, NoiseReferenceProfile? profile)
    {
        if (_referenceError is not null) return false;
        if (ReferencePath is not { } path) { _referenceError = "REFERENCE_STORE_UNAVAILABLE"; return false; }
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            {
                JsonSerializer.Serialize(stream, new ReferenceFile(1, draft, profile, _referenceInvalidated), Protocol.Json);
                if (stream.Position > 64 * 1024) throw new InvalidDataException();
                stream.Flush(true);
            }
            File.Move(temporary, path, true);
            _referenceDraft = draft; _referenceProfile = profile; _referenceRevision++; return true;
        }
        catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException)
        { _referenceError = "REFERENCE_STORE_UNAVAILABLE"; return false; }
        finally { try { File.Delete(temporary); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
    }
    private NoiseReferenceState ReferenceSnapshot(StudyHostState? study, bool paused)
    {
        LoadReference();
        bool sampling = _session is { Reference: not null, Work.IsCompleted: false };
        double progress = sampling ? Math.Min(NoiseReferenceCalibration.MaximumCaptureSeconds, _time.GetElapsedTime(_session!.Started).TotalSeconds) : 0;
        if (sampling && progress >= NoiseReferenceCalibration.MaximumCaptureSeconds)
        { _state = "Stopping"; _session!.Stop.Cancel(); }
        bool busy = paused || study?.Phase is "Preparing" or "Active" || study?.Reason == "EXAM_PAUSED" || _session is { Work.IsCompleted: false };
        return new(_instance, _referenceRevision, sampling ? "Sampling" : busy ? "Busy" : referenceCreate is null ? "Unsupported" : "Ready",
            _selected, _session is { } selectedSession && selectedSession.DeviceId == _selected ? selectedSession.DeviceName : null, progress,
            sampling ? _session!.Reference!.MeasuredSeconds : 0, _referenceDraft, NoiseReferenceCalibration.Evaluate(_referenceDraft),
            _referenceProfile, _referenceError is null && _referenceInvalidated is null && RecentReferenceSignature && _referenceProfile is { } p && p.DeviceId == _selected && p.Signature == _lastReferenceSignature,
            _referenceError, _session?.Reference is not null ? _message : null, _referenceInvalidated,
            sampling && _session!.UsbReference is not null ? MeterSnapshot(_session!) : null);
    }
    public HostResponse ReferenceHandle(HostRequest request, StudyHostState? study = null)
    {
        // Read external runtime flags outside the capture lock to avoid a new cross-service lock order.
        bool paused = CalibrationRuntimePaused();
        lock (_sync)
        {
            LoadReference();
            HostResponse Reply(string? error = null, string message = "外部参考校准状态。", string outcome = "Succeeded") =>
                new(1, request.RequestId, error is null ? outcome : "Rejected", error, message,
                    NoiseReference: ReferenceSnapshot(study, paused));
            if (Protocol.Validate(request) is { } invalid) return Reply(invalid);
            if (request.Capability == "noise.reference.status") return Reply();
            if (request.Capability != "noise.reference.command") return Reply("UnknownCapability");
            var cmd = request.NoiseReference!;
            if (_disposed || _shuttingDown || ExitPending) return Reply("NoiseStopping", "后台维护退出中，未操作参考校准。");
            if (cmd.InstanceId != _instance || cmd.Revision != _referenceRevision) return Reply("REFERENCE_CHANGED", "参考状态已变化，请刷新后重试。");
            if (management is null) return Reply("MANAGEMENT_NOT_CONFIGURED", "请先设置本机管理口令。");
            if (management.VerifyCalibration(cmd.Secret) is { } denied) return Reply(denied, "本机管理验证未通过，未操作参考校准。");
            if (_referenceError is not null) return Reply(_referenceError, "参考文件不可用，原文件已保留。");
            if (cmd.Action == "cancel")
            {
                if (_referenceDraft?.Id != cmd.DraftId) return Reply("REFERENCE_CHANGED");
                if (_session is { Reference: not null, Work.IsCompleted: false } current)
                { current.CalibrationInterrupted = true; _state = "Stopping"; current.Stop.Cancel(); _referenceRevision++; }
                return Reply(message: "已取消本段参考采样，其他统计与原确认参考保留。");
            }
            if (_session is { Work.IsCompleted: false }) return Reply("NoiseBusy", "已有采集尚未释放，参考流程不会停止或接管它。");
            if (paused || study?.Phase is "Preparing" or "Active" || study?.Reason == "EXAM_PAUSED")
                return Reply("REFERENCE_SCHEDULE_BUSY", "准备、自习或考试运行阶段不能操作参考校准。");
            if (cmd.Action == "clear") { _referenceInvalidated = null; return SaveReference(_referenceDraft, null) && ClearReferenceUse() ? Reply(message: "已清除外部参考配置；快速阈值配置保留。") : Reply(_referenceError); }
            if (_selected is null) return Reply("MicrophoneNotConfigured", "请先在本机保存监测麦克风。");
            if (cmd.Action == "begin")
            {
                if (cmd.MeterDevicePath is not null && referenceMeterOpen is null)
                    return Reply("REFERENCE_USB_UNSUPPORTED", "当前后台不支持 USB 同期参考采样，请更新后台。");
                var draft = new NoiseReferenceDraft(Guid.NewGuid(), _selected, cmd.Instrument!, cmd.TimeWeighting!,
                    cmd.StabilityDb!.Value, cmd.CheckToleranceDb!.Value, null, [], cmd.MeterDevicePath);
                return SaveReference(draft, _referenceProfile) ? Reply(message: "已新建外部参考草稿；原确认参考保留。") : Reply(_referenceError);
            }
            if (_referenceDraft is not { } d || d.Id != cmd.DraftId || d.DeviceId != _selected)
                return Reply("REFERENCE_CHANGED", "麦克风或草稿已变化，请新建参考草稿。");
            if (cmd.Action is "reference" or "remove")
            {
                if (cmd.Action == "reference" && d.MeterDevicePath is not null)
                    return Reply("REFERENCE_USB_AUTOMATIC", "USB 草稿使用同期自动读数，不能用手动数值替换；请重采或新建手动草稿。");
                if (!d.Samples.Any(s => s.Id == cmd.SampleId)) return Reply("REFERENCE_CHANGED");
                var samples = d.Samples.Where(s => cmd.Action != "remove" || s.Id != cmd.SampleId).Select(s => s.Id == cmd.SampleId ? s with
                { ReferenceDbA = cmd.ReferenceDbA, ReferenceMinimumDbA = cmd.ReferenceMinimumDbA,
                    ReferenceMaximumDbA = cmd.ReferenceMaximumDbA, SameWindowConfirmed = true } : s).ToArray();
                return SaveReference(d with { Samples = samples }, _referenceProfile) ? Reply(message: "参考统计已更新。") : Reply(_referenceError);
            }
            if (cmd.Action == "confirm")
            {
                var evaluation = NoiseReferenceCalibration.Evaluate(d);
                if (!evaluation.Ready) return Reply("REFERENCE_CHECK_FAILED", evaluation.Message);
                if (!RecentReferenceSignature || !ReferenceSignature(d.Signature) || d.Signature != _lastReferenceSignature)
                    return Reply("REFERENCE_DEVICE_SETTINGS_CHANGED", "尚无匹配的新鲜采集签名，请在同一输入设置下重采。");
                var profile = new NoiseReferenceProfile(1, ReferenceNoiseMeter.AlgorithmVersion,
                    d.MeterDevicePath is null ? "ManualReference" : "UsbReferenceBeta", NoiseReferenceCalibration.Statistic,
                    d.DeviceId, d.Signature!, d.Instrument, d.TimeWeighting, evaluation.OffsetDb!.Value,
                    d.StabilityDb, d.CheckToleranceDb, _time.GetUtcNow(), d.Samples, d.MeterDevicePath);
                _referenceInvalidated = null;
                return SaveReference(d, profile) && ClearReferenceUse() ? Reply(message: "已保存本机估算参考；正式监测将核对输入设置，并分别保存 dBFS 和估算 dB(A)。") : Reply(_referenceError);
            }
            if (referenceCreate is null) return Reply("REFERENCE_UNSUPPORTED", "请更新后台以进行 A 加权参考采样。");
            if (d.MeterDevicePath is not null && referenceMeterOpen is null)
                return Reply("REFERENCE_USB_UNSUPPORTED", "当前后台不支持 USB 同期采样，未打开设备。");
            if (_cleanupFailed) return Reply("NoiseCleanupRequired", "麦克风未确认释放，请重启后台。");
            var listed = Handle(request with { Capability = "noise.devices", NoiseReference = null });
            if (listed.Outcome != "Succeeded" || listed.NoiseDevices?.Any(x => x.Id == _selected) != true)
                return Reply("NoiseDeviceUnavailable", "所选麦克风不可用，不会切换其他设备。");
            _session?.Stop.Dispose();
            var session = new Session(_selected, _time.GetTimestamp(), _time.GetUtcNow())
            { LimitSeconds = NoiseReferenceCalibration.MaximumCaptureSeconds,
                MeterPath = d.MeterDevicePath, UsbReference = d.MeterDevicePath is null ? null : new(d.TimeWeighting) };
            session.Reference = new(cmd.Role!, d.TimeWeighting, session.StartedAt);
            _session = session; _revision++; _referenceRevision++; _lastReferenceSignature = null; _referenceSignatureAt = null;
            _state = "Starting"; _message = "正在打开外部参考采样；不会作为正式噪音会话上报。";
            session.Work = Task.Run(() => RunAsync(session)); session.Watch = Task.Run(() => WatchAsync(session));
            return Reply(message: "参考采样已受理：预热后采样 10 秒，总过程最多 20 秒。", outcome: "Accepted");
        }
    }
    private void CheckReferenceSignature(Session s)
    {
        if (s.Reference is null) return;
        if (_referenceProfile is { } p && s.Signature != p.Signature) InvalidateReference("INPUT_SETTINGS_CHANGED");
        if (!ReferenceSignature(s.Signature) || _referenceDraft?.Signature is { } expected && expected != s.Signature)
        { _lastReferenceSignature = null; s.CalibrationInterrupted = true; s.Failure = "A 加权格式或输入设置不匹配，请新建草稿重新采样。"; s.Stop.Cancel(); }
        // Only actual frame receipts establish a recent signature, not factory acceptance.
    }
    private void ReceiveReference(Session s, ReferenceNoiseFrame frame)
    {
        lock (_sync)
        {
            if (_session != s || s.Stop.IsCancellationRequested || s.Reference is null) return;
            s.LastData = _time.GetTimestamp();
            CheckReferenceInput(s);
            try
            {
                if (s.Metadata?.Signature != s.Signature)
                { _lastReferenceSignature = null; s.CalibrationInterrupted = true; s.Failure = "输入音量、静音或格式已变化，参考采样作废。"; s.Stop.Cancel(); return; }
            }
            catch (Exception e) when (e is not OutOfMemoryException)
            { _lastReferenceSignature = null; s.CalibrationInterrupted = true; s.Stop.Cancel(); return; }
            _lastReferenceSignature = s.Signature; _referenceSignatureAt = _time.GetTimestamp();
            s.Reference.Receive(frame, _time.GetElapsedTime(s.Started).TotalSeconds);
            if (s.Reference.Complete) { _state = "Stopping"; s.Stop.Cancel(); }
        }
    }
    private void FinishReference(Session s)
    {
        if (_referenceDraft is not { } draft || s.Reference is null) return;
        var sample = s.Reference.Finish(s.FinishedSeconds!.Value,
            s.CalibrationInterrupted || s.Failure.Length > 0 || _disposed || _shuttingDown);
        if (s.UsbReference is not null)
        {
            sample = s.UsbReference.Attach(sample, s.StartedAt);
            if (s.UsbReference.Error is { } usbError) _message = usbError + " 设备已释放，原确认参考保留。";
        }
        // A cancelled/failed attempt is visible, but never used to confirm a reference.
        SaveReference(draft with { Signature = draft.Signature ?? (ReferenceSignature(s.Signature) ? s.Signature : null),
            Samples = draft.Samples.Where(x => x.Role != sample.Role).Append(sample).ToArray() }, _referenceProfile);
    }
}

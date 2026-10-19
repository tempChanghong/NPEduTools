using System.IO;
using System.Text.Json;
using System.Windows;
using NPEduTools.Contracts;
using NPEduTools.Core;

namespace NPEduTools.App;

public sealed partial class StudyWindow : Window
{
    private readonly string _pipe;
    private readonly bool _manual;
    private readonly StudySurface _surface;
    private readonly StudyDisplayContinuation _continuation;
    public bool CanKeepAfterReadFailure => !_shutdown && IsVisible && _continuation.CanKeep;
    private StudyHostState? _state;
    private StudySeatingUploadWindow? _seatingUpload;
    private bool _shutdown;
    private Guid? _hideRequest;
    private readonly Guid _appInstance = Guid.NewGuid();
    private long _presentationSequence;
    public StudyPresentationPulse? PresentationPulse()
    {
        if (_state is not { Version: { } version, Window: { } window, Display: { } display } state ||
            state.Phase is not ("Preparing" or "Active" or "Ended")) return null;
        bool visible = !_shutdown && IsVisible && _surface.IsLoaded && _surface.IsVisible && WindowState != WindowState.Minimized;
        int? age = visible ? _surface.FreshRenderAgeMs : null;
        return new(display.InstanceId, _appInstance, ++_presentationSequence, version, window, state.Phase,
            visible ? age is null ? "Stale" : "Visible" : "Hidden", age);
    }
    public async Task ReportPresentationAsync()
    {
        if (PresentationPulse() is not { } pulse) return;
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
            await HostClient.RequestAsync(_pipe, new HostRequest(1, Guid.NewGuid(), _manual?"manual.study.presentation":"study.presentation", StudyPresentation: pulse), timeout.Token);
        }
        catch (Exception e) when (e is IOException or OperationCanceledException or InvalidDataException or JsonException or UnauthorizedAccessException or TimeoutException) { }
        if(!_manual)await ReportSeatingAsync();
    }
    private CancellationTokenSource? _homeworkRead;
    private string? _homeworkKey;
    private StudyHomeworkSnapshot? _homeworkSnapshot;
    private string? _homeworkRetryKey;
    private long _homeworkRetryAt;
    public StudyWindow(string pipe, Action manage, bool muted = false, TimeProvider? time = null,bool manual=false)
    {
        _pipe = pipe; _manual=manual; WindowStyle = WindowStyle.None; ResizeMode = ResizeMode.NoResize;
        _continuation = new(time);
        Left = Top = 0; Width = SystemParameters.PrimaryScreenWidth; Height = SystemParameters.PrimaryScreenHeight;
        ShowInTaskbar = false; Topmost = true; Title = manual?"NPEduTools · 手动自习":"NPEduTools · 学校自习";
        _surface = new(HideAsync, () => { HideDisplay(); manage(); }, muted, time, AuthorizeReminderAsync,manual?EndAsync:null); Content = _surface;
        _surface.RefreshCompleted += () => { if (!_manual && IsVisible && !_continuation.CanKeep) HideDisplay(); };
        if (!manual) _surface.SeatingUploadRequested += OpenSeatingUpload;
        Closing += (_, e) => { if (!_shutdown) { e.Cancel = true; _surface.ReturnError("请长按“临时使用电脑”收起展示。"); } };
    }
    public void Apply(StudyHostState state, NoiseState? noise, TimeSpan transportAge = default, StudyPhotoState? photos = null, NpepState? connection = null,
        NoiseCalibrationStatus? calibration = null)
    {
        _continuation.Observe(state, transportAge);
        if (_state?.Display?.InstanceId != state.Display?.InstanceId || _state?.Window != state.Window || _state?.Version != state.Version)
        { DismissSeatingUpload(); _hideRequest = null; _surface.ReturnError(null); }
        if (!state.ClockReady || state.PolicyState != "APPLIED" || state.Phase is not ("Preparing" or "Active")) DismissSeatingUpload();
        _state = state; _surface.Apply(state, noise, transportAge, connection, calibration);
        _surface.ApplyPhotos(photos);
        UpdateHomework(state); if(!_manual)UpdateSeating(state);
    }
    private void OpenSeatingUpload()
    {
        if (_shutdown || _manual || !IsVisible) return;
        if (_seatingUpload is { } existing) { existing.Activate(); return; }
        var dialog=new StudySeatingUploadWindow(_pipe) { Owner=this, Topmost=true }; _seatingUpload=dialog;
        try { dialog.ShowDialog(); }
        finally { if (ReferenceEquals(_seatingUpload,dialog)) _seatingUpload=null; dialog.Dismiss(); }
    }
    private void DismissSeatingUpload() { var dialog=_seatingUpload; _seatingUpload=null; dialog?.Dismiss(); }
    public void HideDisplay() { DismissSeatingUpload(); Hide(); _surface.Suspend(); _homeworkRead?.Cancel(); _seatingRead?.Cancel(); }
    public void ShowDisplay()
    {
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        if (!IsVisible) Show();
    }
    public void Shutdown() { _shutdown = true; DismissSeatingUpload(); _homeworkRead?.Cancel(); _seatingRead?.Cancel(); _seatingKey = null; _surface.ClearSeating(); _surface.Stop(); Close(); }
    private void UpdateHomework(StudyHostState state)
    {
        if (state.Homework is not { State: "READY", SnapshotId: { } id, Chunks: > 0 and <= 128 } homework)
        { _homeworkRead?.Cancel(); _homeworkKey = null; _homeworkSnapshot = null; return; }
        string key = $"{state.Display?.InstanceId}/{homework.Version}/{homework.Date}/{id}";
        if (_homeworkRetryKey == key && System.Diagnostics.Stopwatch.GetElapsedTime(_homeworkRetryAt).TotalSeconds < 5) return;
        if (_homeworkKey == key && _homeworkSnapshot is { } cached) { _surface.ApplyHomework(id, cached); return; }
        if (_homeworkKey == key && _homeworkRead is { IsCancellationRequested: false }) return;
        _homeworkRead?.Cancel(); _homeworkKey = key; _homeworkSnapshot = null;
        _homeworkRead = new(TimeSpan.FromSeconds(30));
        _ = ReadHomeworkAsync(key, homework, _homeworkRead);
    }
    private async Task ReadHomeworkAsync(string key, StudyHomeworkState homework, CancellationTokenSource cancellation)
    {
        try
        {
            using var buffer = new MemoryStream();
            for (int index = 0; index < homework.Chunks; index++)
            {
                var request = new HostRequest(1, Guid.NewGuid(), "study.homework", StudyHomework: new(homework.SnapshotId!, index));
                var reply = await HostClient.RequestAsync(_pipe, request, cancellation.Token);
                if (reply.RequestId != request.RequestId || reply.Outcome != "Succeeded" || reply.HomeworkChunk is not { } chunk ||
                    chunk.SnapshotId != homework.SnapshotId || chunk.Index != index || chunk.Count != homework.Chunks) throw new InvalidDataException();
                var bytes = Convert.FromBase64String(chunk.Data);
                if (bytes.Length is <= 0 or > 16384 || index < homework.Chunks - 1 && bytes.Length != 16384 || buffer.Length + bytes.Length > 2097152)
                    throw new InvalidDataException();
                buffer.Write(bytes);
            }
            var body = buffer.ToArray();
            if (Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(body)) != homework.SnapshotId) throw new InvalidDataException();
            var snapshot = StudyHomeworkContract.Parse(body);
            if (snapshot.Version != homework.Version || snapshot.Date != homework.Date) throw new InvalidDataException();
            if (!_shutdown && !cancellation.IsCancellationRequested && _homeworkKey == key)
            { _homeworkSnapshot = snapshot; _surface.ApplyHomework(homework.SnapshotId!, snapshot); }
        }
        catch (Exception e) when (e is IOException or InvalidDataException or TimeoutException or OperationCanceledException or JsonException or UnauthorizedAccessException or FormatException)
        { if (_homeworkKey == key && ReferenceEquals(_homeworkRead, cancellation)) { _homeworkKey = null; _homeworkRetryKey = key; _homeworkRetryAt = System.Diagnostics.Stopwatch.GetTimestamp(); } }
        finally { if (ReferenceEquals(_homeworkRead, cancellation)) _homeworkRead = null; cancellation.Dispose(); }
    }
    private async Task<bool> AuthorizeReminderAsync(string phase, StudyHostState state)
    {
        if (state is not { Display: { } display, Window: { } window, Version: { } version }) return false;
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(1));
            var reply = await HostClient.RequestAsync(_pipe, new HostRequest(1, Guid.NewGuid(), _manual?"manual.study.reminder":"study.reminder",
                TimeoutMs: 1000, StudyReminder: new(display.InstanceId, version, window, phase,
                    phase == "Discipline" ? state.Threshold?.DisciplineId : null,
                    phase == "Discipline" ? state.Threshold?.SessionId : null)), timeout.Token);
            return reply.Outcome == "Succeeded" && !_shutdown && IsVisible && WindowState != WindowState.Minimized && _surface.IsVisible;
        }
        catch (Exception e) when (e is IOException or InvalidDataException or TimeoutException or OperationCanceledException or JsonException or UnauthorizedAccessException)
        { return false; } // Never replay an uncertain audio claim on another App instance.
    }
    private async Task EndAsync()
    {
        if(_state?.Manual is not {SessionId:not null} current)return;
        bool Current() => !_shutdown && _state?.Manual is { State: "Starting" or "Active" or "Stopping" } shown &&
            shown.InstanceId == current.InstanceId && shown.SessionId == current.SessionId;
        try{using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(4));var reply=await HostClient.RequestAsync(_pipe,new HostRequest(1,Guid.NewGuid(),"manual.study.command",
            ManualStudy:new("end",current.InstanceId,current.Revision,current.SessionId)),timeout.Token);
            if(Current() && reply.Outcome!="Succeeded")_surface.ReturnError(reply.Message);}
        catch(Exception e)when(e is IOException or OperationCanceledException or InvalidDataException or JsonException or UnauthorizedAccessException or TimeoutException)
        { if(Current())_surface.ReturnError("结束请求待确认，请核对后台状态；未停止其他会话。"); }
    }
    private async Task HideAsync()
    {
        if (_state is not { Display: { } display, Window: { } window, Version: { } version }) return;
        var id = _hideRequest ??= Guid.NewGuid();
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var reply = await HostClient.RequestAsync(_pipe, new HostRequest(1, id, _manual?"manual.study.hide":"study.hide", StudyHide: new(display.InstanceId, version, window)), timeout.Token);
            if (_shutdown || _state?.Display?.InstanceId != display.InstanceId || _state.Window != window || _state.Version != version) return;
            if (reply.Outcome == "Succeeded" && reply.Study is { Display: { Available: false } confirmation } confirmed &&
                confirmation.InstanceId == display.InstanceId && confirmed.Version == version && confirmed.Window == window &&
                double.IsFinite(confirmation.HiddenRemainingSeconds) && confirmation.HiddenRemainingSeconds>0 &&
                confirmation.HiddenRemainingSeconds<=(_manual?10801:StudyIdleReturn.DefaultSeconds))
            { _surface.ReturnError(null); HideDisplay(); }
            else _surface.ReturnError("后台尚未确认收起期限，请重试；没有停止监测。");
            if (_hideRequest == id) _hideRequest = null;
        }
        catch (Exception e) when (e is IOException or InvalidDataException or TimeoutException or OperationCanceledException or JsonException or UnauthorizedAccessException)
        {
            // Retain request id: uncertain transport must not create a renewed hide lease.
            if (!_shutdown && _state?.Display?.InstanceId == display.InstanceId && _state.Window == window && _state.Version == version)
                _surface.ReturnError("后台连接待恢复，请重试；没有停止监测。");
        }
    }
}

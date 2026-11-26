using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using NPEduTools.Contracts;
using NPEduTools.Core;

namespace NPEduTools.App;

public partial class StudySeatingUploadWindow : Window
{
    private readonly string _pipe;
    private readonly CancellationTokenSource _lifetime = new();
    private StudySeatingUploadState? _scope;
    private StudySeatingUploadState? _target;
    private StudySeatingDraft? _draft;
    private StudySeatingUploadCommand? _command;
    private CancellationTokenSource? _scopeRead;
    private long _viewGeneration;
    private bool _wideLayout;
    private bool _bindingVerified;
    private bool _reloadAfterOperation;
    private bool _busy, _uncertain, _closed, _dismissed;
    internal Action? ReturnToSchool { get; init; }
    internal Action<bool>? WriteStateChanged { get; init; }
    internal bool IsWriting { get; private set; }
    private bool Embedded => ReturnToSchool is not null;

    public StudySeatingUploadWindow(string pipe)
    {
        _pipe = pipe;
        InitializeComponent();
        DesktopDialogSizing.Attach(this, pipe);
        Loaded += async (_, _) => { if (!Embedded) await RefreshAsync(); };
        Closing += (_, e) => { if ((_busy || _scopeRead is not null) && !_dismissed) e.Cancel = true; };
        Closed += (_, _) =>
        {
            _closed = true;
            _lifetime.Cancel();
            RemoveTransfer();
            ClearDraft();
            _lifetime.Dispose();
        };
    }

    public void Dismiss()
    {
        if (_closed) return;
        _dismissed = true;
        _lifetime.Cancel();
        Close();
    }

    private void RemoveTransfer()
    {
        if (_command is { } command) StudySeatingTransfer.Delete(_pipe, command.UploadId);
        _command = null;
    }

    private void SeatingLayoutChanged(object sender, SizeChangedEventArgs args)
    {
        const double minimumContentWidth = 750;
        const double minimumWorkspaceWidth = 930;
        bool wide = args.NewSize.Width >= minimumContentWidth &&
            (!Embedded || WorkspacePanel.DialogOwner(this).ActualWidth >= minimumWorkspaceWidth);
        if (wide == _wideLayout) return;
        _wideLayout = wide;
        SeatingLayout.ColumnDefinitions[1].Width = wide ? new GridLength(356) : GridLength.Auto;
        Grid.SetRow(SeatingPreviewPane, wide ? 0 : 1);
        Grid.SetRowSpan(SeatingPreviewPane, wide ? 3 : 1);
        Grid.SetColumn(SeatingScopePane, wide ? 1 : 0);
        Grid.SetColumn(SeatingActionsPane, wide ? 1 : 0);
        Grid.SetRow(SeatingActionsPane, wide ? 1 : 2);
        SeatingPreviewPane.Margin = new Thickness(0, 0, wide ? 12 : 0, 10);
        PreviewFrame.Height = wide ? 288 : 152;
    }

    private void UpdateButtons()
    {
        bool idle = !_closed && !_busy && _scopeRead is null;
        ChooseImage.IsEnabled = idle && !_uncertain && (!Embedded || _bindingVerified);
        ChooseImage.Content = _draft is null ? "选择图片" : "更换图片";
        if (Embedded)
            ChooseImage.SetResourceReference(StyleProperty, ChooseImage.IsEnabled && _draft is null &&
                FinishButton.Visibility != Visibility.Visible ? "TeacherPrimary" : "TeacherButton");
        RefreshScope.IsEnabled = idle && (!_uncertain || Embedded && !_bindingVerified);
        SaveImage.IsEnabled = idle && _draft is not null && _scope is not null && (!Embedded || _bindingVerified);
        SaveImage.Content = _uncertain ? "重试确认" : _scope?.Revision > 0 ? $"确认覆盖版本 {_scope.Revision}" : "确认上传";
    }

    private void ShowFeedback(string heading, string detail, string brush = "TeacherTextBrush")
    {
        ResultHeading.SetResourceReference(TextBlock.ForegroundProperty, brush);
        ResultHeading.Text = heading;
        ResultText.Text = detail;
    }

    private void ShowCompletion(bool saved)
    {
        FinishButton.Visibility = saved ? Visibility.Visible : Visibility.Collapsed;
        SavedMark.Visibility = FinishButton.Visibility;
        SaveImage.Visibility = saved ? Visibility.Collapsed : Visibility.Visible;
        EmptyDraft.Text = saved ? "可以选择下一张座位表" : "尚未选择座位表图片";
    }

    private void FinishClicked(object sender, RoutedEventArgs e)
    {
        if (ReturnToSchool is { } back) back();
        else Close();
    }

    internal void WorkspaceVisibilityChanged(bool visible)
    {
        if (_closed) return;
        if (!visible)
        {
            _viewGeneration++;
            var read = _scopeRead;
            _scopeRead = null;
            read?.Cancel();
            _bindingVerified = false;
            ShowDraft(false);
            UpdateButtons();
            return;
        }
        if (_busy) _reloadAfterOperation = true;
        else _ = RefreshAsync();
    }

    private void ShowDraft(bool show)
    {
        Preview.Source = show ? _draft?.Preview : null;
        bool visible = Preview.Source is not null;
        EmptyDraft.Visibility = visible ? Visibility.Collapsed : Visibility.Visible;
        DraftInfo.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ClearDraft()
    {
        _draft = null;
        DraftInfo.Text = "";
        ShowDraft(false);
    }

    private async Task<HostResponse> SendAsync(string capability, StudySeatingUploadCommand? command = null,
        CancellationToken readToken = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token, readToken);
        timeout.CancelAfter(16500);
        var request = new HostRequest(1, Guid.NewGuid(), capability, TimeoutMs: 15000, SeatingUpload: command);
        var result = await HostClient.RequestAsync(_pipe, request, timeout.Token);
        if (result.RequestId != request.RequestId || result.Version != 1) throw new InvalidDataException();
        return result;
    }

    private static bool TransportFailure(Exception e) =>
        e is IOException or UnauthorizedAccessException or OperationCanceledException or TimeoutException or JsonException or InvalidDataException;

    private async Task RefreshAsync()
    {
        if (_busy || _scopeRead is not null || _closed || (_uncertain && !Embedded)) return;
        using var read = new CancellationTokenSource();
        _scopeRead = read;
        bool completed = Embedded && FinishButton.Visibility == Visibility.Visible;
        if (!_uncertain) _scope = null;
        ShowCompletion(false);
        _bindingVerified = false;
        if (Embedded) ShowDraft(false);
        ScopeText.Text = "正在核对本班当前版本…";
        ShowFeedback("正在核对", "正在读取本机绑定的班级和座位表版本。");
        UpdateButtons();
        try
        {
            var response = await SendAsync(Embedded ? "study.seating.upload.status.scoped" : "study.seating.upload.status", readToken: read.Token);
            if (_closed || !ReferenceEquals(_scopeRead, read)) return;
            if (response.Outcome != "Succeeded" || response.SeatingUpload is not { State: "Ready", Revision: >= 0 } state ||
                state.TicketId == Guid.Empty || string.IsNullOrWhiteSpace(state.ClassId) || string.IsNullOrWhiteSpace(state.Version) ||
                Embedded && string.IsNullOrWhiteSpace(state.BindingStamp))
            {
                ShowScopeFailure(Message(response.ErrorCode));
                return;
            }
            bool changed = Embedded && _target is { } target && !SameTarget(target, state);
            bool retiredUpload = changed && _uncertain;
            bool retiredDraft = changed && _draft is not null;
            if (changed)
            {
                ClearDraft();
                _uncertain = false;
                RemoveTransfer();
            }
            _bindingVerified = true;
            if (_uncertain)
            {
                ShowDraft(true);
                ShowPending();
                return;
            }
            if (completed && !changed && _target?.Revision == state.Revision && _target.Version == state.Version)
            {
                ShowSaved(state);
                return;
            }
            _target = state;
            _scope = state;
            ScopeText.Text = $"{state.ClassName} · 当前版本 {state.Revision}";
            ShowDraft(true);
            if (changed)
                ShowFeedback("上传目标已变更", (retiredUpload
                    ? "已撤下原目标的待确认操作。"
                    : retiredDraft ? "已撤下原目标的图片草稿。" : "") + "请为当前班级选择图片。", "TeacherWarningBrush");
            else if (_draft is null)
                ShowFeedback("选择座位表图片", "选择 PNG 或 JPEG 文件，预览后可确认上传。");
            else
                ShowFeedback("草稿待确认", "已核对当前版本，请核对图片后确认上传。");
        }
        catch (Exception e) when (TransportFailure(e))
        {
            if (!_closed && ReferenceEquals(_scopeRead, read))
                ShowScopeFailure("请检查学校互联连接，然后点击“核对当前版本”。");
        }
        finally
        {
            if (ReferenceEquals(_scopeRead, read))
            {
                _scopeRead = null;
                UpdateButtons();
            }
        }
    }

    private static bool SameTarget(StudySeatingUploadState left, StudySeatingUploadState right) =>
        left.ClassId == right.ClassId && left.BindingStamp == right.BindingStamp;

    private void ShowScopeFailure(string detail)
    {
        ScopeText.Text = "当前班级及版本未确认";
        if (_uncertain)
        {
            ShowFeedback("上传结果待确认", "原申请已保留。核对当前绑定后才能重试确认。" + detail, "TeacherWarningBrush");
            return;
        }
        ShowFeedback("无法核对当前版本", detail, "TeacherWarningBrush");
    }

    private async void ChooseClicked(object sender, RoutedEventArgs e)
    {
        var picker = new OpenFileDialog { Title = "选择自习座位表", Filter = "座位表图片|*.png;*.jpg;*.jpeg", CheckFileExists = true };
        if (picker.ShowDialog(WorkspacePanel.DialogOwner(this)) == true) await LoadDraftAsync(picker.FileName);
    }

    internal async Task LoadDraftAsync(string path)
    {
        if (_busy || _scopeRead is not null || _uncertain || _closed || (Embedded && !_bindingVerified)) return;
        long generation = _viewGeneration;
        _busy = true;
        ShowCompletion(false);
        ShowFeedback("正在读取图片", "正在为所选文件生成预览。");
        UpdateButtons();
        try
        {
            var draft = await StudySeatingImage.LoadAsync(path);
            if (_closed || generation != _viewGeneration) return;
            RemoveTransfer();
            _draft = draft;
            DraftInfo.Text = $"{draft.Width} × {draft.Height} · 上传为 PNG · {draft.Png.Length / 1024.0:F0} KiB　{Path.GetFileName(path)}";
            ShowDraft(true);
            if (_scope is null) ScopeText.Text = "当前班级及版本未确认";
            ShowFeedback("草稿待确认", _scope is null
                ? "请核对图片，再点击“核对当前版本”确认上传到哪个班级。"
                : "请核对图片和班级，确认后才会上传。");
        }
        catch (Exception e) when (TransportFailure(e) || e is NotSupportedException or ArgumentException or FileFormatException)
        {
            if (_closed || generation != _viewGeneration) return;
            string detail = e is InvalidDataException ? e.Message : "无法读取图片，请选择完整的 PNG 或 JPEG 文件。";
            if (_draft is not null) detail += " 当前预览仍是上一张图片。";
            ShowFeedback("图片未载入", detail, "TeacherErrorBrush");
        }
        finally { OperationFinished(); }
    }

    private async void RefreshClicked(object sender, RoutedEventArgs e) => await RefreshAsync();

    private async void SaveClicked(object sender, RoutedEventArgs args)
    {
        if (_closed || _busy || _scopeRead is not null || _scope is not { } scope || _draft is not { } draft ||
            (Embedded && !_bindingVerified)) return;
        _busy = true;
        IsWriting = true;
        WriteStateChanged?.Invoke(true);
        UpdateButtons();
        if (_uncertain) ShowFeedback("正在确认上传结果", "正在向服务器核对同一次上传。");
        else ShowFeedback("正在上传", $"正在提交到{scope.ClassName}，请等待服务器确认。");

        bool sent = false;
        try
        {
            if (_command is null)
            {
                var id = Guid.NewGuid();
                StudySeatingTransfer.Write(_pipe, id, draft.Png);
                _command = new(scope.TicketId, id, scope.Version, scope.Revision, draft.Hash, draft.Png.Length, draft.Width, draft.Height);
            }
            sent = true;
            var reply = await SendAsync(Embedded ? "study.seating.upload.scoped" : "study.seating.upload", _command);
            if (_closed) return;
            if (reply.Outcome == "Succeeded" && reply.SeatingUpload is { State: "Saved" } saved &&
                saved.TicketId == scope.TicketId && SameTarget(saved, scope) && saved.Version == scope.Version &&
                saved.Hash == draft.Hash && saved.Revision == scope.Revision + 1)
            {
                _uncertain = false;
                RemoveTransfer();
                _scope = null;
                _target = saved;
                ClearDraft();
                ShowSaved(saved);
            }
            else if (reply.ErrorCode is "SEATING_CHANGED" or "POLICY_CHANGED" or "UPLOAD_EXPIRED" or "STUDY_NOT_APPLIED" or
                     "SESSION_SUPERSEDED" or "BINDING_CHANGED" or "AUTH_INVALID" or "INVALID_IMAGE" or "IMAGE_DIMENSIONS" or
                     "IDEMPOTENCY_CONFLICT" or "UnknownCapability" or "SEATING_UPLOAD_UNSUPPORTED" or "STUDY_SEATING_UNSUPPORTED")
            {
                // A refusal on retry only describes this attempt. The first one may have been saved.
                if (_uncertain)
                    ShowFeedback("上次上传结果未确认", "草稿已保留。请先核对本班当前版本，再决定是否重新上传。", "TeacherWarningBrush");
                else
                    ShowFeedback("上传失败", Message(reply.ErrorCode) + " 草稿已保留，请核对当前版本后再确认。", "TeacherErrorBrush");
                _uncertain = false;
                RemoveTransfer();
                _scope = null;
                if (Embedded && reply.ErrorCode is "BINDING_CHANGED" or "SESSION_SUPERSEDED" or "AUTH_INVALID")
                {
                    _bindingVerified = false;
                    ShowDraft(false);
                }
                ScopeText.Text = "当前班级及版本未确认";
            }
            else ShowPending();
        }
        catch (Exception e) when (TransportFailure(e))
        {
            if (_closed) return;
            if (sent) ShowPending();
            else
            {
                RemoveTransfer();
                ShowFeedback("上传失败", "未能准备本机图片。草稿已保留，可以重试上传。", "TeacherErrorBrush");
            }
        }
        finally
        {
            IsWriting = false;
            WriteStateChanged?.Invoke(false);
            OperationFinished();
        }
    }

    private void OperationFinished()
    {
        _busy = false;
        UpdateButtons();
        if (_reloadAfterOperation)
        {
            _reloadAfterOperation = false;
            if (WorkspacePanel.Visible(this)) _ = RefreshAsync();
        }
    }

    private void ShowSaved(StudySeatingUploadState saved)
    {
        ShowCompletion(true);
        ScopeText.Text = $"{saved.ClassName} · 已保存版本 {saved.Revision}";
        ShowFeedback("已保存到本班", "服务器已保存。自习展示下载新图后即可查看；正在查看的旧图会在关闭后切换。", "TeacherSuccessBrush");
    }

    private void ShowPending()
    {
        _uncertain = true;
        if (_scope is { } scope) ScopeText.Text = $"{scope.ClassName} · 提交时版本 {scope.Revision}";
        ShowFeedback("等待确认", "尚未收到保存确认。点击“重试确认”继续同一次上传。", "TeacherWarningBrush");
    }

    private static string Message(string? code) => code switch
    {
        "SEATING_CHANGED" => "座位表已被另一端更换。",
        "POLICY_CHANGED" or "BINDING_CHANGED" or "SESSION_SUPERSEDED" or "AUTH_INVALID" => "班级绑定或学校安排已变化。",
        "UPLOAD_EXPIRED" => "本次版本核对已过期。",
        "UnknownCapability" or "SEATING_UPLOAD_UNSUPPORTED" or "STUDY_SEATING_UNSUPPORTED" => "当前后台或服务器尚不支持大屏上传，请更新后重试。",
        "INVALID_IMAGE" or "IMAGE_DIMENSIONS" => "图片未通过校验。",
        "IDEMPOTENCY_CONFLICT" => "本次上传与原提交内容不符。",
        _ => "请检查学校互联连接，并确认本班自习设置已应用。"
    };
}

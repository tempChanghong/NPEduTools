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
    private StudySeatingDraft? _draft;
    private StudySeatingUploadCommand? _command;
    private bool _busy, _uncertain, _closed, _dismissed;

    public StudySeatingUploadWindow(string pipe)
    {
        _pipe = pipe;
        InitializeComponent();
        DesktopDialogSizing.Attach(this, pipe);
        Loaded += async (_, _) => await RefreshAsync();
        Closing += (_, e) => { if (_busy && !_dismissed) e.Cancel = true; };
        Closed += (_, _) =>
        {
            _closed = true;
            _lifetime.Cancel();
            RemoveTransfer();
            Preview.Source = null;
            _draft = null;
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

    private void UpdateButtons()
    {
        ChooseImage.IsEnabled = !_busy && !_uncertain;
        RefreshScope.IsEnabled = !_busy && !_uncertain;
        SaveImage.IsEnabled = !_busy && _draft is not null && _scope is not null;
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
        SaveImage.Visibility = saved ? Visibility.Collapsed : Visibility.Visible;
        EmptyDraft.Text = saved ? "可以关闭窗口，或选择下一张座位表" : "尚未选择座位表图片";
    }

    private void FinishClicked(object sender, RoutedEventArgs e) => Close();

    private async Task<HostResponse> SendAsync(string capability, StudySeatingUploadCommand? command = null)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
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
        if (_busy || _uncertain || _closed) return;
        _busy = true;
        _scope = null;
        ShowCompletion(false);
        ScopeText.Text = "正在核对本班当前版本…";
        ShowFeedback("正在核对", "正在读取本机绑定的班级和座位表版本。");
        UpdateButtons();
        try
        {
            var response = await SendAsync("study.seating.upload.status");
            if (_closed) return;
            if (response.Outcome != "Succeeded" || response.SeatingUpload is not { State: "Ready" } state)
            {
                ShowScopeFailure(Message(response.ErrorCode));
                return;
            }
            _scope = state;
            ScopeText.Text = $"{state.ClassName} · 当前版本 {state.Revision}";
            if (_draft is null)
                ShowFeedback("选择座位表图片", "选择 PNG 或 JPEG 文件，预览后可确认上传。");
            else
                ShowFeedback("草稿待确认", "已核对当前版本，请核对图片后确认上传。");
        }
        catch (Exception e) when (TransportFailure(e))
        {
            if (!_closed) ShowScopeFailure("请检查学校互联连接，然后点击“核对当前版本”。");
        }
        finally { _busy = false; UpdateButtons(); }
    }

    private void ShowScopeFailure(string detail)
    {
        ScopeText.Text = "当前班级及版本未确认";
        ShowFeedback("无法核对当前版本", detail, "TeacherWarningBrush");
    }

    private async void ChooseClicked(object sender, RoutedEventArgs e)
    {
        var picker = new OpenFileDialog { Title = "选择自习座位表", Filter = "座位表图片|*.png;*.jpg;*.jpeg", CheckFileExists = true };
        if (picker.ShowDialog(this) == true) await LoadDraftAsync(picker.FileName);
    }

    internal async Task LoadDraftAsync(string path)
    {
        if (_busy || _uncertain || _closed) return;
        _busy = true;
        ShowCompletion(false);
        ShowFeedback("正在读取图片", "正在为所选文件生成预览。");
        UpdateButtons();
        try
        {
            var draft = await StudySeatingImage.LoadAsync(path);
            if (_closed) return;
            RemoveTransfer();
            _draft = draft;
            Preview.Source = draft.Preview;
            EmptyDraft.Visibility = Visibility.Collapsed;
            DraftInfo.Text = $"{draft.Width} × {draft.Height} · 上传为 PNG · {draft.Png.Length / 1024.0:F0} KiB　{Path.GetFileName(path)}";
            DraftInfo.Visibility = Visibility.Visible;
            if (_scope is null) ScopeText.Text = "当前班级及版本未确认";
            ShowFeedback("草稿待确认", _scope is null
                ? "请核对图片，再点击“核对当前版本”确认上传到哪个班级。"
                : "请核对图片和班级，确认后才会上传。");
        }
        catch (Exception e) when (TransportFailure(e) || e is NotSupportedException or ArgumentException or FileFormatException)
        {
            if (_closed) return;
            string detail = e is InvalidDataException ? e.Message : "无法读取图片，请选择完整的 PNG 或 JPEG 文件。";
            if (_draft is not null) detail += " 当前预览仍是上一张图片。";
            ShowFeedback("图片未载入", detail, "TeacherErrorBrush");
        }
        finally { _busy = false; UpdateButtons(); }
    }

    private async void RefreshClicked(object sender, RoutedEventArgs e) => await RefreshAsync();

    private async void SaveClicked(object sender, RoutedEventArgs args)
    {
        if (_closed || _busy || _scope is not { } scope || _draft is not { } draft) return;
        _busy = true;
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
            var reply = await SendAsync("study.seating.upload", _command);
            if (_closed) return;
            if (reply.Outcome == "Succeeded" && reply.SeatingUpload is { State: "Saved" } saved &&
                saved.TicketId == scope.TicketId && saved.ClassId == scope.ClassId && saved.Version == scope.Version &&
                saved.Hash == draft.Hash && saved.Revision == scope.Revision + 1)
            {
                _uncertain = false;
                RemoveTransfer();
                _scope = null;
                _draft = null;
                Preview.Source = null;
                ShowCompletion(true);
                EmptyDraft.Visibility = Visibility.Visible;
                DraftInfo.Text = "";
                DraftInfo.Visibility = Visibility.Collapsed;
                ScopeText.Text = $"{saved.ClassName} · 已保存版本 {saved.Revision}";
                ShowFeedback("已保存到本班", "服务器已保存。自习展示下载新图后即可查看；正在查看的旧图会在关闭后切换。", "TeacherSuccessBrush");
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
        finally { _busy = false; UpdateButtons(); }
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

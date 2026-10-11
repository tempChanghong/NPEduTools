using System.IO;
using System.Text.Json;
using System.Windows;
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
        _pipe=pipe; InitializeComponent();
        Loaded += async (_,_) => await RefreshAsync();
        Closing += (_,e) => { if (_busy && !_dismissed) e.Cancel=true; };
        Closed += (_,_) => { _closed=true; _lifetime.Cancel(); RemoveTransfer(); Preview.Source=null; _draft=null; _lifetime.Dispose(); };
    }
    public void Dismiss()
    {
        if (_closed) return;
        _dismissed=true; _lifetime.Cancel(); Close();
    }
    private void RemoveTransfer()
    { if (_command is {} command) StudySeatingTransfer.Delete(_pipe,command.UploadId); _command=null; }
    private void Buttons()
    {
        ChooseImage.IsEnabled=!_busy && !_uncertain;
        RefreshScope.IsEnabled=!_busy && !_uncertain;
        SaveImage.IsEnabled=!_busy && _draft is not null && _scope is not null;
        SaveImage.Content=_uncertain ? "重试确认上传结果" : _scope?.Revision > 0 ? $"确认覆盖版本 {_scope.Revision}" : "确认上传";
    }
    private async Task<HostResponse> SendAsync(string capability, StudySeatingUploadCommand? command=null)
    {
        using var timeout=CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token); timeout.CancelAfter(16500);
        var request=new HostRequest(1,Guid.NewGuid(),capability,TimeoutMs:15000,SeatingUpload:command);
        var result=await HostClient.RequestAsync(_pipe,request,timeout.Token);
        if (result.RequestId!=request.RequestId || result.Version!=1) throw new InvalidDataException();
        return result;
    }
    private static bool TransportFailure(Exception e) => e is IOException or UnauthorizedAccessException or OperationCanceledException or TimeoutException or JsonException or InvalidDataException;
    private async Task RefreshAsync()
    {
        if (_busy || _uncertain || _closed) return;
        _busy=true; _scope=null; Buttons();
        try {
            var response=await SendAsync("study.seating.upload.status");
            if(response.Outcome!="Succeeded" || response.SeatingUpload is not {State:"Ready"} state) { ResultText.Text=Message(response.ErrorCode); return; }
            _scope=state; ScopeText.Text=$"{state.ClassName} · 当前版本 {state.Revision}";
            ResultText.Text=_draft is null ? "选择图片并核对预览后确认上传。" : "已核对当前版本，请再次确认后上传草稿。";
        } catch(Exception e) when(TransportFailure(e)) { ResultText.Text="暂时无法核对本班版本，请恢复互联后重试。"; }
        finally { _busy=false; Buttons(); }
    }
    private async void ChooseClicked(object sender,RoutedEventArgs e)
    {
        var picker=new OpenFileDialog {Title="选择自习座位表",Filter="座位表图片|*.png;*.jpg;*.jpeg",CheckFileExists=true};
        if(picker.ShowDialog(this)!=true) return;
        await LoadDraftAsync(picker.FileName);
    }
    internal async Task LoadDraftAsync(string path)
    {
        if(_busy || _uncertain || _closed) return;
        _busy=true; Buttons();
        try {
            var draft=await StudySeatingImage.LoadAsync(path);
            if(_closed) return;
            RemoveTransfer(); _draft=draft; Preview.Source=draft.Preview; EmptyDraft.Visibility=Visibility.Collapsed;
            DraftInfo.Text=$"{draft.Width} × {draft.Height} · PNG · {draft.Png.Length/1024.0:F0} KiB";
            ResultText.Text="请核对图片；确认后才会上传并替换服务器版本。";
        } catch(Exception e) when(TransportFailure(e) || e is NotSupportedException or ArgumentException)
        { ResultText.Text=e is InvalidDataException ? e.Message : "无法读取图片，请选择完整的 PNG 或 JPEG 文件。"; }
        finally { _busy=false; Buttons(); }
    }
    private async void RefreshClicked(object sender,RoutedEventArgs e) => await RefreshAsync();
    private async void SaveClicked(object sender,RoutedEventArgs args)
    {
        if(_busy || _scope is not {} scope || _draft is not {} draft) return;
        _busy=true; Buttons();
        bool sent=false;
        try {
            if(_command is null) {
                var id=Guid.NewGuid();
                StudySeatingTransfer.Write(_pipe,id,draft.Png);
                _command=new(scope.TicketId,id,scope.Version,scope.Revision,draft.Hash,draft.Png.Length,draft.Width,draft.Height);
            }
            sent=true;
            var reply=await SendAsync("study.seating.upload",_command);
            if(reply.Outcome=="Succeeded" && reply.SeatingUpload is {State:"Saved"} saved && saved.ClassId==scope.ClassId && saved.Version==scope.Version && saved.Hash==draft.Hash && saved.Revision==scope.Revision+1) {
                _uncertain=false; RemoveTransfer(); _scope=null; _draft=null; Preview.Source=null; EmptyDraft.Visibility=Visibility.Visible; DraftInfo.Text="";
                ScopeText.Text=$"{saved.ClassName} · 已保存版本 {saved.Revision}";
                ResultText.Text="服务器已保存。全屏展示下载新图后可按“自习座位表”查看；正在查看的旧图会在关闭后切换。";
            } else if(reply.ErrorCode is "SEATING_CHANGED" or "POLICY_CHANGED" or "UPLOAD_EXPIRED" or "STUDY_NOT_APPLIED" or "SESSION_SUPERSEDED" or "BINDING_CHANGED" or "AUTH_INVALID" or "INVALID_IMAGE" or "IMAGE_DIMENSIONS" or "IDEMPOTENCY_CONFLICT" or "SEATING_UPLOAD_UNSUPPORTED" or "STUDY_SEATING_UNSUPPORTED") {
                _uncertain=false; RemoveTransfer(); _scope=null; ResultText.Text=Message(reply.ErrorCode)+" 草稿已保留，请核对当前班级和版本后再确认。";
            } else { _uncertain=true; ResultText.Text="上传结果待确认，请重试同一次上传。确认前无法选择新图或更换版本。"; }
        } catch(Exception e) when(TransportFailure(e)) {
            _uncertain=sent; ResultText.Text=sent ? "连接中断，服务器可能已保存。请重试确认同一次上传，不会重复创建版本。" : "无法准备本机图片，未发送上传请求。";
            if(!sent) RemoveTransfer();
        } finally { _busy=false; Buttons(); }
    }
    private static string Message(string? code) => code switch {
        "SEATING_CHANGED"=>"座位表已被另一端更换，未覆盖新版本。",
        "POLICY_CHANGED" or "BINDING_CHANGED" or "SESSION_SUPERSEDED" or "AUTH_INVALID"=>"班级绑定或学校安排已变化，未确认上传成功。",
        "UPLOAD_EXPIRED"=>"本次版本核对已过期。",
        "UnknownCapability" or "SEATING_UPLOAD_UNSUPPORTED" or "STUDY_SEATING_UNSUPPORTED"=>"当前后台或服务器尚不支持大屏上传，请更新后重试。",
        "INVALID_IMAGE" or "IMAGE_DIMENSIONS"=>"图片未通过校验。",
        _=>"请先保持学校互联在线，并确认本班自习设置已应用。"
    };
}

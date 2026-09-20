using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using NPEduTools.Contracts;

namespace NPEduTools.App;

public partial class MainWindow
{
    private NpepState? _npepState;
    private bool _npepSending, _npepReading;
    private string? _npepServerSeen, _npepApprovalSeen;
    private static string NpepText(JsonObject? value, string key) => value?[key]?.GetValue<string>() ?? "";
    private async Task NpepPollAsync()
    {
        while (!_lifetime.IsCancellationRequested)
        {
            if (IsVisible && SettingsPage.Visibility == Visibility.Visible && NpepSettingsPanel.Visibility == Visibility.Visible) await RefreshNpepAsync();
            try { await Task.Delay(1000, _lifetime.Token); } catch (OperationCanceledException) { break; }
        }
    }
    private async Task RefreshNpepAsync()
    {
        if (_npepReading || _npepSending || _lifetime.IsCancellationRequested) return;
        _npepReading = true;
        try
        {
            var reply = await ManagementRequestAsync(new(Protocol.Version, Guid.NewGuid(), "npep.status"));
            if (!_npepSending) ApplyNpep(reply.Npep);
            if (reply.Npep is null) NpepMessage.Text = reply.Message;
        }
        catch (Exception e) when (IsManagementError(e)) { ApplyNpep(null); }
        finally { _npepReading = false; }
    }
    private void ApplyNpep(NpepState? state)
    {
        _npepState = state;
        string? server = state?.Server?.ToJsonString();
        string? approval = state?.Approval?.ToJsonString();
        if (server != _npepServerSeen) { _npepServerSeen = server; NpepServerConfirmed.IsChecked = false; }
        if (approval != _npepApprovalSeen) { _npepApprovalSeen = approval; NpepBindingConfirmed.IsChecked = false; }
        NpepStatusTitle.Text = state is null ? "后台未连接" : state.Error == "PAIRING_EXPIRED" ? "配对码已过期" : state.State switch
        {
            "UNPAIRED" => "尚未配对", "CREATING" => "配对申请待恢复", "PENDING" => "等待管理员批准", "APPROVED" => "请在本机确认连接",
            "CONFIRMING" => "正在确认配对结果", "ACTIVE" => state.ReportingPaused ? "已暂停上报" : state.Connection == "ONLINE" ? "已连接学校服务" : state.Connection == "STOPPED" ? "已配对 · 连接已停止" : "已配对 · 等待连接",
            "SUSPENDED" => "连接已停用", "UNPAIRING" => "解绑尚未完成", _ => "互联暂不可用"
        };
        NpepMessage.Text = state?.Message ?? "无法读取后台状态。连接恢复后会自动刷新，此处不把旧状态显示为在线。";
        NpepError.Text = state?.Error is { } code ? "错误：" + code : "";
        NpepReceipt.Text = DateTimeOffset.TryParse(state?.LastReceivedAt, out var received) ? "学校服务最近接收：" + received.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss") + "（本机时区）" : "";
        NpepCreatePanel.Visibility = state?.State == "UNPAIRED" ? Visibility.Visible : Visibility.Collapsed;
        NpepServerPanel.Visibility = state?.Server is not null ? Visibility.Visible : Visibility.Collapsed;
        NpepServerIdentity.Text = state?.Server is null ? "" : $"地址：{state.Origin}\n实例：{NpepText(state.Server, "serverInstanceId")}\n部署标识：{NpepText(state.Server, "deploymentEpoch")}";
        NpepCodePanel.Visibility = state?.State is "PENDING" or "APPROVED" ? Visibility.Visible : Visibility.Collapsed;
        NpepCode.Text = NpepText(state?.Pairing, "userCode");
        NpepExpiry.Text = "配对截止（学校服务器 UTC）：" + NpepText(state?.Pairing, "expiresAt");
        NpepBindingPanel.Visibility = state?.Approval is not null ? Visibility.Visible : Visibility.Collapsed;
        NpepBinding.Text = $"学校：{NpepText(state?.Approval, "schoolName")}\n班级：{NpepText(state?.Approval, "administrativeClassName")}\n大屏：{NpepText(state?.Approval, "screenBindingName")}\n共享内容：只读设备状态";
        NpepBoundOrigin.Text = state?.Origin;
        NpepBindingIdentity.Text = $"学校：{NpepText(state?.Approval, "schoolId")}\n班级：{NpepText(state?.Approval, "administrativeClassId")}\n大屏：{NpepText(state?.Approval, "screenBindingId")}\n批准编号：{NpepText(state?.Approval, "approvalId")}";
        NpepConfirmPanel.Visibility = state?.State == "APPROVED" ? Visibility.Visible : Visibility.Collapsed;
        NpepRecoverButton.Visibility = state?.State is "CREATING" or "CONFIRMING" || state?.State is "PENDING" or "ACTIVE" && state.Connection == "STOPPED" ? Visibility.Visible : Visibility.Collapsed;
        NpepRecoverButton.Content = state?.State == "ACTIVE" ? "重新连接" : "恢复未完成操作";
        NpepPauseButton.Visibility = state?.State == "ACTIVE" ? Visibility.Visible : Visibility.Collapsed;
        NpepPauseButton.Content = state?.ReportingPaused == true ? "恢复上报" : "暂停上报";
        NpepUnpairButton.Visibility = state is not null && state.State is not ("UNPAIRED" or "STORE_UNAVAILABLE") ? Visibility.Visible : Visibility.Collapsed;
        NpepUnpairButton.Content = state?.State is "PENDING" or "APPROVED" or "CREATING" ? "取消配对" : "解除绑定";
        UpdateNpepControls();
    }
    private void UpdateNpepControls()
    {
        if (NpepInspectButton is null || NpepPairButton is null) return;
        bool available = !_npepSending && _npepState is { Busy: false };
        NpepOriginBox.IsEnabled = available; NpepNameBox.IsEnabled = available;
        NpepInspectButton.IsEnabled = available && _npepState?.State == "UNPAIRED" && NpepContract.Valid(new("inspect", 0, NpepOriginBox.Text.Trim()));
        NpepPairButton.IsEnabled = available && _npepState?.State == "UNPAIRED" && NpepServerConfirmed.IsChecked == true &&
            _npepState.Server is not null && Uri.TryCreate(NpepOriginBox.Text.Trim(), UriKind.Absolute, out var origin) && origin.GetLeftPart(UriPartial.Authority) == _npepState.Origin &&
            NpepContract.Valid(new("pair", _npepState.Revision, NpepOriginBox.Text.Trim(), NpepNameBox.Text.Trim(), NpepText(_npepState.Server, "serverInstanceId"), NpepText(_npepState.Server, "deploymentEpoch")));
        NpepConfirmButton.IsEnabled = available && _npepState?.State == "APPROVED" && NpepBindingConfirmed.IsChecked == true;
        NpepPauseButton.IsEnabled = NpepRecoverButton.IsEnabled = NpepUnpairButton.IsEnabled = available;
    }
    private void NpepInputChanged(object sender, TextChangedEventArgs e)
    {
        if (ReferenceEquals(sender, NpepOriginBox) && NpepServerConfirmed is not null) NpepServerConfirmed.IsChecked = false;
        UpdateNpepControls();
    }
    private void NpepConsentChanged(object sender, RoutedEventArgs e) => UpdateNpepControls();
    private async Task SendNpepAsync(NpepCommand command)
    {
        if (_npepSending || _npepState?.Busy != false) return;
        _npepSending = true; UpdateNpepControls();
        try
        {
            var response = await ManagementRequestAsync(new(Protocol.Version, Guid.NewGuid(), "npep.command", Npep: command));
            ApplyNpep(response.Npep);
            if (response.Outcome == "Rejected") NpepMessage.Text = response.Message;
        }
        catch (Exception e) when (IsManagementError(e))
        { NpepMessage.Text = "未能收到后台确认。请先刷新状态，勿重复创建配对；后台可能已受理原操作。"; }
        finally { _npepSending = false; UpdateNpepControls(); }
    }
    private async void NpepRefreshClicked(object sender, RoutedEventArgs e) => await RefreshNpepAsync();
    private async void NpepInspectClicked(object sender, RoutedEventArgs e) => await SendNpepAsync(new("inspect", _npepState!.Revision, NpepOriginBox.Text.Trim()));
    private async void NpepPairClicked(object sender, RoutedEventArgs e)
    {
        if (_npepState?.Server is not { } info || NpepServerConfirmed.IsChecked != true) return;
        await SendNpepAsync(new("pair", _npepState.Revision, NpepOriginBox.Text.Trim(), NpepNameBox.Text.Trim(), NpepText(info, "serverInstanceId"), NpepText(info, "deploymentEpoch")));
    }
    private async void NpepConfirmClicked(object sender, RoutedEventArgs e)
    {
        if (_npepState?.Approval is not { } approval || NpepBindingConfirmed.IsChecked != true) return;
        await SendNpepAsync(new("confirm", _npepState.Revision, ApprovalId: NpepText(approval, "approvalId")));
    }
    private async void NpepPauseClicked(object sender, RoutedEventArgs e) => await SendNpepAsync(new(_npepState!.ReportingPaused ? "resume" : "pause", _npepState.Revision));
    private async void NpepRecoverClicked(object sender, RoutedEventArgs e) => await SendNpepAsync(new(_npepState!.State == "CREATING" ? "resume-create" : _npepState.State == "CONFIRMING" ? "recover" : _npepState.State == "ACTIVE" ? "resume" : "poll", _npepState.Revision));
    private async void NpepUnpairClicked(object sender, RoutedEventArgs e)
    {
        if (_npepState is null) return;
        if (MessageBox.Show(this, "将停止状态上报并删除本机配对凭据。若远端暂时无法连接，还需要学校管理员撤销登记。继续解除绑定吗？", "解除学校互联", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
            await SendNpepAsync(new("unpair", _npepState.Revision));
    }
}

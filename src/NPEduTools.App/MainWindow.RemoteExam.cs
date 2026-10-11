using System.Windows;
using NPEduTools.Contracts;

namespace NPEduTools.App;

public partial class MainWindow
{
    private RemoteExamStatus? _remoteExamState;
    private bool _remoteExamBusy, _remoteExamReading;
    private long _remoteExamUiGeneration;
    private long? _remoteExamInspectedRevision;
    private Guid? _remoteExamInspectedEpoch;

    private async Task RefreshRemoteExamAsync()
    {
        if (_remoteExamBusy || _remoteExamReading || _lifetime.IsCancellationRequested) return;
        _remoteExamReading = true;
        long generation = _remoteExamUiGeneration;
        try
        {
            var reply = await ManagementRequestAsync(new(Protocol.Version, Guid.NewGuid(), "remoteexam.status"));
            if (!_remoteExamBusy && generation == _remoteExamUiGeneration) ApplyRemoteExam(reply.RemoteExam);
        }
        catch (Exception e) when (IsManagementError(e))
        { if (!_remoteExamBusy && generation == _remoteExamUiGeneration) ApplyRemoteExam(null); }
        finally { _remoteExamReading = false; }
    }

    private void ApplyRemoteExam(RemoteExamStatus? state)
    {
        _remoteExamState = state;
        if (state is null || _remoteExamInspectedRevision != state.RuntimeRevision || _remoteExamInspectedEpoch != state.Policy.ControlEpoch)
        { _remoteExamInspectedRevision = null; _remoteExamInspectedEpoch = null; }
        RemoteExamBinding.Text = state?.Policy.Binding ?? "当前没有可授权的学校绑定。";
        ExamPlanPolicyMessage.Text = state?.PlanPolicy?.Message ?? "后台尚未提供考试方案通道。";
        RemoteExamPolicyMessage.Text = state?.Policy.Message ?? "后台未连接，无法读取本机许可。";
        RemoteExamRuntimeMessage.Text = RemoteExamPresentation.Runtime(state);
        RemoteExamRuntimeDetails.Text = RemoteExamPresentation.RuntimeDetails(state);
        RemoteExamRuntimeDetailsPanel.Visibility = state is null ? Visibility.Collapsed : Visibility.Visible;
        RemoteExamHistory.ShowHistory(state?.History);
        RemoteExamCheckButton.Content = state?.AutomaticPaused == true ? "核实当前考试状态" : "检查考试环境";
        UpdateRemoteExamControls();
    }

    private void ShowRemoteExamFeedback(RemoteExamFeedback feedback)
    {
        RemoteExamCheckMessage.Text = string.Join("\n", new[] { feedback.Title, feedback.Message, feedback.NextAction }
            .Where(text => text.Length > 0));
        RemoteExamCheckDetails.Text = feedback.Details;
        RemoteExamCheckDetailsPanel.IsExpanded = false;
        RemoteExamCheckDetailsPanel.Visibility = feedback.Details.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    private void UpdateRemoteExamControls()
    {
        var state = _remoteExamState;
        RemoteExamCheckButton.IsEnabled = !_remoteExamBusy;
        RemoteExamEndButton.IsEnabled = !_remoteExamBusy && state is { AutomaticPaused: true, StorageError: null, PauseOperationId: not null } &&
            _remoteExamInspectedRevision == state.RuntimeRevision && _remoteExamInspectedEpoch == state.Policy.ControlEpoch;
    }

    private async void RemoteExamEndClicked(object sender, RoutedEventArgs e)
    {
        if (!RemoteExamEndButton.IsEnabled || _remoteExamState is not { PauseOperationId: { } id } state) return;
        if (MessageBox.Show(this, "请先在“课堂模式”返回日常；若切换未完成，先恢复切换前设置，并处理现场软件。\n\n此操作只解除 N3 的自动录课暂停，不切换软件、不修改 Windows 自启动，也不清除历史记录。若原录课计划已启用且其他条件满足，当前课时可能开始录制。\n\n确认解除本次远程录课暂停？",
            "解除远程录课暂停", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        await SendRemoteExamAsync(new("end-local", state.RuntimeRevision, OperationId: id));
    }

    private async Task SendRemoteExamAsync(RemoteExamCommand command)
    {
        _remoteExamUiGeneration++;
        _remoteExamBusy = true; UpdateRemoteExamControls();
        try
        {
            var reply = await ManagementRequestAsync(new(Protocol.Version, Guid.NewGuid(), "remoteexam.command", RemoteExam: command));
            ApplyRemoteExam(reply.RemoteExam);
            ShowRemoteExamFeedback(RemoteExamPresentation.Feedback(reply, RemoteExamLocalAction.EndPause));
        }
        catch (Exception e) when (IsManagementError(e))
        {
            ApplyRemoteExam(null);
            ShowRemoteExamFeedback(new("操作结果待核实", "未收到后台确认，不能认定暂停已解除。",
                "等待状态刷新并核实结果；此处不会自动重发操作。", ""));
        }
        finally { _remoteExamBusy = false; UpdateRemoteExamControls(); }
    }

    private async void RemoteExamCheckClicked(object sender, RoutedEventArgs e)
    {
        if (!RemoteExamCheckButton.IsEnabled) return;
        _remoteExamUiGeneration++;
        _remoteExamBusy = true;
        _remoteExamInspectedRevision = null; _remoteExamInspectedEpoch = null;
        UpdateRemoteExamControls();
        bool inspecting = _remoteExamState?.AutomaticPaused == true;
        ShowRemoteExamFeedback(new(inspecting ? "正在核实当前考试状态…" : "正在检查本机考试环境…", "", "", ""));
        try
        {
            var response = await ManagementRequestAsync(new(Protocol.Version, Guid.NewGuid(),
                inspecting ? "remoteexam.inspect" : "remoteexam.preflight"));
            if (response.Outcome == "Succeeded" && response.RemoteExam is { } inspected)
            {
                _remoteExamInspectedRevision = inspected.RuntimeRevision;
                _remoteExamInspectedEpoch = inspected.Policy.ControlEpoch;
            }
            ApplyRemoteExam(response.RemoteExam);
            ShowRemoteExamFeedback(RemoteExamPresentation.Feedback(response,
                inspecting ? RemoteExamLocalAction.InspectCurrent : RemoteExamLocalAction.CheckEnvironment));
        }
        catch (Exception error) when (IsManagementError(error))
        {
            ApplyRemoteExam(null);
            ShowRemoteExamFeedback(new("检查结果未读取", "未能读取检查结果，不能认定检查通过。",
                "核对后台连接后重新检查；没有请求执行软件切换。", ""));
        }
        finally { _remoteExamBusy = false; UpdateRemoteExamControls(); }
    }
}

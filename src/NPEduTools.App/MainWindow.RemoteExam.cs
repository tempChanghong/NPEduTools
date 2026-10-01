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
        RemoteExamRuntimeMessage.Text = state is null ? "本地考试状态未知。" : state.StorageError is not null
            ? "考试记录不可用，自动录课保护保留。请先处理存储故障。" : state.AutomaticPaused
            ? $"N3 自动录课暂停中 · 操作 {state.PauseOperationId}"
            : "当前没有 N3 自动录课暂停。";
        RemoteExamHistoryText.Text = state?.History is { Length: > 0 } history
            ? string.Join("\n\n", history.Select(x => $"{x.UpdatedAt.ToLocalTime():MM-dd HH:mm:ss} · {(x.Target == "Daily" ? "返回日常" : "进入考试")} · {RemoteExamOutcome(x.Outcome)} · {x.Step}\n{x.OperationId}" +
                (x.Reason is null ? "" : $"\n原因：{x.Reason}") + (x.LocallyEndedAt is null ? "" : $"\n已由本机结束：{x.LocallyEndedAt.Value.ToLocalTime():MM-dd HH:mm:ss}")))
            : "暂无记录。";
        RemoteExamCheckButton.Content = state?.AutomaticPaused == true ? "核实当前考试状态" : "检查考试环境";
        UpdateRemoteExamControls();
    }

    private static string RemoteExamOutcome(string outcome) => outcome switch
    {
        "SUCCEEDED" => "切换成功", "REJECTED" => "未执行", "PARTIAL" => "切换未完成，可由学校重试补完",
        "UNKNOWN" => "结果未知，可由学校重新核查并切入", "CHECKING" => "检查中", "RUNNING" => "执行中",
        "WAITING_LOCAL" => "等待现场处理", "RECEIVED" => "已接收", _ => outcome
    };

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
            RemoteExamCheckMessage.Text = reply.Message + (reply.ErrorCode is { } code ? $"（{code}）" : "");
        }
        catch (Exception e) when (IsManagementError(e))
        {
            ApplyRemoteExam(null);
            RemoteExamCheckMessage.Text = "未收到确认，请等待状态刷新后核实结果；不会自动重发操作。";
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
        RemoteExamCheckMessage.Text = "正在检查本机考试环境…";
        try
        {
            var response = await ManagementRequestAsync(new(Protocol.Version, Guid.NewGuid(),
                _remoteExamState?.AutomaticPaused == true ? "remoteexam.inspect" : "remoteexam.preflight"));
            if (response.Outcome == "Succeeded" && response.RemoteExam is { } inspected)
            {
                _remoteExamInspectedRevision = inspected.RuntimeRevision;
                _remoteExamInspectedEpoch = inspected.Policy.ControlEpoch;
            }
            ApplyRemoteExam(response.RemoteExam);
            RemoteExamCheckMessage.Text = response.Message +
                (response.ErrorCode is { } code ? $"（{code}）" : "");
        }
        catch (Exception error) when (IsManagementError(error))
        { RemoteExamCheckMessage.Text = "未能读取检查结果，请核对后台连接。没有请求执行软件切换。"; }
        finally { _remoteExamBusy = false; UpdateRemoteExamControls(); }
    }
}

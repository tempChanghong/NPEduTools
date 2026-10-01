using NPEduTools.Contracts;
using NPEduTools.Core;
using NPEduTools.Integrations.Npep;

namespace NPEduTools.Host;

public sealed partial class PipeServer
{
    private RemoteExamStatus? RemoteExamSnapshot()
    {
        if (remoteExam is null || npep is null) return null;
        var state = remoteExam.State;
        var history = (state.Operations ?? []).Reverse().Take(8).Select(x => new RemoteExamHistory(
            x.Intent.OperationId, x.Outcome, x.Step, x.Reason, x.UpdatedAt, x.LocallyEndedAt, x.Intent.Target)).ToArray();
        return new(npep.ControlPolicy(), state.Revision, state.AutomaticPaused, state.PauseOperationId, state.StorageError, history, npep.PlanPolicy());
    }

    private async Task<HostResponse> RemoteExamLocalAsync(HostRequest request, CancellationToken token)
    {
        HostResponse Reply(string outcome, string? code, string message) => new(Protocol.Version,
            request.RequestId, outcome, code, message, RemoteExam: RemoteExamSnapshot());
        if (remoteExam is null || npep is null) return Reply("Rejected", "RemoteExamUnavailable", "请重启新版后台以使用本地远程考试管理。");
        try
        {
            if (request.Capability == "remoteexam.status") return Reply("Succeeded", null, "本地远程考试状态");
            var command = request.RemoteExam!;
            if (command.Action == "plan-consent")
            {
                npep.SetPlanConsent(command);
                return Reply("Succeeded", null, command.Allowed == true ? "已允许学校投递并明确开始考试方案放映。" : "已关闭方案放映许可；正在进行的放映需在 ExamAware 中手动结束。");
            }
            if (command.Action == "consent")
            {
                npep.SetControlConsent(command);
                return Reply("Succeeded", null, command.Allowed == true
                    ? "本机许可已保存，连接正常后将同步给学校服务。"
                    : "本机许可已关闭。已有考试状态与录课暂停保留，请按需在本机核实后结束。");
            }
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
            deadline.CancelAfter(TimeSpan.FromMilliseconds(request.TimeoutMs));
            await remoteExam.EndLocallyAsync(command.OperationId!.Value, command.Revision, deadline.Token);
            return Reply("Succeeded", null, "已结束这次远程考试状态，仅解除 N3 暂停。原自动录课计划仍受原开关、课堂模式及其他条件约束；未启动或退出软件。");
        }
        catch (NpepException error) { return Reply("Rejected", error.Code, RemoteExamPreflightMessage(error.Code)); }
        catch (RemoteExamException error) { return Reply("Rejected", error.Code, error.LocalMessage ?? RemoteExamPreflightMessage(error.Code)); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException or
            System.ComponentModel.Win32Exception or LaunchTargetException or OperationCanceledException)
        { return Reply("Rejected", "LOCAL_CONTROL_UNAVAILABLE", "未能确认操作结果，请刷新本地状态后再核实；不会自动执行软件切换。"); }
    }
}

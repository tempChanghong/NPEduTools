using System.IO;
using System.Text;
using System.Windows;
using Microsoft.Win32;
using NPEduTools.Contracts;

namespace NPEduTools.App;

public partial class ExamAwareWindow
{
    private ExamAwarePlanSummary? _prepared;
    private bool _allowPreparedPlan = true;
    private void RenderPlan(ExamAwareStatus? state, bool ready)
    {
        _prepared = _allowPreparedPlan ? state?.PreparedPlan : null;
        PreparePlanButton.IsEnabled = ready && state?.CanPresent == true;
        PresentPlanButton.IsEnabled = ready && _prepared is not null && state?.Player is { Known: true, Sessions.Length: 0 };
        PlanOperationText.Text = state?.PlanOperation?.Message ?? "需要桥接插件 0.4.0，并授权放映与状态读取权限。";
        PlanSummaryText.Text = _prepared is { } p
            ? $"{p.ExamName} · {p.Exams.Length} 场考试\n" + string.Join("\n", p.Exams.Select(x => $"{x.Name}　{x.Start} → {x.End}　提前 {x.AlertTime} 分钟提醒")) +
              (string.IsNullOrWhiteSpace(p.Message) ? "" : "\n提示语：" + p.Message) + "\n文件 SHA-256：" + p.Sha256
            : "尚无可启动的已校验方案。启动后或连接变化后，需要重新校验。";
        PlayerStateText.Text = state?.BridgeState != "Connected" || state.Player is not { Known: true } player
            ? "实时放映状态：未知（断线或无读取权限）"
            : player.Sessions.Length == 0 ? "实时放映状态：当前没有活动放映会话。"
            : "实时放映状态：\n" + string.Join("\n", player.Sessions.Select(x => $"{x.ExamName} · " + (x.State switch {
                "preparing" => "准备中", "opening" => "窗口已创建，等待就绪", "ready" => "已就绪", "closing" => "正在关闭", _ => "未知"
            })));
        if (state?.BridgeState == "Connected" && state.Player is { Known: true, LastSession: { } last })
            PlayerStateText.Text += "\n本次会话：" + last.Id + " · " + (last.State switch {
                "ready" => "已就绪", "opening" => "等待就绪", "preparing" => "准备中", "closing" => "正在关闭", "closed" => "已关闭", "failed" => "放映失败，请检查 ExamAware", _ => "未知"
            });
    }
    private async void PreparePlanClicked(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        var dialog = new OpenFileDialog { Title = "选择 ExamAware 考试方案", Filter = "考试方案 JSON|*.json|所有文件|*.*", CheckFileExists = true };
        if (dialog.ShowDialog(this) != true) return;
        // Drop the old start button immediately, including when the new file fails to read.
        _allowPreparedPlan = false; _prepared = null; PresentPlanButton.IsEnabled = false;
        try
        {
            // Bounded read, including files which grow after the dialog has closed.
            byte[] buffer = new byte[ExamAwarePlanContract.MaxBytes + 1];
            using var stream = new FileStream(dialog.FileName, FileMode.Open, FileAccess.Read, FileShare.Read);
            int count = stream.ReadAtLeast(buffer, buffer.Length, false);
            if (count > ExamAwarePlanContract.MaxBytes) throw new InvalidDataException();
            var input = new ExamAwarePlanInput("prepare", Convert.ToBase64String(buffer, 0, count));
            if (!ExamAwarePlanContract.Valid(input)) throw new InvalidDataException();
            PlanFileText.Text = "本次选择：" + Path.GetFileName(dialog.FileName);
            await RunAsync("examaware.plan", expectedRevision: _revision, plan: input);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or OperationCanceledException or DecoderFallbackException)
        { Message.Text = "无法读取方案：请使用 UTF-8 JSON，文件不超过 24 KiB；特殊字符过多时请缩短提示语后重试。"; }
    }
    private async void PresentPlanClicked(object sender, RoutedEventArgs e)
    {
        if (_busy || _prepared is not { } plan) return;
        long revision = _revision;
        if (MessageBox.Show(this, $"开始放映“{plan.ExamName}”（{plan.Exams.Length} 场）吗？\n将使用刚才校验的文件内容；如文件已修改，请取消并重新校验。\n已有放映不会被替换。",
            "开始考试放映", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;
        await RunAsync("examaware.plan", expectedRevision: revision, plan: new("start", PreparationId: plan.PreparationId));
    }
}

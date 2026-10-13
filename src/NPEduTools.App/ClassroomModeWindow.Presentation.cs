using System.Windows;
using System.Windows.Controls;
using NPEduTools.Contracts;

namespace NPEduTools.App;

public partial class ClassroomModeWindow
{
    private void ModeSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (ModeChoices is null || ExamCard is null) return;
        bool compact = ModeScroll.ActualWidth < 650;
        ModeChoices.ColumnDefinitions[1].Width = compact ? new(0) : new(1, GridUnitType.Star);
        Grid.SetColumn(ExamCard, compact ? 0 : 1); Grid.SetRow(ExamCard, compact ? 1 : 0);
        DailyCard.Margin = compact ? new(0, 0, 0, 12) : new(0, 0, 12, 0);
    }
    private void SwitchOptionsChanged(object sender, RoutedEventArgs e) => RenderSwitchPreview();
    private void RenderSwitchPreview()
    {
        if (DailyRuntimePreview is null || ExamRuntimePreview is null) return;
        bool running = SwitchRunning.IsChecked == true;
        DailyRuntimePreview.Text = running ? "当前软件：正常退出考试看板，再通过管理员任务启动 ClassIsland。请先结束放映、保存并关闭编辑器。" :
            "当前软件：不自动退出；为设置自启动，可能打开考试看板。";
        ExamRuntimePreview.Text = running ? "当前软件：准备好考试看板，再正常退出 ClassIsland。请先处理未保存内容与弹窗。" :
            "当前软件：不自动退出；为设置自启动，可能打开考试看板。";
    }
    private void RenderActualResults(ClassroomModeState? state)
    {
        ActualCheckedAtText.Text = state?.Actual is not null && state.CheckedAt is { } at
            ? $"最近核实：{at.ToLocalTime():yyyy-MM-dd HH:mm:ss} · 仅代表该时刻" : "尚未取得完整核实时间，请点击核实实际设置。";
        ActualClassIslandText.Text = state?.Actual is { } actual ? "ClassIsland 自启动：" + Enabled(actual.ClassIslandEnabled) : "ClassIsland 自启动：尚未核实";
        ActualExamAwareText.Text = state?.Actual is { } exam ? "ExamAware2 自启动登记：" + Enabled(exam.ExamAwareEnabled) : "ExamAware2 自启动登记：尚未核实";
        RuntimeProgressText.Text = state is null ? "当前软件切换与恢复状态未知，等待后台连接。" :
            _sending ? "正在等待管理验证或后台回执；本次执行结果尚未确认。" :
            state.Runtime is { } runtime ? "当前软件切换待完成：" + ModeName(runtime.Target) + "；请核对后台说明后重试。" :
            state.Busy ? "后台正在切换或核实，请等待新结果。" : state.Recovery is not null
            ? "存在未完成的模式切换，请先恢复切换前设置。" : "当前没有待重试的即时切换记录；自启动登记不代表软件已运行。";
        RenderSwitchPreview();
    }
}

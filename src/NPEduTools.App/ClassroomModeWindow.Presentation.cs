using System.Windows;
using System.Windows.Controls;
using NPEduTools.Contracts;

namespace NPEduTools.App;

public partial class ClassroomModeWindow
{
    private void ModeSizeChanged(object sender, SizeChangedEventArgs e) => UpdateModeLayout();

    private void UpdateModeLayout()
    {
        if (ModeChoices is null || ExamButton is null) return;
        bool embedded = WorkspacePanel.Embedded(this);
        bool compact = ModeScroll.ActualWidth < (embedded ? 840 : 650);
        bool examFirst = compact && embedded && _state?.Mode == "Daily";
        ModeChoices.ColumnDefinitions[1].Width = compact ? new(0) : new(1, GridUnitType.Star);
        Grid.SetColumn(ExamCard, compact ? 0 : 1);
        Grid.SetRow(DailyCard, examFirst ? 1 : 0);
        Grid.SetRow(ExamCard, compact && !examFirst ? 1 : 0);
        DailyCard.Margin = compact ? new(0, 0, 0, examFirst ? 0 : 12) : new(0, 0, 12, 0);
        ExamCard.Margin = new(0, 0, 0, examFirst ? 12 : 0);
        DailyButton.TabIndex = examFirst ? 1 : 0;
        ExamButton.TabIndex = examFirst ? 0 : 1;
    }

    private void UpdatePrimaryAction(ClassroomModeState? state)
    {
        Button? primary = null;
        if (RefreshButton.IsEnabled && _awaitingVerification) primary = RefreshButton;
        else if (RetryButton.IsEnabled) primary = RetryButton;
        else if (RestoreButton.IsEnabled) primary = RestoreButton;
        else if (DailyButton.IsEnabled) primary = state?.Mode == "Daily" ? ExamButton : DailyButton;

        ApplyActionStyle(DailyButton);
        ApplyActionStyle(ExamButton);
        ApplyActionStyle(RefreshButton, "TeacherQuiet");
        ApplyActionStyle(RetryButton);
        ApplyActionStyle(RestoreButton);
        DailyButton.Content = state?.Mode == "Daily" ? "重新应用日常模式" : "切换到日常模式";
        ExamButton.Content = state?.Mode == "Exam" ? "重新应用考试模式" : "切换到考试模式";
        RestoreButton.Visibility = state?.Recovery is null ? Visibility.Collapsed : Visibility.Visible;
        string badge = state is null || state.Busy || ClassroomModePresentation.From(state).Attention
            ? "TeacherWarningBrush" : state.MatchesMode == true ? "TeacherSuccessBrush" : "TeacherMutedBrush";
        ModeTitle.SetResourceReference(ForegroundProperty, badge);
        ModeBadge.SetResourceReference(Border.BorderBrushProperty, badge);

        void ApplyActionStyle(Button button, string secondary = "TeacherButton") =>
            button.SetResourceReference(StyleProperty, button == primary ? "TeacherPrimary" : secondary);
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
        bool needsAttention = state is null || state.Busy || state.Runtime is not null || state.Recovery is not null;
        RuntimeProgressText.Visibility = needsAttention || _sending ? Visibility.Visible : Visibility.Collapsed;
        RecoveryText.Visibility = state is null || state.Recovery is not null ? Visibility.Visible : Visibility.Collapsed;
        RuntimeProgressText.Text = state is null ? "当前软件切换与恢复状态未知，等待后台连接。" :
            _sending ? "正在等待管理验证或后台回执；本次执行结果尚未确认。" :
            state.Runtime is { } runtime ? "当前软件切换待完成：" + ModeName(runtime.Target) + "；请核对后台说明后重试。" :
            state.Busy ? "后台正在切换或核实，请等待新结果。" : state.Recovery is not null
            ? "存在未完成的模式切换，请先恢复切换前设置。" : "当前没有待重试的即时切换记录；自启动登记不代表软件已运行。";
        RenderSwitchPreview();
    }
}

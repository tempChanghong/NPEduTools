using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Threading;
using NPEduTools.App;
using NPEduTools.Contracts;
using NPEduTools.Core;

internal static partial class Program
{
    private static void RunWorkspaceReportThresholdChecks()
    {
        var previous = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Application.Current.Dispatcher));
        try
        {
            var errors = new List<Exception>();
            try { WorkspaceReportRefreshCheck(); }
            catch (Exception error) { errors.Add(error); }
            foreach (string scenario in new[] { "estimated-policy", "high-only", "zero-exceedance", "digital-silence", "partial-policy", "fractional-policy", "short-tail" })
            foreach (bool manual in new[] { false, true })
                try { WorkspaceReportThresholdCheck(scenario, manual); }
                catch (Exception error) { errors.Add(new InvalidOperationException($"{scenario}/{manual}: {error.Message}", error)); }
            if (errors.Count > 0) throw new AggregateException(errors);
        }
        finally { SynchronizationContext.SetSynchronizationContext(previous); }
    }

    private static void WorkspaceReportRefreshCheck()
    {
        var start = new DateTimeOffset(2026, 11, 19, 18, 40, 0, TimeSpan.FromHours(8));
        var schoolData = new StudyReportData(new(start, start.AddMinutes(45)), 2700, 2550, 2550d / 2700, .15, 100, 2,
            new("dBFS", 2550, -51.4, -22.1, 2500, 30, 4, [], 2490),
            new("estimated-dB(A)", 600, 49.6, 67.3, 600, 12, 3, [], 600), true, ["估算有效采样共 10 分钟。"]);
        var manualStart = start.AddHours(5).AddMinutes(5);
        var manualData = schoolData with
        {
            Window = new(manualStart, manualStart.AddMinutes(30)), ExpectedSeconds = 1800,
            SampledSeconds = 0, Coverage = 0, Raw = new("dBFS", 0, null, null, 0, 0, 0, []),
            Estimated = new("estimated-dB(A)", 0, null, null, 0, 0, 0, []),
            DisciplineReminders = 0, Complete = false, Mode = "Manual", ClockSource = "Windows", Notes = ["未取得有效采样。"]
        };
        string pipe = "NPEduTools.Test.report-refresh." + Guid.NewGuid().ToString("N");
        using var stop = new CancellationTokenSource();
        var initialSchool = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var oldRefresh = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int schoolReads = 0, manualReads = 0, oldReplies = 0;
        var ended = new ManualStudyState(Guid.NewGuid(), 2, Guid.NewGuid(), "Ended", "UserEnded", manualStart,
            manualStart.AddMinutes(30), false, null);
        var idleNoise = new NoiseState(ended.InstanceId, 2, "Idle", "Synthetic", "fixture", "隔离模拟麦克风", null, null, "Unknown", null, []);
        var server = Task.Run(() => WorkspaceProxy(pipe, async request =>
        {
            if (request.Capability is "noise.devices" or "noise.status" or "manual.study.status")
                return new(1, request.RequestId, "Succeeded", null, "", Noise: idleNoise, NoiseDevices: [], ManualStudy: ended);
            bool school = request.Capability == "study.report.status";
            if (!school && request.Capability != "manual.study.report.status") return new(1, request.RequestId, "Rejected", "UNSUPPORTED", "");
            int read = school ? Interlocked.Increment(ref schoolReads) : Interlocked.Increment(ref manualReads);
            if (school && read == 1) await initialSchool.Task.WaitAsync(stop.Token);
            if (read == 2) { await oldRefresh.Task.WaitAsync(stop.Token); Interlocked.Increment(ref oldReplies); }
            if (school && read >= 4 || !school && read == 3) return new(1, request.RequestId, "Rejected", "UNSUPPORTED", "");
            var data = read == 3 ? null : school ? schoolData : manualData;
            return new(1, request.RequestId, "Succeeded", null, "", StudyReport: new(Guid.NewGuid(), "fixture", false, "Report", 0, data));
        }, stop.Token));
        var main = PrivateWorkspace(pipe);
        try
        {
            Exercise(main, owner =>
            {
                main.Width = 840; main.Height = 600;
                main.UpdateLayout();
                Click(main, "StudyNav");
                var manualPage = WorkspaceField<NoiseWindow>(main, "_noiseWindow");
                PumpUntil(() => Button(manualPage, "ManualReportButton").IsVisible, "the ended manual session has no report entry");
                Click(manualPage, "ManualReportButton");
                var school = (ContentControl)main.FindName("WorkspaceSchoolReport");
                var manual = (ContentControl)main.FindName("WorkspaceManualReport");
                var details = (ContentControl)main.FindName("WorkspaceReportDetails");
                var scroll = (ScrollViewer)main.FindName("WorkspaceReportScroll");
                var reportTools = (FrameworkElement)main.FindName("WorkspaceReportTools");
                Assert(reportTools.IsDescendantOf(scroll), "device and calibration actions still occupy a fixed report footer");
                PumpUntil(() => manual.Content is StudyWorkspaceReport { State: WorkspaceReportState.Partial } &&
                    Volatile.Read(ref schoolReads) == 1, "manual report waited for the school report");
                Assert(school.Content is StudyWorkspaceReport { State: WorkspaceReportState.Loading } &&
                    manual.IsKeyboardFocused && !details.IsVisible && Button(main, "ReportWorkspaceTab").Tag as string == "active",
                    "the manual entry did not focus the manual summary while school data was loading");
                var manualView = (StudyWorkspaceReport)manual.Content;
                Assert(manualView is { HasValidSamples: false, SampledTime: "—", Coverage: "—", Clipping: "—", ReminderCount: "0 次", Raw: null, Estimated: null } &&
                    manualView.WindowText.Contains("2026-11-19") && manualView.WindowText.Contains("2026-11-20"), "zero samples or cross-day range misrepresented");
                Assert(((TextBlock)Find(manual, "StudyReportNoValidSamples")).IsVisible &&
                    ((TextBlock)Find(manual, "StudyReportReminderCount")).Text == "0 次", "zero recorded reminders were hidden or equated with missing samples");
                OpenDetails(manual);
                Assert(details.IsVisible && details.IsKeyboardFocused &&
                    details.Content is StudyWorkspaceReport { Title: "手动自习", IsDetailsOpen: true },
                    "opening the manual report did not focus its scoped details");
                AssertFocusedBorder(details);
                var noSamples = VisibleReportText(details);
                Assert(noSamples.Any(t => t.Contains("没有有效样本")) && noSamples.Any(t => t.Contains("Windows")) &&
                    noSamples.All(t => !t.Contains("dBFS") && !t.Contains("dB(A)")),
                    "a zero-sample report showed numeric channels or lost its clock evidence");
                main.UpdateLayout();
                double originalTop = Bounds(details, scroll).Top;
                initialSchool.SetResult();
                PumpUntil(() => school.Content is StudyWorkspaceReport { HasData: true }, "school report did not arrive");
                Assert(details.IsKeyboardFocused && details.Content is StudyWorkspaceReport { Title: "手动自习" } &&
                    ((StudyWorkspaceReport)manual.Content).IsDetailsOpen, "late school report replaced the manual selection or took its focus");
                PumpUntil(() => Math.Abs(Bounds(details, scroll).Top - originalTop) <= 1, "late school report moved the manual reading position");
                Snapshot(main, "teacher-study-report-no-samples.png");
                Button clickClose = (Button)Find(details, "WorkspaceCloseReportDetails");
                Press(clickClose);
                Assert(manual.IsKeyboardFocused && !details.IsVisible, "closing details did not return to the original summary");
                AssertFocusedBorder(manual);

                foreach (var (width, height, name) in new[] { (1040, 760, "teacher-study-reports.png"), (840, 600, "teacher-study-reports-narrow.png") })
                {
                    main.Width = width; main.Height = height;
                    scroll.ScrollToTop();
                    main.UpdateLayout();
                    PumpUntil(() => Math.Abs(main.ActualWidth - width) < 1 && Math.Abs(main.ActualHeight - height) < 1, "report main window did not resize");
                    Assert(Window.GetWindow(scroll) == main && new[] { "MinimizeWindow", "MaximizeRestoreWindow", "CloseWindow" }
                        .All(id => Find(main, id) is Button { IsVisible: true }), "report workspace lost the main window or its caption controls");
                    InViewport(school);
                    InViewport(manual);
                    InViewport((Button)Find(school, "StudyReportSamplingDetails"));
                    InViewport((Button)Find(manual, "StudyReportSamplingDetails"));
                    Snapshot(main, name);
                }
                OpenDetails(school);
                main.UpdateLayout();
                Snapshot(main, "teacher-study-report-details.png");
                Assert(details.Content is StudyWorkspaceReport { Title: "学校定时自习" } && !((StudyWorkspaceReport)manual.Content).IsDetailsOpen,
                    "the opened details did not switch to the school report");
                double detailTop = Bounds(details, scroll).Top;
                double manualBottom = Bounds(manual, scroll).Bottom;
                Assert(detailTop >= manualBottom - 1, $"the opened details do not follow both summaries ({detailTop:F2} versus {manualBottom:F2})");
                var detailText = VisibleReportText(details);
                Assert(detailText.Any(t => t.Contains("ClassIsland")) && detailText.Contains("4 秒（判定采样 41 分 40 秒）") &&
                    detailText.Any(t => t.Contains("学校定时自习 · 采样详情")) && detailText.Any(t => t.Contains("2026-11-19 18:40")),
                    $"expanded school report lost its source, time or readable threshold duration: {string.Join(" | ", detailText)}");
                var refresh = Button(main, "WorkspaceRefreshReports");
                var headerPosition = refresh.TranslatePoint(new(), main);
                scroll.ScrollToEnd();
                main.UpdateLayout();
                Assert(scroll.VerticalOffset > 0 && refresh.TranslatePoint(new(), main) == headerPosition, "refresh button scrolled away with sampling details");
                InViewport((TextBlock)Find(details, "StudyReportNotes"));
                Snapshot(main, "teacher-study-report-details-end.png");
                var closeSchool = (Button)Find(details, "WorkspaceCloseReportDetails");
                closeSchool.BringIntoView();
                main.UpdateLayout();
                InViewport(closeSchool);
                Press(closeSchool);
                scroll.ScrollToTop();
                school.MaxWidth = 340;
                main.UpdateLayout();
                var sampled = (TextBlock)Find(school, "StudyReportSampledTime");
                var reminders = (TextBlock)Find(school, "StudyReportReminderCount");
                Assert(reminders.TranslatePoint(new(), school).Y > sampled.TranslatePoint(new(), school).Y &&
                    reminders.TranslatePoint(new(reminders.ActualWidth, 0), school).X <= school.ActualWidth, "metrics did not wrap inside the narrow card");

                OpenDetails(school);
                Click(main, "WorkspaceRefreshReports");
                PumpUntil(() => Volatile.Read(ref schoolReads) == 2 && Volatile.Read(ref manualReads) == 2, "refresh did not request both sources");
                Assert(school.Content is StudyWorkspaceReport { State: WorkspaceReportState.Loading } &&
                    manual.Content is StudyWorkspaceReport { State: WorkspaceReportState.Loading } &&
                    !details.IsVisible, "refresh retained old results or open details while loading");
                Click(main, "WorkspaceRefreshReports");
                PumpUntil(() => school.Content is StudyWorkspaceReport { State: WorkspaceReportState.Empty } &&
                    manual.Content is StudyWorkspaceReport { State: WorkspaceReportState.Failed }, "new refresh did not resolve sources independently");
                oldRefresh.SetResult();
                PumpUntil(() => Volatile.Read(ref oldReplies) == 2, "superseded replies did not complete");
                Assert(school.Content is StudyWorkspaceReport { State: WorkspaceReportState.Empty } &&
                    manual.Content is StudyWorkspaceReport { State: WorkspaceReportState.Failed } && !details.IsVisible, "late refresh restored stale report data");
                Click(main, "WorkspaceRefreshReports");
                PumpUntil(() => school.Content is StudyWorkspaceReport { State: WorkspaceReportState.Failed } &&
                    manual.Content is StudyWorkspaceReport { State: WorkspaceReportState.Partial }, "school read failure prevented the manual report from recovering");
                OpenDetails(manual);
                Assert(details.IsVisible && details.Content is StudyWorkspaceReport { Title: "手动自习" },
                    "a failed school report blocked the successful manual report's details");
                Checks.Add("Local study reports in the 1040x760/840x600 main window: first-screen summaries, one scoped details panel, last field reachable, zero samples distinct, actual manual entry keeps focus and reading position after delayed school arrival, another source may fail independently, and stale refresh cannot restore data.");
                owner.Close();

                void InViewport(FrameworkElement item)
                {
                    Rect bounds = Bounds(item, scroll);
                    Assert(item.IsVisible && bounds.Width > 0 && bounds.Height > 0 && bounds.Left >= -1 && bounds.Top >= -1 &&
                        bounds.Right <= scroll.ViewportWidth + 1 && bounds.Bottom <= scroll.ViewportHeight + 1,
                        $"report field {item.Name} clipped at {main.Width}x{main.Height}: {bounds.Top:0}–{bounds.Bottom:0}/{scroll.ViewportHeight:0}");
                }

                void AssertFocusedBorder(ContentControl item)
                {
                    main.UpdateLayout();
                    var surface = Descendants<Border>(item).First();
                    Assert(surface.BorderBrush is SolidColorBrush border && main.FindResource("TeacherTextBrush") is SolidColorBrush focus &&
                        border.Color == focus.Color, "focused report surface lacks its visible outline");
                }
            });
        }
        finally
        {
            main.Close(); stop.Cancel();
            try { server.GetAwaiter().GetResult(); } catch (OperationCanceledException) { }
        }
    }

    private static void WorkspaceReportThresholdCheck(string scenario, bool manual)
    {
        var raw = new StudyReportAccumulator("dBFS");
        var estimate = new StudyReportAccumulator("estimated-dB(A)");
        var policy = new StudyNoise(true, -40, -50, 600, 3);
        if (scenario == "estimated-policy")
        {
            policy = policy with { Estimated = new(65, 50) };
            raw.Receive(1, 1, .001, -20, policy); estimate.Receive(1, 1, 1e6, 65, policy);
        }
        else if (scenario == "partial-policy")
        {
            raw.Receive(1, 1, .001, -20, null);
            raw.Receive(2, 1, .001, -20, policy with { MediumLineDbfs = null });
            raw.Receive(3, 1, .001, -20, policy with { MediumLineDbfs = null });
            raw.Receive(4, 1, .001, -20, policy);
        }
        else if (scenario is "fractional-policy" or "short-tail")
        {
            double seconds = scenario == "short-tail" ? .005 : .75;
            raw.Receive(seconds, seconds, .001, -20, policy);
        }
        else raw.Receive(1, 1, scenario == "digital-silence" ? 0 : scenario == "zero-exceedance" ? 1e-6 : .001, scenario == "digital-silence" ? null : -20,
            scenario == "high-only" ? policy with { MediumLineDbfs = null } : policy);
        var channel = raw.Snapshot();
        var window = StudyFixture().Window!; double expected = (window.End - window.Start).TotalSeconds;
        var data = new StudyReportData(window, expected, channel.SampledSeconds, channel.SampledSeconds / expected,
            0, 10, 2, channel, scenario == "estimated-policy" ? estimate.Snapshot() : null, true, [],
            Mode: manual ? "Manual" : "Daily", ClockSource: manual ? "Windows" : "ClassIsland");
        string pipe = "NPEduTools.Test.report-thresholds." + Guid.NewGuid().ToString("N");
        string capability = manual ? "manual.study.report.status" : "study.report.status";
        using var stop = new CancellationTokenSource();
        var server = Task.Run(() => WorkspaceProxy(pipe, request => Task.FromResult(new HostResponse(1, request.RequestId, "Succeeded", null, "isolated report",
            StudyReport: new(Guid.NewGuid(), "fixture", false, "Manual", 0, request.Capability == capability ? data : null))), stop.Token));
        var main = PrivateWorkspace(pipe);
        try
        {
            Exercise(main, owner =>
            {
                WorkspaceCall(main, "ShowStudyWorkspace", "Reports");
                var card = (ContentControl)main.FindName(manual ? "WorkspaceManualReport" : "WorkspaceSchoolReport");
                PumpUntil(() => card.Content is StudyWorkspaceReport { HasData: true }, "report page did not read the synthetic accumulator result");
                var report = (StudyWorkspaceReport)card.Content;
                var levels = report.Raw!;
                if (scenario is "estimated-policy" or "digital-silence")
                    Assert(levels is { MediumThreshold: "无有效判定数据", HighThreshold: "无有效判定数据" }, "unclassified raw samples displayed as zero exceedance");
                if (scenario == "estimated-policy")
                    Assert(report.Estimated is { MediumThreshold: "1 秒（判定采样 1 秒）", HighThreshold: "0 秒（判定采样 1 秒）" }, "estimated threshold evidence was dropped or mixed into raw units");
                if (scenario == "high-only")
                    Assert(levels is { MediumThreshold: "无有效判定数据", HighThreshold: "1 秒（判定采样 1 秒）" }, "absent medium line displayed as an observed zero");
                if (scenario == "zero-exceedance")
                    Assert(levels is { MediumThreshold: "0 秒（判定采样 1 秒）", HighThreshold: "0 秒（判定采样 1 秒）" }, "genuine observed zero lost its classification coverage");
                if (scenario == "partial-policy")
                    Assert(levels is { MediumThreshold: "1 秒（判定采样 1 秒）", HighThreshold: "3 秒（判定采样 3 秒）" }, "different medium/high classification coverage was flattened");
                if (scenario is "fractional-policy" or "short-tail")
                {
                    string seconds = scenario == "short-tail" ? "不足 0.01 秒" : "0.75 秒";
                    string threshold = $"{seconds}（判定采样 {seconds}）";
                    Assert(report.SampledTime == seconds && levels.MediumThreshold == threshold && levels.HighThreshold == threshold,
                        "positive fractional samples/exceedance were rounded to a false zero");
                }
                Assert(report.ReminderCount == "2 次" && report.Coverage != "0%", "report dropped reminders or rounded positive coverage to zero");
                main.UpdateLayout();
                var details = (ContentControl)main.FindName("WorkspaceReportDetails");
                Assert(!details.IsVisible, "sampling details opened before the teacher requested them");
                OpenDetails(card);
                main.UpdateLayout();
                var visible = VisibleReportText(details);
                var summary = VisibleReportText(card);
                Assert(visible.Contains(levels.HighThreshold) && visible.Any(t => t.Contains("dBFS")) &&
                    visible.Any(t => t.Contains("dB(A)")) == (report.Estimated is not null) &&
                    summary.Contains(report.ReminderCount) && summary.Contains(report.Coverage) &&
                    visible.All(t => !t.Contains("已上传")), "report template lost the bound threshold, units or summary values");
                Checks.Add($"{scenario}/{(manual ? "manual" : "school")}: actual accumulators/private pipe/report workspace distinguish unclassified samples from measured zero and preserve per-unit/per-line coverage; no sensors");
                owner.Close();
            });
        }
        finally
        {
            main.Close(); stop.Cancel();
            try { server.GetAwaiter().GetResult(); } catch (OperationCanceledException) { }
        }
    }

    private static void OpenDetails(ContentControl summary) => Press((Button)Find(summary, "StudyReportSamplingDetails"));

    private static void Press(Button button) => button.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent, button));

    private static string[] VisibleReportText(DependencyObject root) => Descendants<TextBlock>(root)
        .Where(t => t.IsVisible).Select(t => new TextRange(t.ContentStart, t.ContentEnd).Text).ToArray();
}

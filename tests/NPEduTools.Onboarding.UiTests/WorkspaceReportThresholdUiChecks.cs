using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
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
            new("estimated-dB(A)", 600, 49.6, 67.3, 600, 12, 3, [], 600), true, []);
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
        var firstManual = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var oldRefresh = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int schoolReads = 0, manualReads = 0, oldReplies = 0;
        var server = Task.Run(() => WorkspaceProxy(pipe, async request =>
        {
            bool school = request.Capability == "study.report.status";
            if (!school && request.Capability != "manual.study.report.status") return new(1, request.RequestId, "Rejected", "UNSUPPORTED", "");
            int read = school ? Interlocked.Increment(ref schoolReads) : Interlocked.Increment(ref manualReads);
            if (!school && read == 1) await firstManual.Task.WaitAsync(stop.Token);
            if (read == 2) { await oldRefresh.Task.WaitAsync(stop.Token); Interlocked.Increment(ref oldReplies); }
            if (!school && read >= 3) return new(1, request.RequestId, "Rejected", "UNSUPPORTED", "");
            var data = read >= 3 ? null : school ? schoolData : manualData;
            return new(1, request.RequestId, "Succeeded", null, "", StudyReport: new(Guid.NewGuid(), "fixture", false, "Report", 0, data));
        }, stop.Token));
        var main = PrivateWorkspace(pipe);
        try
        {
            Exercise(main, owner =>
            {
                Click(main, "StudyNav");
                Click(main, "ReportWorkspaceTab");
                var school = (ContentControl)main.FindName("WorkspaceSchoolReport");
                var manual = (ContentControl)main.FindName("WorkspaceManualReport");
                PumpUntil(() => school.Content is StudyWorkspaceReport { HasData: true } && Volatile.Read(ref manualReads) == 1, "school report waited for manual");
                Assert(manual.Content is StudyWorkspaceReport { State: WorkspaceReportState.Loading } && Button(main, "ReportWorkspaceTab").Tag as string == "active", "pending report or active tab mislabeled");
                firstManual.SetResult();
                PumpUntil(() => manual.Content is StudyWorkspaceReport { State: WorkspaceReportState.Partial }, "partial report did not appear");
                var manualView = (StudyWorkspaceReport)manual.Content;
                Assert(manualView is { SampledTime: "0 秒", Coverage: "0%", Clipping: "—", ReminderCount: "0 次", Estimated: null } &&
                    manualView.WindowText.Contains("2026-11-19") && manualView.WindowText.Contains("2026-11-20"), "zero samples or cross-day range misrepresented");
                Snapshot(main, "teacher-study-reports.png");
                var schoolDetails = Descendants<Expander>(school).Single();
                var manualDetails = Descendants<Expander>(manual).Single();
                Assert(!schoolDetails.IsExpanded && !manualDetails.IsExpanded, "details are not initially collapsed");
                schoolDetails.IsExpanded = true;
                main.UpdateLayout();
                Snapshot(main, "teacher-study-report-details.png");
                Assert(!manualDetails.IsExpanded, "opening school details also opened manual details");
                var detailText = Descendants<TextBlock>(school).Where(t => t.IsVisible).Select(t => new TextRange(t.ContentStart, t.ContentEnd).Text).ToArray();
                Assert(detailText.Any(t => t.Contains("ClassIsland")) && detailText.Contains("4 秒（判定采样 41 分 40 秒）"),
                    "expanded school report lost the clock source or readable threshold duration");
                var scroll = Descendants<ScrollViewer>((FrameworkElement)main.FindName("WorkspaceReports")).Single();
                var refresh = Button(main, "WorkspaceRefreshReports");
                var headerPosition = refresh.TranslatePoint(new(), main);
                scroll.ScrollToEnd();
                main.UpdateLayout();
                Assert(scroll.VerticalOffset > 0 && refresh.TranslatePoint(new(), main) == headerPosition, "refresh button scrolled away with sampling details");
                schoolDetails.IsExpanded = false;
                scroll.ScrollToTop();
                main.Width = main.MinWidth;
                main.Height = main.MinHeight;
                PumpUntil(() => Math.Abs(main.ActualWidth - main.Width) < 1, "window did not resize");
                Snapshot(main, "teacher-study-reports-narrow.png");
                school.MaxWidth = 420;
                main.UpdateLayout();
                var sampled = (TextBlock)Find(school, "StudyReportSampledTime");
                var reminders = (TextBlock)Find(school, "StudyReportReminderCount");
                Assert(reminders.TranslatePoint(new(), school).Y > sampled.TranslatePoint(new(), school).Y &&
                    reminders.TranslatePoint(new(reminders.ActualWidth, 0), school).X <= school.ActualWidth, "metrics did not wrap inside the narrow card");

                Click(main, "WorkspaceRefreshReports");
                PumpUntil(() => Volatile.Read(ref schoolReads) == 2 && Volatile.Read(ref manualReads) == 2, "refresh did not request both sources");
                Assert(school.Content is StudyWorkspaceReport { State: WorkspaceReportState.Loading } &&
                    manual.Content is StudyWorkspaceReport { State: WorkspaceReportState.Loading }, "refresh retained old results while loading");
                Click(main, "WorkspaceRefreshReports");
                PumpUntil(() => school.Content is StudyWorkspaceReport { State: WorkspaceReportState.Empty } &&
                    manual.Content is StudyWorkspaceReport { State: WorkspaceReportState.Failed }, "new refresh did not resolve sources independently");
                oldRefresh.SetResult();
                PumpUntil(() => Volatile.Read(ref oldReplies) == 2, "superseded replies did not complete");
                Assert(school.Content is StudyWorkspaceReport { State: WorkspaceReportState.Empty } &&
                    manual.Content is StudyWorkspaceReport { State: WorkspaceReportState.Failed }, "late refresh restored stale report data");
                Checks.Add("Teacher report cards: independent arrival, partial cross-day session, empty versus failed, responsive metrics, independent details and latest refresh wins");
                owner.Close();
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
                var details = Descendants<Expander>(card).Single();
                Assert(!details.IsExpanded, "sampling details opened before the teacher requested them");
                details.IsExpanded = true;
                main.UpdateLayout();
                var visible = Descendants<TextBlock>(card).Where(t => t.IsVisible).Select(t => t.Text).ToArray();
                Assert(visible.Contains(levels.HighThreshold) && visible.Any(t => t.Contains("dBFS")) &&
                    visible.Any(t => t.Contains("dB(A)")) == (report.Estimated is not null) &&
                    visible.Contains(report.ReminderCount) && visible.Contains(report.Coverage) &&
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
}

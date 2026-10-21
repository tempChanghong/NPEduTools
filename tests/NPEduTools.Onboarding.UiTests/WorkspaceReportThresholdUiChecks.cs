using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
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
            foreach (string scenario in new[] { "estimated-policy", "high-only", "zero-exceedance", "digital-silence", "partial-policy", "fractional-policy", "short-tail" })
            foreach (bool manual in new[] { false, true })
                try { WorkspaceReportThresholdCheck(scenario, manual); }
                catch (Exception error) { errors.Add(new InvalidOperationException($"{scenario}/{manual}: {error.Message}", error)); }
            if (errors.Count > 0) throw new AggregateException(errors);
        }
        finally { SynchronizationContext.SetSynchronizationContext(previous); }
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
            StudyReport: request.Capability == capability ? new(Guid.NewGuid(), "fixture", true, "Report", 270, data) : null)), stop.Token));
        var main = PrivateWorkspace(pipe);
        try
        {
            Exercise(main, owner =>
            {
                WorkspaceCall(main, "ShowStudyWorkspace", "Reports");
                var label = (TextBlock)main.FindName(manual ? "WorkspaceManualReport" : "WorkspaceSchoolReport");
                PumpUntil(() => label.Text.Contains("dBFS"), "report page did not read the synthetic accumulator result");
                string text = label.Text;
                if (scenario is "estimated-policy" or "digital-silence")
                    Assert(text.Contains("原始阈值超限：本单位没有有效阈值判定数据") && !text.Contains("原始阈值超限：中线 0 秒"), "unclassified raw samples displayed as zero exceedance");
                if (scenario == "estimated-policy")
                    Assert(text.Contains("估算阈值超限：中线 1 秒（判定采样 1 秒） · 高线 0 秒（判定采样 1 秒）"), "estimated threshold evidence was dropped or mixed into raw units");
                if (scenario == "high-only")
                    Assert(text.Contains("原始阈值超限：中线没有有效判定数据 · 高线 1 秒（判定采样 1 秒）"), "absent medium line displayed as an observed zero");
                if (scenario == "zero-exceedance")
                    Assert(text.Contains("原始阈值超限：中线 0 秒（判定采样 1 秒） · 高线 0 秒（判定采样 1 秒）"), "genuine observed zero lost its classification coverage");
                if (scenario == "partial-policy")
                    Assert(text.Contains("原始阈值超限：中线 1 秒（判定采样 1 秒） · 高线 3 秒（判定采样 3 秒）"), "different medium/high classification coverage was flattened");
                if (scenario is "fractional-policy" or "short-tail")
                {
                    string seconds = scenario == "short-tail" ? "不足 0.01 秒" : "0.75 秒";
                    Assert(text.Contains($"有效采样 {seconds}") && text.Contains($"原始阈值超限：中线 {seconds}（判定采样 {seconds}） · 高线 {seconds}（判定采样 {seconds}）"), "positive fractional samples/exceedance were rounded to a false zero");
                }
                Assert(text.Contains("提醒 2 次") && text.Contains("覆盖率") && !text.Contains("已上传"), "report lost existing fields or claimed an upload");
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

using NPEduTools.Contracts;

namespace NPEduTools.App;

internal enum WorkspaceReportState { Loading, Empty, Failed, Complete, Partial }

internal sealed record StudyWorkspaceReport(string Title, WorkspaceReportState State, string Status)
{
    public bool HasData => State is WorkspaceReportState.Complete or WorkspaceReportState.Partial;
    public bool HasValidSamples { get; private init; }
    public bool IsDetailsOpen { get; init; }
    public string WindowText { get; private init; } = "";
    public string SampledTime { get; private init; } = "";
    public string Coverage { get; private init; } = "";
    public string ReminderCount { get; private init; } = "";
    public WorkspaceReportChannel? Raw { get; private init; }
    public WorkspaceReportChannel? Estimated { get; private init; }
    public string Clipping { get; private init; } = "";
    public string ClockSource { get; private init; } = "";
    public string Notes { get; private init; } = "";

    public static StudyWorkspaceReport Loading(string title) => new(title, WorkspaceReportState.Loading, "正在读取");
    public static StudyWorkspaceReport Failed(string title) => new(title, WorkspaceReportState.Failed, "读取失败");

    public static StudyWorkspaceReport FromResponse(string title, HostResponse response)
    {
        if (response.Outcome != "Succeeded" || response.StudyReport is not { } report) return Failed(title);
        if (report.Data is not { } data) return new(title, WorkspaceReportState.Empty, "暂无本机报告");
        if (data.Window is null || data.Raw is null) return Failed(title);
        bool hasSamples = double.IsFinite(data.SampledSeconds) && data.SampledSeconds > 0;

        // Available controls the classroom popup. The teacher can still read a returned report.
        return new(title, data.Complete ? WorkspaceReportState.Complete : WorkspaceReportState.Partial,
            data.Complete ? "会话完整" : "会话不完整")
        {
            WindowText = StudySchedulePresentation.Span(data.Window),
            HasValidSamples = hasSamples,
            SampledTime = hasSamples ? Duration(data.SampledSeconds) : "—",
            Coverage = hasSamples ? Percent(100 * data.Coverage) : "—",
            ReminderCount = $"{data.DisciplineReminders} 次",
            Raw = data.Raw.SampledSeconds > 0 ? Channel(data.Raw) : null,
            Estimated = data.Estimated is { SampledSeconds: > 0 } estimated ? Channel(estimated) : null,
            Clipping = hasSamples ? Percent(data.ClippedPercent) : "—",
            ClockSource = string.IsNullOrWhiteSpace(data.ClockSource) ? "—" : data.ClockSource,
            Notes = string.Join("\n", data.Notes ?? [])
        };
    }

    private static WorkspaceReportChannel Channel(StudyReportChannel channel) => new(
        Number(channel.EnergyMean), Number(channel.Maximum),
        Threshold(channel.MediumSeconds, channel.MediumThresholdSampledSeconds),
        Threshold(channel.HighSeconds, channel.ThresholdSampledSeconds));

    private static string Number(double? value) =>
        value is { } v && double.IsFinite(v) ? v.ToString("F1") : "—";

    private static string Threshold(double exceeded, double classified) => classified > 0
        ? $"{Duration(exceeded)}（判定采样 {Duration(classified)}）" : "无有效判定数据";

    private static string Seconds(double value) => value switch
    {
        < 0 => "—",
        > 0 and < .01 => "不足 0.01 秒",
        _ when double.IsFinite(value) => $"{value:0.##} 秒",
        _ => "—"
    };

    private static string Duration(double seconds)
    {
        if (!double.IsFinite(seconds) || seconds < 60) return Seconds(seconds);
        double rounded = Math.Round(seconds, 2);
        double minutes = Math.Floor(rounded / 60);
        double rest = rounded % 60;
        return rest == 0 ? $"{minutes:0} 分钟" : $"{minutes:0} 分 {rest:0.##} 秒";
    }

    private static string Percent(double value) => value switch
    {
        > 0 and < .01 => "不足 0.01%",
        > 99.99 and < 100 => "超过 99.99%",
        >= 0 and <= 100 => $"{value:0.##}%",
        _ => "—"
    };
}

internal sealed record WorkspaceReportChannel(string Mean, string Maximum, string MediumThreshold, string HighThreshold);

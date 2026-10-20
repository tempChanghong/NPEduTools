using NPEduTools.Contracts;
using NPEduTools.Host;

namespace NPEduTools.Tests;

public sealed partial class StudyReportTests
{
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task A_temporarily_full_collector_can_recover_the_current_session_but_not_hide_a_missed_session(bool missedSession)
    {
        var clock = new Clock(); var id = Guid.NewGuid(); var state = Active(id); var statistics = Report(id);
        var finished = new Dictionary<Guid, StudySessionReport>();
        var reports = new List<StudySessionReport>();
        await using var service = new StudyReportService(() => ("class-a", true, state), () => statistics, clock, false,
            finishedStatistics: session => finished.GetValueOrDefault(session));
        int count = missedSession ? 18 : 17;
        for (int i = 0; i < count; i++)
        {
            id = Guid.NewGuid(); clock.Ms = i * 100_000;
            state = Active(id) with { SchoolNow = Window.Start.AddSeconds((i + 1) * 100) };
            reports.Add(statistics = Report(id)); service.Snapshot();
        }
        // The same current session is still available when earlier terminal receipts arrive.
        foreach (var report in reports) finished[report.SessionId] = report with { Complete = true };
        statistics = finished[id]; clock.Ms += 1000; service.Snapshot();
        state = state with { Phase = "Outside", Window = null, SchoolNow = Window.End, Execution = null };
        service.Snapshot(); clock.Ms += 5000;
        var data = service.Snapshot().Data!;
        Assert.Equal(34, data.SampledSeconds);
        Assert.Equal(!missedSession, data.Complete);
        Assert.Equal(missedSession, data.Notes.Any(note => note.Contains("尾段未能确认")));
    }

    [Theory]
    [InlineData(17, "none")] [InlineData(64, "none")]
    [InlineData(64, "scope")] [InlineData(64, "mode")] [InlineData(64, "version")]
    public async Task Repeated_recovery_keeps_every_witnessed_terminal_session_in_the_final_report(int count, string withdraw)
    {
        var clock = new Clock(); var id = Guid.NewGuid(); var state = Active(id);
        var statistics = Report(id); string scope = "class-a";
        var finished = new Dictionary<Guid, StudySessionReport>();
        await using var service = new StudyReportService(() => (scope, true, state), () => statistics, clock, false,
            finishedStatistics: session => finished.GetValueOrDefault(session));
        for (int i = 0; i < count; i++)
        {
            id = Guid.NewGuid(); clock.Ms = i * 100_000;
            state = Active(id) with { SchoolNow = Window.Start.AddSeconds((i + 1) * 100) };
            statistics = Report(id, estimateReason: i == 0 ? "INPUT_SETTINGS_CHANGED" : null) with { ReferenceNote = "reference-" + i };
            service.Snapshot();
            finished[id] = statistics with { Complete = true, Frames = 5 };
        }
        statistics = finished[id];
        state = state with { Phase = "Outside", Window = null, SchoolNow = Window.End, Execution = null };
        service.Snapshot(); clock.Ms += 5000;
        var data = service.Snapshot().Data!;
        Assert.True(data.Complete); Assert.Equal(count * 2, data.SampledSeconds);
        Assert.Equal(count * 5, data.Frames); Assert.Equal(count * 3, data.DisciplineReminders);
        Assert.Equal(count, data.Estimated!.SampledSeconds);
        Assert.Equal(count * .01 / (count * 2) * 100, data.ClippedPercent, 8);
        Assert.Equal(count * 2, data.Raw.Trend.Sum(point => point.SampledSeconds));
        Assert.InRange(data.Raw.Trend.Count, 1, 180); Assert.InRange(data.Estimated.Trend.Count, 1, 180);
        Assert.Equal(Report(id).Raw.EnergyMean!.Value, data.Raw.EnergyMean!.Value, 8);
        Assert.Equal(count * Report(id).Raw.HighSeconds, data.Raw.HighSeconds, 8);
        Assert.Contains(data.Notes, note => note.Contains("参考已失效"));
        for (int i = 0; i < 3; i++) Assert.Contains("reference-" + i, data.Notes);
        Assert.Contains("reference-" + (count - 1), data.Notes); Assert.InRange(data.Notes.Count, 1, 40);
        if (count == 64) Assert.Contains(data.Notes, note => note.Contains("说明较多"));
        if (withdraw == "scope") scope = "class-b";
        if (withdraw == "mode") state = state with { Mode = "Exam" };
        if (withdraw == "version") state = state with { Version = "version-b" };
        if (withdraw != "none") { Assert.False(service.Snapshot().Available); Assert.Null(service.Snapshot().Data); }
    }

    [Fact]
    public async Task Unresolved_overflow_cannot_be_reported_as_complete_when_only_tracked_tails_arrive_later()
    {
        var clock = new Clock(); var id = Guid.NewGuid(); var state = Active(id); var statistics = Report(id);
        var finished = new Dictionary<Guid, StudySessionReport>();
        var reports = new List<StudySessionReport>();
        await using var service = new StudyReportService(() => ("class-a", true, state), () => statistics, clock, false,
            finishedStatistics: session => finished.GetValueOrDefault(session));
        for (int i = 0; i < 17; i++)
        {
            id = Guid.NewGuid(); clock.Ms = i * 100_000;
            state = Active(id) with { SchoolNow = Window.Start.AddSeconds((i + 1) * 100) };
            reports.Add(statistics = Report(id)); service.Snapshot();
        }
        foreach (var report in reports) finished[report.SessionId] = report with { Complete = true };
        statistics = finished[id];
        state = state with { Phase = "Outside", Window = null, SchoolNow = Window.End, Execution = null };
        service.Snapshot(); clock.Ms += 5000;
        var data = service.Snapshot().Data!;
        Assert.False(data.Complete); Assert.Contains(data.Notes, note => note.Contains("尾段未能确认"));
    }
}

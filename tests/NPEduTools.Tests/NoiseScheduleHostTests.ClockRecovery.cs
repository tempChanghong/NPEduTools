using NPEduTools.Contracts;
using NPEduTools.Host;

namespace NPEduTools.Tests;

public sealed partial class NoiseScheduleHostTests
{
    [Theory]
    [InlineData(false, false)] [InlineData(false, true)]
    [InlineData(true, false)] [InlineData(true, true)]
    public async Task School_clock_recovery_keeps_both_real_capture_segments_in_the_same_study_report(bool offline, bool exam)
    {
        await using var f = new Fixture(); f.Base = new(2026, 10, 1, 19, 59, 35, TimeSpan.Zero);
        var config = StudyConfig(f);
        config["effective"]!["settings"]!["examSchedule"] = config["effective"]!["settings"]!["schedule"]!.DeepClone();
        bool clockAvailable = true; long sequence = 0;
        var connection = Guid.NewGuid(); var bridge = Guid.NewGuid();
        SchoolClockFrame ReadClock() => clockAvailable
            ? new(connection, bridge, ++sequence, 0, f.Base.AddMilliseconds(f.Time.Ms), 0, "Advancing", "synthetic")
            : SchoolClockFrame.Unavailable("synthetic clock interruption");
        await f.Scheduler.DisposeAsync();
        f.Scheduler = new(f.Noise, ReadClock, () => false, f.Path, f.Time, false, mode: () => exam ? "Exam" : "Daily");
        await using var study = new StudySettingsService(ReadClock, () => false, f.Time, false, f.Scheduler, () => exam ? "Exam" : "Daily");
        study.BindStudy(f.Scope); study.ApplyStudy(config);
        var grant = Grant(config); if (exam) grant["mode"] = "Exam";
        study.ConfirmStudyExecution(config, grant); await f.Start();
        f.Tick(); study.ApplyStudy(config); f.Tick(); study.ApplyStudy(config);
        await using var report = new StudyReportService(study.DisplaySnapshot, f.Noise.StudyReportSnapshot, f.Time, false,
            finishedStatistics: f.Noise.FinishedStudyReport);
        var initial = report.Snapshot(); Assert.Equal("Monitoring", initial.State);
        for (int i = 0; i < 10; i++) { f.Captures[0].Sample(); f.Tick(); study.ApplyStudy(config); report.Snapshot(); }
        var firstSession = f.Noise.Snapshot().SessionId;
        if (offline) study.StudyFailed("STUDY_OFFLINE");
        clockAvailable = false; f.Tick(); study.Tick();
        var blocked = study.Snapshot();
        Assert.False(blocked.ClockReady); Assert.Equal("SCHOOL_CLOCK_UNAVAILABLE", blocked.Reason);
        Assert.False(report.Snapshot().Available); Assert.Null(report.Snapshot().Data);
        await Until(() => !f.Noise.CaptureBusy);
        var first = f.Noise.StudyReportSnapshot()!; Assert.True(first.Complete);
        for (int i = 0; i < 4; i++) { f.Tick(); study.Tick(); report.Snapshot(); }
        Assert.Single(f.Captures); // No clock fallback or capture during the interruption.

        clockAvailable = true;
        await Until(() => {
            f.Tick(); if (offline) study.Tick(); else study.ApplyStudy(config);
            report.Snapshot();
            return f.Noise.Snapshot() is { State: "Active" } resumed && resumed.SessionId != firstSession;
        });
        while (f.Time.Ms < 25000)
        {
            f.Captures[^1].Sample(); f.Tick();
            if (offline) study.Tick(); else study.ApplyStudy(config);
            report.Snapshot();
        }
        await Until(() => !f.Noise.CaptureBusy);
        var second = f.Noise.StudyReportSnapshot()!;
        for (int i = 0; i < 11; i++) { f.Tick(); if (offline) study.Tick(); else study.ApplyStudy(config); report.Snapshot(); }
        var final = report.Snapshot();
        Assert.True(final.Available); Assert.True(final.Data!.Complete);
        Assert.Equal(initial.InstanceId, final.InstanceId);
        Assert.Equal(first.Raw.SampledSeconds + second.Raw.SampledSeconds, final.Data.SampledSeconds, 6);
        Assert.Equal(first.Frames + second.Frames, final.Data.Frames);
        Assert.Equal(3600, final.Data.ExpectedSeconds);
        Assert.Equal(final.Data.SampledSeconds / final.Data.ExpectedSeconds, final.Data.Coverage, 8);
        Assert.Equal(exam ? "Exam" : "Daily", final.Data.Mode);
        Assert.Equal(exam ? "Windows" : "ClassIsland", final.Data.ClockSource);
        Assert.Equal(2, f.Captures.Count); Assert.All(f.Captures, capture => Assert.True(capture.Disposed));
    }
}

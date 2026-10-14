using System.Text.Json;
using NPEduTools.Contracts;
using NPEduTools.Core;
using NPEduTools.Host;

namespace NPEduTools.Tests;

public sealed class StudyReportTests
{
    private sealed class Clock : TimeProvider
    {
        public long Ms;
        public override long TimestampFrequency => 1000;
        public override long GetTimestamp() => Ms;
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.MinValue;
    }
    private static readonly SchoolNoiseWindow Window = new(DateTimeOffset.Parse("2026-10-12T19:00:00Z"), DateTimeOffset.Parse("2026-10-12T21:30:00Z"));
    private static readonly StudyNoise Policy = new(true, -40, -50, 600, 3);
    private static readonly StudySettings Settings = new(new("Asia/Shanghai", "2026-10-12", null, [], []), new("StatusTrend", 180),
        new(true, "OwnClass", [], 60), Policy, new(false, 3, 30, 30, true, false));
    private static StudyHostState Active(Guid session) => new("version-a", "APPLIED", "ACTIVE", "ACTIVE", "Active", "WINDOW_ACTIVE", true,
        Window.Start.AddSeconds(100), Window, null, Settings, new("Schedule", "Active", session, "WINDOW_ACTIVE", true, null, null, true));
    private static StudySessionReport Report(Guid id, bool complete = false, string? estimateReason = null)
    {
        var raw = new StudyReportAccumulator("dBFS");
        raw.Receive(1, 1, .0001, -30, Policy); raw.Receive(61, 1, .01, -10, Policy);
        var estimated = new StudyReportAccumulator("estimated-dB(A)");
        estimated.Receive(1, 1, 1e6, 65, Policy with { Estimated = new(70, 55) });
        return new(id, complete, 100, .01, 2, 3, raw.Snapshot(), estimated.Snapshot(), estimateReason);
    }
    [Fact]
    public async Task Five_second_end_then_five_minute_report_then_close_uses_Host_monotonic_time()
    {
        var clock = new Clock(); var id = Guid.NewGuid(); var state = Active(id); var statistics = Report(id);
        await using var service = new StudyReportService(() => ("class-a", true, state), () => statistics, clock, false);
        Assert.False(service.Snapshot().Available);
        state = state with { Phase = "Outside", Window = null, SchoolNow = Window.End, Execution = null };
        statistics = statistics with { Complete = true };
        var end = service.Snapshot(); Assert.Equal("EndReminder", end.State); Assert.False(end.Available);
        clock.Ms = 4999; Assert.False(service.Snapshot().Available);
        clock.Ms = 5000; var report = service.Snapshot(); Assert.True(report.Available); Assert.Equal(300, report.RemainingSeconds);
        Assert.Equal(3, report.Data!.DisciplineReminders); Assert.Equal(2d / 9000, report.Data.Coverage, 8);
        clock.Ms = 125000; Assert.Equal(180, service.Snapshot().RemainingSeconds); // Reopening the App only reads status.
        Assert.Equal(report.InstanceId, service.Snapshot().InstanceId);
        clock.Ms = 304999; Assert.True(service.Snapshot().Available);
        clock.Ms = 305000; Assert.False(service.Snapshot().Available); Assert.Equal("Expired", service.Snapshot().State);
        // Stale school wall time and a fresh status read must never restart the deadline.
        clock.Ms += 600000; Assert.False(service.Snapshot().Available);
    }
    [Theory]
    [InlineData("clock")] [InlineData("exam")] [InlineData("revoked")] [InlineData("scope")] [InlineData("version")]
    public async Task Withdrawn_context_cannot_show_previous_report(string change)
    {
        var clock = new Clock(); var id = Guid.NewGuid(); var state = Active(id); string scope = "class-a";
        await using var service = new StudyReportService(() => (scope, true, state), () => Report(id), clock, false,
            finishedStatistics: session => session == id ? Report(id, true) : null);
        service.Snapshot(); state = state with { Phase = "Outside", Window = null, SchoolNow = Window.End };
        service.Snapshot(); clock.Ms = 5000; Assert.True(service.Snapshot().Available);
        if (change == "clock") state = state with { ClockReady = false };
        if (change == "exam") state = state with { Phase = "Blocked", Reason = "EXAM_PAUSED" };
        if (change == "revoked") state = state with { PolicyState = "UNAVAILABLE" };
        if (change == "scope") scope = "class-b";
        if (change == "version") state = state with { Version = "version-b" };
        Assert.False(service.Snapshot().Available);
        state = state with { ClockReady = true, Phase = "Outside", PolicyState = "APPLIED" };
        Assert.False(service.Snapshot().Available);
    }
    [Fact]
    public async Task Late_reconnect_manual_capture_and_zero_data_do_not_manufacture_monitoring_success()
    {
        var id = Guid.NewGuid(); var state = Active(id) with { Phase = "Outside", Window = null, SchoolNow = Window.End.AddSeconds(60) };
        await using var service = new StudyReportService(() => ("class-a", true, state), () => Report(id, true), new Clock(), false);
        Assert.Null(service.Snapshot().Data);
        state = Active(id) with { Execution = new("Manual", "Active", id, "MANUAL", true, null, null, true) };
        service.Snapshot(); state = state with { Phase = "Outside", Window = null, SchoolNow = Window.End };
        Assert.Equal(0, service.Snapshot().Data!.SampledSeconds);
        Assert.Null(service.Snapshot().Data!.Raw.EnergyMean);
        Assert.Contains(service.Snapshot().Data!.Notes, n => n.Contains("未取得有效采样"));
    }
    [Fact]
    public async Task Final_tail_updates_until_complete_and_freezes_without_waiting_for_server_receipt()
    {
        var clock = new Clock(); var id = Guid.NewGuid(); var state = Active(id); var statistics = Report(id);
        await using var service = new StudyReportService(() => ("class-a", true, state), () => statistics, clock, false);
        service.Snapshot(); state = state with { Phase = "Outside", Window = null, SchoolNow = Window.End }; service.Snapshot();
        clock.Ms = 5000; Assert.False(service.Snapshot().Data!.Complete);
        statistics = statistics with { Complete = true, Frames = 5, EstimateReason = "INPUT_SETTINGS_CHANGED" };
        var final = service.Snapshot(); Assert.True(final.Data!.Complete); Assert.Equal(5, final.Data.Frames);
        Assert.Contains(final.Data.Notes, n => n.Contains("参考已失效"));
        statistics = statistics with { Frames = 999 }; Assert.Equal(5, service.Snapshot().Data!.Frames);
    }
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task Finished_lookup_cannot_adopt_another_session_or_an_unfinished_snapshot(bool sameSession)
    {
        var clock = new Clock(); var id = Guid.NewGuid(); var state = Active(id); StudySessionReport? statistics = Report(id);
        var wrong = sameSession ? Report(id) : Report(Guid.NewGuid(), true);
        await using var service = new StudyReportService(() => ("class-a", true, state), () => statistics, clock, false,
            finishedStatistics: _ => wrong);
        service.Snapshot(); statistics = null;
        state = state with { Phase = "Outside", Window = null, SchoolNow = Window.End, Execution = null };
        service.Snapshot(); clock.Ms = 5000;
        Assert.False(service.Snapshot().Data!.Complete); Assert.Equal(2, service.Snapshot().Data!.Frames);
        wrong = Report(id, true) with { Frames = 5 };
        Assert.True(service.Snapshot().Data!.Complete); Assert.Equal(5, service.Snapshot().Data!.Frames);
    }
    [Fact]
    public void Energy_mean_threshold_durations_and_units_are_separate_and_invalid_data_is_excluded()
    {
        var raw = new StudyReportAccumulator("dBFS");
        raw.Receive(1, 1, 1e-6, -50, Policy); raw.Receive(2, 1, .01, -10, Policy);
        raw.Receive(3, 1, double.NaN, -20, Policy); raw.Receive(4, -1, .01, -20, Policy);
        raw.Receive(5, 1, .01, -10, Policy with { Estimated = new(70, 55) });
        var r = raw.Snapshot(); Assert.Equal(3, r.SampledSeconds); Assert.Equal(2, r.ThresholdSampledSeconds);
        Assert.Equal(1, r.HighSeconds); Assert.Equal(1, r.MediumSeconds);
        Assert.Equal(2, r.MediumThresholdSampledSeconds);
        Assert.Equal(10 * Math.Log10((1e-6 + .02) / 3), r.EnergyMean!.Value, 8);
        var estimated = new StudyReportAccumulator("estimated-dB(A)");
        estimated.Receive(1, 1, 1e6, 65, Policy); Assert.Equal(0, estimated.Snapshot().ThresholdSampledSeconds);
        estimated.Receive(2, 1, 1e8, 85, Policy with { Estimated = new(70, 55) });
        Assert.Equal(1, estimated.Snapshot().HighSeconds); Assert.Equal(1, estimated.Snapshot().MediumSeconds);
        Assert.Equal(1, estimated.Snapshot().ThresholdSampledSeconds);
        var highOnly = new StudyReportAccumulator("dBFS"); highOnly.Receive(1, 1, .01, -10, Policy with { MediumLineDbfs = null });
        Assert.Equal(0, highOnly.Snapshot().MediumThresholdSampledSeconds); Assert.Equal(1, highOnly.Snapshot().HighSeconds);
    }
    [Fact]
    public void Digital_silence_keeps_coverage_but_does_not_invent_a_finite_sound_level()
    {
        var a = new StudyReportAccumulator("dBFS"); a.Receive(1, 1, 0, null, Policy);
        var r = a.Snapshot(); Assert.Equal(1, r.SampledSeconds); Assert.Null(r.EnergyMean); Assert.Null(r.Maximum);
        Assert.Null(r.Trend.Single().Mean); Assert.Equal(0, r.ThresholdSampledSeconds);
    }
    [Fact]
    public void Full_three_hour_trend_is_bounded_and_compatible_with_pipe_frame_limit()
    {
        var raw = new StudyReportAccumulator("dBFS"); var estimated = new StudyReportAccumulator("estimated-dB(A)");
        for (int i = 0; i < 10800; i++)
        {
            raw.Receive(i + 1, 1, Math.Pow(10, -30d / 10), -20, Policy);
            estimated.Receive(i + 1, 1, 1e7, 80, Policy with { Estimated = new(70, 55) });
        }
        Assert.Equal(180, raw.Snapshot().Trend.Count); Assert.Equal(180, estimated.Snapshot().Trend.Count);
        var data = new StudyReportData(Window, 10800, 10800, 1, 0, 108000, 20, raw.Snapshot(), estimated.Snapshot(), true, []);
        var reply = new HostResponse(1, Guid.NewGuid(), "Succeeded", null, "本机报告", StudyReport: new(Guid.NewGuid(), "version", true, "Report", 300, data));
        Assert.InRange(JsonSerializer.SerializeToUtf8Bytes(reply, Protocol.Json).Length, 1, Protocol.MaxFrameBytes);
        var old = JsonSerializer.Serialize(new HostResponse(1, Guid.NewGuid(), "Succeeded", null, "旧回执"), Protocol.Json);
        Assert.DoesNotContain("studyReport", old); Assert.Null(Protocol.Validate(new(1, Guid.NewGuid(), "study.report.status")));
    }

    private sealed class Capture : INoiseCapture
    {
        public string DeviceName => "Synthetic report microphone";
        public event Action<NoiseFrame>? Frame;
        public event Action<string>? Failed { add { } remove { } }
        public void Start() { }
        public void Emit() => Frame?.Invoke(new(.1, .0001, .02, 0, false));
        public void Dispose() { }
        public void StopAndFlush(Action<NoiseFrame> raw, Action<ReferenceNoiseFrame> reference) => raw(new(.05, .01, .2, 1, false));
    }
    [Fact]
    public async Task Scheduled_collector_matches_canonical_final_statistics_including_tail_and_excludes_manual_capture()
    {
        var clock = new Clock(); var capture = new Capture();
        await using var noise = new NoiseService(_ => capture, () => [], clock);
        var s = noise.Snapshot();
        noise.Handle(new(1, Guid.NewGuid(), "noise.command", Noise: new("select", s.InstanceId, s.Revision, "synthetic-mic")));
        var id = Guid.NewGuid(); Assert.Equal("Accepted", noise.StartScheduled(id).Outcome);
        await Until(() => noise.Snapshot().State == "Active");
        noise.StudyThresholds(id, Policy);
        for (int i = 0; i < 2; i++) { clock.Ms += 100; capture.Emit(); }
        noise.RecordStudyReminder(id); noise.RecordStudyReminder(Guid.NewGuid());
        clock.Ms += 50; noise.StopScheduled(id); await Until(() => !noise.CaptureBusy);
        var canonical = noise.Snapshot().Summary!; var report = noise.StudyReportSnapshot()!;
        Assert.True(report.Complete); Assert.Equal(1, report.DisciplineReminders);
        Assert.Equal(canonical.SampledSeconds, report.Raw.SampledSeconds, 8);
        Assert.Equal(canonical.EnergyMeanDbfs!.Value, report.Raw.EnergyMean!.Value, 8);
        Assert.Equal(canonical.PeakDbfs!.Value, report.Raw.Maximum!.Value, 8);
        Assert.Equal(canonical.ClippedPercent, report.ClippedSeconds / report.Raw.SampledSeconds * 100, 8);
        Assert.Equal(.25, report.Raw.HighSeconds, 8);
        s = noise.Snapshot(); noise.Handle(new(1, Guid.NewGuid(), "noise.command", Noise: new("start", s.InstanceId, s.Revision, "synthetic-mic")));
        await Until(() => noise.Snapshot().State == "Active"); Assert.Null(noise.StudyReportSnapshot());
    }
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task Scheduled_report_keeps_terminal_tail_when_next_capture_replaces_current_session(bool nextScheduled)
    {
        var clock = new Clock(); var capture = new Capture();
        await using var noise = new NoiseService(_ => capture, () => [], clock);
        var n = noise.Snapshot();
        noise.Handle(new(1, Guid.NewGuid(), "noise.command", Noise: new("select", n.InstanceId, n.Revision, "synthetic-mic")));
        var id = Guid.NewGuid(); Assert.Equal("Accepted", noise.StartScheduled(id).Outcome);
        await Until(() => noise.Snapshot().State == "Active");
        noise.StudyThresholds(id, Policy); clock.Ms = 100; capture.Emit();
        var state = Active(id);
        await using var reports = new StudyReportService(() => ("class-a", true, state), noise.StudyReportSnapshot, clock, false,
            finishedStatistics: noise.FinishedStudyReport);
        reports.Snapshot();
        clock.Ms = 150; noise.StopScheduled(id); await Until(() => !noise.CaptureBusy);
        var terminal = noise.StudyReportSnapshot()!;
        Assert.True(terminal.Complete); Assert.Equal(.15, terminal.Raw.SampledSeconds, 8);
        if (nextScheduled) Assert.Equal("Accepted", noise.StartScheduled(Guid.NewGuid()).Outcome);
        else
        {
            n = noise.Snapshot();
            Assert.Equal("Accepted", noise.Handle(new(1, Guid.NewGuid(), "noise.command",
                Noise: new("start", n.InstanceId, n.Revision, "synthetic-mic"))).Outcome);
        }
        await Until(() => noise.Snapshot().State == "Active");
        clock.Ms = 250; capture.Emit(); // This belongs to the new session, never to the old report.
        state = state with { Phase = "Outside", Window = null, SchoolNow = Window.End, Execution = null };
        reports.Snapshot(); clock.Ms += 5000;
        var final = reports.Snapshot();
        Assert.True(final.Data!.Complete);
        Assert.Equal(terminal.Raw.SampledSeconds, final.Data.SampledSeconds, 8);
        Assert.Equal(terminal.Raw.EnergyMean!.Value, final.Data.Raw.EnergyMean!.Value, 8);
        Assert.Equal(terminal.Raw.Maximum, final.Data.Raw.Maximum);
        Assert.Equal(terminal.Frames, final.Data.Frames);
    }
    [Fact]
    public void Discipline_retries_count_only_successful_Host_permission_once()
    {
        var id = Guid.NewGuid(); var s = Active(id); int count = 0;
        var display = new StudyDisplayService(() => ("class-a", true, s), thresholds: (_, _) => ("High", true),
            disciplineClaimed: got => { Assert.Equal(id, got); count++; });
        var state = display.Snapshot();
        var request = new HostRequest(1, Guid.NewGuid(), "study.reminder", StudyReminder:
            new(state.Display!.InstanceId, state.Version!, state.Window!, "Discipline", state.Threshold!.DisciplineId, id));
        Assert.Equal("Succeeded", display.Handle(request).Outcome);
        Assert.Equal("Rejected", display.Handle(request).Outcome); Assert.Equal(1, count);
    }
    private static async Task Until(Func<bool> predicate)
    {
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!predicate()) await Task.Delay(5, stop.Token);
    }
    [Fact]
    public async Task Host_observes_effective_thresholds_without_App_or_camera_but_never_adopts_manual_or_stale_input()
    {
        var id = Guid.NewGuid(); var s = Active(id); int observed = 0;
        await using var reports = new StudyReportService(() => ("class-a", true, s), () => Report(id), background: false,
            observePolicy: (scope, version, session, policy) => { Assert.Equal("class-a", scope); Assert.Equal(s.Version, version); Assert.Equal(id, session); Assert.Equal(Policy, policy); observed++; });
        reports.Snapshot(); Assert.Equal(1, observed);
        s = s with { Execution = s.Execution! with { SignalFresh = false } }; reports.Snapshot(); Assert.Equal(1, observed);
        s = s with { Execution = s.Execution! with { SignalFresh = true, Owner = "Manual" } }; reports.Snapshot(); Assert.Equal(1, observed);
    }
    private sealed class NoSchoolProbe : ILessonStatusReader
    {
        public Task<StatusResult> ReadAsync(StatusQuery query, CancellationToken token) => throw new InvalidOperationException("No actual ClassIsland probes");
    }
    [Fact]
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public async Task Report_is_readable_through_opt_in_named_pipe_without_touching_capture_or_old_ping_wire()
    {
        var clock = new Clock(); var id = Guid.NewGuid(); var state = Active(id);
        await using var reports = new StudyReportService(() => ("class-a", true, state), () => Report(id, true), clock, false);
        reports.Snapshot(); state = state with { Phase = "Outside", Window = null, SchoolNow = Window.End }; reports.Snapshot(); clock.Ms = 5000;
        string pipe = "NPEduTools.Test.study-report." + Guid.NewGuid().ToString("N");
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var running = new PipeServer(pipe, new NoSchoolProbe(), _ => { }, studyReport: reports).RunAsync(stop.Token);
        try
        {
            var ping = await HostClient.RequestAsync(pipe, "host.ping", stop.Token); Assert.Null(ping.StudyReport);
            var reply = await HostClient.RequestAsync(pipe, new HostRequest(1, Guid.NewGuid(), "study.report.status"), stop.Token);
            Assert.Equal("Succeeded", reply.Outcome); Assert.True(reply.StudyReport!.Available);
            Assert.Equal(300, reply.StudyReport.RemainingSeconds); Assert.Equal(3, reply.StudyReport.Data!.DisciplineReminders);
            Assert.Null(reply.Noise); Assert.Null(reply.StudyPhotos); Assert.Null(reply.StudyExit);
        }
        finally { await stop.CancelAsync(); try { await running; } catch (OperationCanceledException) { } }
    }
    [Fact]
    public async Task Active_monitoring_does_not_build_or_transmit_the_full_report_on_every_poll()
    {
        var id = Guid.NewGuid(); var s = Active(id); var clock = new Clock(); int reads = 0;
        await using var reports = new StudyReportService(() => ("class-a", true, s), () => { reads++; return Report(id); }, clock, false);
        for (int i = 0; i < 5; i++) Assert.Null(reports.Snapshot().Data);
        Assert.Equal(1, reads); clock.Ms = 1000; reports.Snapshot(); Assert.Equal(2, reads);
        s = s with { Phase = "Outside", Window = null, SchoolNow = Window.End };
        Assert.NotNull(reports.Snapshot().Data); Assert.Equal(3, reads); // Final tail reads bypass the active polling limit.
    }
}

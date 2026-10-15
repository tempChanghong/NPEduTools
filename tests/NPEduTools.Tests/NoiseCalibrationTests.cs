using System.Text.Json;
using NPEduTools.Contracts;
using NPEduTools.Core;
using NPEduTools.Host;

namespace NPEduTools.Tests;

public sealed partial class NoiseCalibrationTests
{
    private const string Secret = "calibration-admin-2026";
    private const string Scope = "synthetic-school/class/screen";
    private static readonly StudyNoise Policy = new(true, -15, -25, 600, 5);
    private static readonly StudyHostState Study = new(new string('a', 64), "APPLIED", "", "", "Outside", "", true, null, null, null, null);
    [Fact]
    public async Task Background_manual_statistics_resolve_the_same_quick_calibration_as_the_display()
    {
        await using var f = new Fixture(); Assert.Equal("Succeeded", f.Command("begin").Outcome);
        await f.SampleAsync("Acceptable", -55); await f.SampleAsync("Discipline", -20);
        Assert.Equal("Succeeded", f.Command("confirm", medium: -40, high: -30).Outcome);
        var settings = new StudySettings(new("Asia/Shanghai", "2026-10-01", null, [], []), new("StatusTrend", 600),
            new(false, "OwnClass", [], 60), Policy, new(false, 5, 30, 30, true, false));
        await using var manual = new ManualStudyService(f.Noise, () => (Scope, true, Study with { Settings = settings }), () => false,
            Path.Combine(f.DirectoryPath, "manual"), f.Time, false);
        var status = manual.Status();
        Assert.Equal("Succeeded", manual.Handle(new(1, Guid.NewGuid(), "manual.study.command",
            ManualStudy: new("start", status.InstanceId, status.Revision, DeviceId: "mic"))).Outcome);
        await Until(() => f.Noise.Snapshot().State == "Active"); manual.Tick();
        // -20 dBFS crosses the confirmed -30 line, but not the school's -15 fallback.
        for (int i = 0; i < 50; i++) { f.Time.Milliseconds += 100; f.Capture!.Emit(-20); manual.Tick(); }
        var report = f.Noise.StudyReportSnapshot()!;
        Assert.Equal(5, report.Raw.HighSeconds, 6); Assert.Equal(5, report.Raw.MediumSeconds, 6);
        Assert.Equal("Active", f.Noise.Snapshot().State);
    }

    private static NoiseCalibrationSample Sample(string label, double level) =>
        new(Guid.NewGuid(), "NormalStudy", label, 15, 1, level - 1, level, level + 1, 0, "Good");
    [Fact]
    public void Two_natural_scenes_can_suggest_both_lines_without_absolute_silence()
    {
        var s = NoiseQuickCalibration.Suggest([Sample("Acceptable", -50), Sample("Discipline", -25)]);
        Assert.True(s.Ready); Assert.InRange(s.MediumDbfs!.Value, -49, -26);
        Assert.True(s.HighDbfs > s.MediumDbfs);
        Assert.Contains("视觉场景尚未采样", s.Message);
    }
    [Fact]
    public void Three_judgements_and_multiple_samples_use_conservative_edges()
    {
        var s = NoiseQuickCalibration.Suggest([Sample("Acceptable", -60), Sample("Acceptable", -55),
            Sample("Visual", -40), Sample("Discipline", -20)]);
        Assert.True(s.Ready); Assert.Equal(-47.5, s.MediumDbfs); Assert.Equal(-30, s.HighDbfs);
    }
    [Fact]
    public void Overlap_and_missing_judgement_do_not_manufacture_success()
    {
        Assert.False(NoiseQuickCalibration.Suggest([Sample("Acceptable", -40), Sample("Discipline", -39)]).Ready);
        Assert.False(NoiseQuickCalibration.Suggest([Sample("Visual", -40), Sample("Discipline", -20)]).Ready);
        Assert.False(NoiseQuickCalibration.Suggest([Sample("Acceptable", -60), Sample("Visual", -59), Sample("Discipline", -20)]).Ready);
    }
    [Fact]
    public void Below_supported_range_does_not_offer_unconfirmable_thresholds()
    { Assert.False(NoiseQuickCalibration.Suggest([Sample("Acceptable", -155), Sample("Discipline", -130)]).Ready); }
    [Theory]
    [InlineData(0, false, 15, "Good")]
    [InlineData(.01, false, 15, "Clipping")]
    [InlineData(0, true, 15, "Invalid")]
    [InlineData(0, false, 3, "Incomplete")]
    public void Sample_quality_rejects_bad_data(double clipping, bool invalid, double elapsed, string quality)
    {
        var collector = new NoiseQuickCalibration("LowConversation", "Acceptable");
        for (int i = 0; i < 150; i++) collector.Receive(new(.1, .001, .1, clipping, invalid));
        Assert.Equal(quality, collector.Finish(elapsed).Quality);
    }
    private sealed class Clock : TimeProvider
    {
        public long Milliseconds;
        public bool RuntimeTimers;
        public override long TimestampFrequency => 1000;
        public override long GetTimestamp() => Milliseconds;
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) =>
            RuntimeTimers ? base.CreateTimer(callback, state, dueTime, period) : new NoTimer();
        private sealed class NoTimer : ITimer
        { public bool Change(TimeSpan dueTime, TimeSpan period) => true; public void Dispose() { } public ValueTask DisposeAsync() => ValueTask.CompletedTask; }
    }
    [Fact]
    public void Buffered_frame_burst_is_not_a_valid_fifteen_second_observation()
    {
        var collector = new NoiseQuickCalibration("NormalStudy", "Acceptable");
        for (int i = 0; i < 149; i++) collector.Receive(new(.1, .001, .1, 0, false), 0);
        collector.Receive(new(.1, .001, .1, 0, false), 15);
        Assert.Equal("Invalid", collector.Finish(15).Quality);
    }
    private sealed class Capture : INoiseCapture, INoiseCaptureMetadata
    {
        public string DeviceName => "Synthetic microphone";
        public string Signature { get; set; } = "pcm-energy-v1|48000|1|32|float|gain=.7|mute=False";
        public bool Disposed;
        public bool Started;
        public event Action<NoiseFrame>? Frame;
        public event Action<string>? Failed;
        public void Start() => Started = true;
        public void Emit(double dbfs, double clipped = 0) => Frame?.Invoke(new(.1, Math.Pow(10, dbfs / 10), .1, clipped, false));
        public void Unplug() => Failed?.Invoke("Synthetic device lost");
        public void Dispose() => Disposed = true;
    }
    private sealed class Fixture : IAsyncDisposable
    {
        public readonly string DirectoryPath = Path.Combine(Path.GetTempPath(), "npep-calibration-" + Guid.NewGuid());
        public readonly Clock Time = new();
        public readonly NoiseManagementStore Management;
        public readonly NoiseService Noise;
        public Capture? Capture;
        public int Reports, Creates;
        public volatile bool Paused, ThrowPaused, PauseOnOpen;
        public Fixture(bool runtimeTimers = false)
        {
            Time.RuntimeTimers = runtimeTimers;
            Management = new(DirectoryPath, Time); Assert.Null(Management.Configure(null, Secret, false));
            Noise = new(_ => { Creates++; var capture = Capture = new(); if (PauseOnOpen) Paused = true; return capture; },
                () => [new("mic", "Synthetic")], Time, DirectoryPath, Management,
                referencePaused: () => ThrowPaused ? throw new IOException("Synthetic runtime unavailable") : Paused);
            var s = Noise.Snapshot();
            Assert.Equal("Succeeded", Noise.Handle(new(1, Guid.NewGuid(), "noise.command",
                Noise: new("select", s.InstanceId, s.Revision, "mic"))).Outcome);
            Noise.Completed += _ => Reports++;
        }
        public NoiseCalibrationState State => Noise.CalibrationHandle(new(1, Guid.NewGuid(), "noise.calibration.status"), Scope, Study).NoiseCalibration!;
        public HostResponse Command(string action, string? label = null, Guid? sample = null, double? medium = null, double? high = null,
            string secret = Secret, StudyHostState? study = null)
        {
            var s = State;
            return Noise.CalibrationHandle(new(1, Guid.NewGuid(), "noise.calibration.command", NoiseCalibration:
                new(action, s.InstanceId, s.Revision, secret, action == "begin" ? null : s.DraftId,
                    action == "start" ? "LowConversation" : null, label, sample, medium, high)), Scope, study ?? Study);
        }
        public async Task SampleAsync(string label, double level)
        {
            Assert.Equal("Accepted", Command("start", label).Outcome);
            await Until(() => Capture is { Started: true, Disposed: false });
            for (int i = 0; i < 150; i++) { Time.Milliseconds += 100; Capture!.Emit(level); }
            await Until(() => State.State == "Ready");
            Assert.True(Capture!.Disposed);
        }
        public async ValueTask DisposeAsync() { await Noise.DisposeAsync(); Directory.Delete(DirectoryPath, true); }
    }
    private static async Task Until(Func<bool> check)
    { using var timeout = new CancellationTokenSource(5000); while (!check()) await Task.Delay(10, timeout.Token); }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Runtime_pause_or_unreadable_flags_block_quick_status_and_start(bool broken)
    {
        await using var f = new Fixture(); Assert.Equal("Succeeded", f.Command("begin").Outcome);
        f.Paused = !broken; f.ThrowPaused = broken;
        Assert.Equal("Busy", f.State.State);
        Assert.Equal("CALIBRATION_RUNTIME_PAUSED", f.Command("start", "Acceptable").ErrorCode);
        Assert.Equal(0, f.Creates); Assert.Null(f.Capture);
    }

    [Theory]
    [InlineData("begin")]
    [InlineData("start")]
    [InlineData("confirm")]
    [InlineData("clear")]
    public async Task Runtime_pause_preserves_saved_thresholds_and_draft_for_quick_writes(string action)
    {
        await using var f = new Fixture(); f.Command("begin");
        await f.SampleAsync("Acceptable", -55); await f.SampleAsync("Discipline", -20);
        Assert.Equal("Succeeded", f.Command("confirm", medium: -40, high: -30).Outcome);
        string path = Path.Combine(f.DirectoryPath, "noise-quick-calibration.json"); string before = File.ReadAllText(path);
        int creates = f.Creates; f.Paused = true;
        var reply = f.Command(action, label: action == "start" ? "Acceptable" : null,
            medium: action == "confirm" ? -40 : null, high: action == "confirm" ? -30 : null);
        Assert.Equal("CALIBRATION_RUNTIME_PAUSED", reply.ErrorCode);
        Assert.Equal(before, File.ReadAllText(path)); Assert.Equal(creates, f.Creates);
        f.Paused = false; Assert.Equal("Ready", f.State.State); Assert.NotNull(f.State.Profile);
    }

    [Fact]
    public async Task Exam_paused_study_reason_blocks_quick_sampling_even_outside_a_study_window()
    {
        await using var f = new Fixture(); f.Command("begin");
        Assert.Equal("CALIBRATION_RUNTIME_PAUSED", f.Command("start", "Acceptable",
            study: Study with { Reason = "EXAM_PAUSED" }).ErrorCode);
        Assert.Equal(0, f.Creates);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Runtime_pause_during_quick_sampling_cancels_without_ui_polling(bool broken)
    {
        await using var f = new Fixture(runtimeTimers: true); f.Command("begin");
        Assert.Equal("Accepted", f.Command("start", "Acceptable").Outcome);
        await Until(() => f.Capture is { Started: true });
        f.Paused = !broken; f.ThrowPaused = broken;
        await Until(() => f.Capture!.Disposed && !f.Noise.CaptureBusy); // No status capability drives cleanup.
        f.Paused = f.ThrowPaused = false;
        Assert.Equal("Interrupted", Assert.Single(f.State.Samples).Quality); Assert.Equal(0, f.Reports);
    }

    [Fact]
    public async Task Runtime_pause_after_factory_open_prevents_quick_capture_start()
    {
        await using var f = new Fixture(); f.Command("begin"); f.PauseOnOpen = true;
        Assert.Equal("Accepted", f.Command("start", "Acceptable").Outcome);
        await Until(() => f.Capture is { Disposed: true } && !f.Noise.CaptureBusy);
        Assert.False(f.Capture!.Started); f.Paused = false;
        Assert.Equal("Interrupted", Assert.Single(f.State.Samples).Quality); Assert.Equal(0, f.Reports);
    }

    [Fact]
    public async Task Runtime_pause_after_acceptance_is_rechecked_before_creating_a_quick_capture()
    {
        string directory = Path.Combine(Path.GetTempPath(), "npep-calibration-start-guard-" + Guid.NewGuid());
        var management = new NoiseManagementStore(directory); Assert.Null(management.Configure(null, Secret, false));
        int guardReads = 0, creates = 0;
        try
        {
            await using var noise = new NoiseService(_ => { Interlocked.Increment(ref creates); return new Capture(); },
                () => [new("mic", "Synthetic")], directory: directory, management: management,
                referencePaused: () => Interlocked.Increment(ref guardReads) >= 3);
            var initial = noise.Snapshot(); noise.Handle(new(1, Guid.NewGuid(), "noise.command",
                Noise: new("select", initial.InstanceId, initial.Revision, "mic")));
            var begun = noise.CalibrationHandle(new(1, Guid.NewGuid(), "noise.calibration.command", NoiseCalibration:
                new("begin", initial.InstanceId, 0, Secret)), Scope, Study).NoiseCalibration!;
            Assert.Equal("Accepted", noise.CalibrationHandle(new(1, Guid.NewGuid(), "noise.calibration.command", NoiseCalibration:
                new("start", begun.InstanceId, begun.Revision, Secret, begun.DraftId, "LowConversation", "Acceptable")), Scope, Study).Outcome);
            await Until(() => !noise.CaptureBusy);
            Assert.Equal(0, creates); Assert.True(guardReads >= 3);
            var state = noise.CalibrationHandle(new(1, Guid.NewGuid(), "noise.calibration.status"), Scope, Study).NoiseCalibration!;
            Assert.Equal("Interrupted", Assert.Single(state.Samples).Quality);
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task Quick_cancel_remains_available_during_runtime_pause()
    {
        await using var f = new Fixture(); f.Command("begin"); f.Command("start", "Acceptable");
        await Until(() => f.Capture is { Started: true }); f.Paused = true;
        Assert.Equal("Succeeded", f.Command("cancel").Outcome);
        await Until(() => f.Capture!.Disposed && !f.Noise.CaptureBusy); f.Paused = false;
        Assert.Equal("Interrupted", Assert.Single(f.State.Samples).Quality);
    }

    [Fact]
    public async Task Calibration_runtime_guard_does_not_stop_ordinary_manual_monitoring()
    {
        await using var f = new Fixture(runtimeTimers: true); var s = f.Noise.Snapshot();
        Assert.Equal("Accepted", f.Noise.Handle(new(1, Guid.NewGuid(), "noise.command",
            Noise: new("start", s.InstanceId, s.Revision, "mic"))).Outcome);
        await Until(() => f.Noise.Snapshot().State == "Active"); f.Paused = true;
        await Task.Delay(550);
        Assert.False(f.Capture!.Disposed); Assert.Equal("Active", f.Noise.Snapshot().State);
        Assert.Equal("NoiseBusy", f.Command("begin").ErrorCode);
    }

    [Fact]
    public async Task Natural_sampling_confirm_restart_and_device_configuration_fences()
    {
        await using var f = new Fixture();
        Assert.Equal("Succeeded", f.Command("begin").Outcome);
        await f.SampleAsync("Acceptable", -55); await f.SampleAsync("Discipline", -20);
        var suggested = f.State.Suggestion;
        Assert.True(suggested.Ready);
        Assert.Equal("Succeeded", f.Command("confirm", medium: suggested.MediumDbfs, high: suggested.HighDbfs).Outcome);
        Assert.Equal(suggested.MediumDbfs, f.Noise.ResolveCalibration(Scope, Study.Version, Policy).MediumLineDbfs);
        Assert.Equal(Policy, f.Noise.ResolveCalibration("another-school", Study.Version, Policy));
        Assert.Equal(Policy, f.Noise.ResolveCalibration(Scope, new string('b', 64), Policy));
        Assert.Equal(0, f.Reports);
        string json = File.ReadAllText(Path.Combine(f.DirectoryPath, "noise-quick-calibration.json"));
        Assert.DoesNotContain(Secret, json); Assert.DoesNotContain("audio", json, StringComparison.OrdinalIgnoreCase);
        await using var restarted = new NoiseService(_ => new Capture(), () => [], f.Time, f.DirectoryPath, f.Management);
        var read = restarted.CalibrationHandle(new(1, Guid.NewGuid(), "noise.calibration.status"), Scope, Study).NoiseCalibration!;
        Assert.Equal(2, read.Samples.Count); Assert.NotNull(read.Profile); Assert.False(read.Applied);
        Assert.Equal(Policy, restarted.ResolveCalibration(Scope, Study.Version, Policy)); // No fresh capture signature yet.
        Assert.Equal("Succeeded", f.Command("clear").Outcome);
        Assert.Equal(Policy, f.Noise.ResolveCalibration(Scope, Study.Version, Policy));
    }
    [Fact]
    public async Task Manual_threshold_confirmation_is_possible_when_natural_samples_overlap()
    {
        await using var f = new Fixture(); f.Command("begin");
        await f.SampleAsync("Acceptable", -40); await f.SampleAsync("Discipline", -39);
        Assert.False(f.State.Suggestion.Ready);
        Assert.Equal("Succeeded", f.Command("confirm", medium: -35, high: -25).Outcome);
    }
    [Fact]
    public async Task Cancellation_and_schedule_preemption_release_capture_without_report_or_skip()
    {
        await using var f = new Fixture(); f.Command("begin");
        Assert.Equal("Accepted", f.Command("start", "Acceptable").Outcome);
        await Until(() => f.Capture is { Started: true, Disposed: false });
        Assert.Equal("Succeeded", f.Command("cancel").Outcome);
        await Until(() => f.State.State == "Ready");
        Assert.Equal("Interrupted", Assert.Single(f.State.Samples).Quality);
        f.Command("remove", sample: f.State.Samples[0].Id);
        f.Command("start", "Discipline"); await Until(() => f.Capture is { Started: true, Disposed: false });
        int skipped = 0; f.Noise.ManualStopping = _ => { skipped++; return true; };
        Assert.Equal("NoiseBusy", f.Noise.StartScheduled(Guid.NewGuid()).ErrorCode);
        await Until(() => f.State.State == "Ready");
        Assert.Equal("Interrupted", Assert.Single(f.State.Samples).Quality);
        Assert.Equal(0, skipped); Assert.Equal(0, f.Reports);
        Assert.Equal("Accepted", f.Noise.StartScheduled(Guid.NewGuid()).Outcome);
        await Until(() => f.Noise.Snapshot().State == "Active");
        Assert.Equal("NoiseBusy", f.Command("start", "Acceptable").ErrorCode);
        Assert.Equal("MANAGEMENT_REQUIRED", f.Noise.Handle(new(1, Guid.NewGuid(), "noise.command",
            Noise: new("stop", f.Noise.Snapshot().InstanceId, f.Noise.Snapshot().Revision))).ErrorCode);
    }
    [Fact]
    public async Task Gain_change_and_unplug_do_not_create_good_samples()
    {
        await using var f = new Fixture(); f.Command("begin"); f.Command("start", "Acceptable");
        await Until(() => f.Capture is { Started: true, Disposed: false });
        f.Time.Milliseconds += 100; f.Capture!.Emit(-40);
        f.Capture.Signature += "|changed"; f.Time.Milliseconds += 100; f.Capture.Emit(-40);
        await Until(() => f.State.State == "Ready"); Assert.Equal("Interrupted", f.State.Samples[0].Quality);
        f.Command("begin"); f.Command("start", "Acceptable");
        await Until(() => f.Capture is { Started: true, Disposed: false });
        f.Capture!.Unplug(); await Until(() => f.State.State == "Ready");
        Assert.Equal("Interrupted", f.State.Samples[0].Quality); Assert.Equal(0, f.Reports);
    }
    [Fact]
    public async Task Relabel_delete_wrong_password_old_revision_and_invalid_lines_are_rejected_or_saved_correctly()
    {
        await using var f = new Fixture();
        Assert.Equal("MANAGEMENT_SECRET_INVALID", f.Command("begin", secret: "wrong-secret").ErrorCode);
        f.Command("begin"); await f.SampleAsync("Acceptable", -50);
        var before = f.State;
        Assert.Equal("Succeeded", f.Command("label", label: "Visual", sample: before.Samples[0].Id).Outcome);
        Assert.Equal("Visual", f.State.Samples[0].Label);
        Assert.Equal("CALIBRATION_CHANGED", f.Noise.CalibrationHandle(new(1, Guid.NewGuid(), "noise.calibration.command",
            NoiseCalibration: new("cancel", before.InstanceId, before.Revision, Secret, before.DraftId)), Scope, Study).ErrorCode);
        Assert.Equal("InvalidNoiseCalibrationCommand", f.Command("confirm", medium: -20, high: -30).ErrorCode);
        Assert.Equal("CALIBRATION_SCHEDULE_BUSY", f.Command("begin", study: Study with { Phase = "Preparing" }).ErrorCode);
        f.Command("remove", sample: before.Samples[0].Id); Assert.Empty(f.State.Samples);
    }
    [Fact]
    public async Task Corrupt_storage_remains_untouched_and_cannot_apply_thresholds()
    {
        await using var f = new Fixture();
        string path = Path.Combine(f.DirectoryPath, "noise-quick-calibration.json");
        File.WriteAllText(path, "{corrupt");
        await using var reader = new NoiseService(_ => new Capture(), () => [], directory: f.DirectoryPath, management: f.Management);
        var r = reader.CalibrationHandle(new(1, Guid.NewGuid(), "noise.calibration.status"), Scope, Study);
        Assert.Equal("CALIBRATION_STORE_UNAVAILABLE", r.NoiseCalibration!.Error);
        Assert.Equal(Policy, reader.ResolveCalibration(Scope, Study.Version, Policy));
        Assert.Equal("{corrupt", File.ReadAllText(path));
    }

    [Theory]
    [InlineData("null")]
    [InlineData("{\"version\":2,\"draft\":null,\"profile\":null}")]
    [InlineData("{\"version\":1,\"draft\":{\"id\":\"00000000-0000-0000-0000-000000000000\",\"deviceId\":\"mic\",\"context\":null,\"signature\":null,\"samples\":[]},\"profile\":null}")]
    [InlineData("oversize")]
    public async Task Structurally_invalid_quick_store_is_reported_without_throwing_or_replacing_original(string content)
    {
        await using var f = new Fixture();
        if (content == "oversize") content = new string('x', 128 * 1024 + 1);
        string path = Path.Combine(f.DirectoryPath, "noise-quick-calibration.json");
        File.WriteAllText(path, content);
        var state = f.State;
        Assert.Equal("CALIBRATION_STORE_UNAVAILABLE", state.Error); Assert.Null(state.Profile);
        Assert.Equal("CALIBRATION_STORE_UNAVAILABLE", f.Command("begin").ErrorCode);
        Assert.Equal(Policy, f.Noise.ResolveCalibration(Scope, Study.Version, Policy));
        Assert.Equal(content, File.ReadAllText(path)); Assert.Null(f.Capture);
    }

    [Fact]
    public async Task Quick_sampling_does_not_publish_formal_state_or_persist_a_report_checkpoint()
    {
        await using var f = new Fixture();
        var transport = new NoiseTransport(f.Noise, Path.Combine(f.DirectoryPath, "npep")); transport.Bind(Scope);
        f.Command("begin"); Assert.Equal("Accepted", f.Command("start", "Acceptable").Outcome);
        await Until(() => f.Capture is { Started: true });
        f.Time.Milliseconds += 100; f.Capture!.Emit(-40);
        var wire = transport.Observe();
        Assert.Equal("Idle", wire["state"]!.GetValue<string>()); Assert.Null(wire["sessionId"]); Assert.Null(wire["summary"]);
        var outbox = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(Path.Combine(f.DirectoryPath, "npep", "noise-outbox.json")))!;
        Assert.Null(outbox["checkpoint"]); Assert.Empty(transport.Reports());
        Assert.True(f.Noise.CaptureBusy);
        f.Command("cancel"); await Until(() => !f.Noise.CaptureBusy);
        Assert.Null(f.Noise.Snapshot().SessionId); Assert.Empty(transport.Reports()); Assert.Equal(0, f.Reports);
    }

    [Fact]
    public async Task Actual_scheduler_preempts_quick_sampling_instead_of_retaining_it_as_manual_monitoring()
    {
        await using var f = new Fixture(); f.Command("begin"); f.Command("start", "Acceptable");
        await Until(() => f.Capture is { Started: true }); var quick = f.Capture!;
        var connection = Guid.NewGuid(); var bridge = Guid.NewGuid(); long sequence = 0;
        var schoolBase = new DateTimeOffset(2026, 10, 1, 19, 0, 0, TimeSpan.Zero);
        await using var scheduler = new NoiseScheduleService(f.Noise, () => new(connection, bridge, ++sequence, 0,
            schoolBase.AddMilliseconds(f.Time.Milliseconds), 0, "Advancing", "synthetic school time"), () => false,
            Path.Combine(f.DirectoryPath, "scheduler"), f.Time, background: false);
        scheduler.Bind(Scope, true);
        scheduler.Confirm(System.Text.Json.Nodes.JsonNode.Parse("""
            {"policy":{"version":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","source":"Grade",
            "rules":[{"days":[4],"start":"19:00","end":"21:30"}]},"leaseSeconds":86400,"confirmed":true,"command":null}
            """)!.AsObject());
        for (int i = 0; i < 3; i++) { f.Time.Milliseconds += 500; scheduler.Tick(); }
        await Until(() => quick.Disposed);
        Assert.Equal("Interrupted", Assert.Single(f.State.Samples).Quality);
        await Until(() => { f.Time.Milliseconds += 100; scheduler.Tick(); return f.Noise.Snapshot().State == "Active" && scheduler.Observe()["owner"]!.GetValue<string>()=="Schedule"; });
        Assert.Equal("Schedule", scheduler.Observe()["owner"]!.GetValue<string>()); Assert.Equal(0, f.Reports);
    }
    [Fact]
    public async Task Confirmed_lines_drive_scheduled_bands_and_five_second_reminder_with_fallback_on_gain_change()
    {
        await using var f = new Fixture(); f.Command("begin");
        await f.SampleAsync("Acceptable", -60); await f.SampleAsync("Discipline", -20);
        Assert.Equal("Succeeded", f.Command("confirm", medium: -50, high: -35).Outcome);
        Assert.Equal("Accepted", f.Noise.StartScheduled(Guid.NewGuid()).Outcome);
        await Until(() => f.Noise.Snapshot().State == "Active");
        var session = f.Noise.Snapshot().SessionId!.Value;
        var settings = new StudySettings(new("Asia/Shanghai", "2026-10-12", null, [], []),
            new("StatusTrend", 180), new(false, "OwnClass", [], 60), Policy, new(false, 5, 30, 30, false, false));
        var start = new DateTimeOffset(2026, 10, 12, 19, 0, 0, TimeSpan.Zero);
        var state = Study with { Settings = settings, Phase = "Active", SchoolNow = start.AddMinutes(10),
            Window = new(start, start.AddMinutes(150)),
            Execution = new("Schedule", "Active", session, "WINDOW_ACTIVE", true, null, null, true) };
        var display = new StudyDisplayService(() => (Scope, true, state), f.Time, f.Noise.StudyThresholds,
            f.DirectoryPath, f.Noise.ResolveCalibration);
        display.Snapshot(); f.Time.Milliseconds += 100; f.Capture!.Emit(-45);
        Assert.Equal("Medium", display.Snapshot().Threshold!.Band);
        f.Time.Milliseconds += 100; f.Capture.Emit(-30);
        Assert.Equal("High", display.Snapshot().Threshold!.Band);
        Assert.Null(display.Snapshot().Threshold!.DisciplineId);
        for (int i = 0; i < 49; i++) { f.Time.Milliseconds += 100; f.Capture.Emit(-30); }
        var eligible = display.Snapshot();
        Assert.NotNull(eligible.Threshold!.DisciplineId);
        var claim = new HostRequest(1, Guid.NewGuid(), "study.reminder", StudyReminder:
            new(eligible.Display!.InstanceId, eligible.Version!, eligible.Window!, "Discipline", eligible.Threshold.DisciplineId, session));
        Assert.Equal("Succeeded", display.Handle(claim).Outcome);
        f.Capture.Signature += "|gain-changed"; f.Time.Milliseconds += 100; f.Capture.Emit(-30);
        Assert.Equal("None", display.Snapshot().Threshold!.Band); // Restore school's -25 / -15 lines.
        Assert.Equal(Policy, f.Noise.ResolveCalibration(Scope, Study.Version, Policy));
    }
    [Fact]
    public async Task Failed_atomic_confirmation_does_not_activate_new_profile_or_replace_file()
    {
        await using var f = new Fixture(); f.Command("begin");
        await f.SampleAsync("Acceptable", -55); await f.SampleAsync("Discipline", -20);
        string path = Path.Combine(f.DirectoryPath, "noise-quick-calibration.json");
        string before = File.ReadAllText(path);
        using (var blocked = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            Assert.Equal("CALIBRATION_STORE_UNAVAILABLE", f.Command("confirm", medium: -40, high: -30).ErrorCode);
            Assert.Null(f.State.Profile);
        }
        Assert.Equal(before, File.ReadAllText(path));
        Assert.Equal(Policy, f.Noise.ResolveCalibration(Scope, Study.Version, Policy));
    }
    [Fact]
    public async Task Host_restart_requires_new_instance_and_fresh_microphone_metadata_before_applying_saved_profile()
    {
        await using var f = new Fixture(); f.Command("begin");
        await f.SampleAsync("Acceptable", -55); await f.SampleAsync("Discipline", -20);
        f.Command("confirm", medium: -40, high: -30);
        var old = f.State; Capture? capture = null;
        await using var restarted = new NoiseService(_ => capture = new(), () => [new("mic", "Synthetic")], f.Time, f.DirectoryPath, f.Management);
        Assert.Equal("CALIBRATION_CHANGED", restarted.CalibrationHandle(new(1, Guid.NewGuid(), "noise.calibration.command",
            NoiseCalibration: new("clear", old.InstanceId, old.Revision, Secret, old.DraftId)), Scope, Study).ErrorCode);
        Assert.Equal("Accepted", restarted.StartScheduled(Guid.NewGuid()).Outcome);
        await Until(() => restarted.Snapshot().State == "Active");
        Assert.Equal(-40, restarted.ResolveCalibration(Scope, Study.Version, Policy).MediumLineDbfs);
        f.Time.Milliseconds += 100; capture!.Signature += "|format-changed"; capture.Emit(-20);
        Assert.Equal(Policy, restarted.ResolveCalibration(Scope, Study.Version, Policy));
    }
    [Fact]
    public async Task Pipe_uses_real_host_and_returns_statistics_without_secret_or_raw_audio()
    {
        if (!OperatingSystem.IsWindows()) return;
        await using var f = new Fixture();
        string pipe = "NPEduTools.Test.quick-calibration." + Guid.NewGuid().ToString("N");
        using var lifetime = new CancellationTokenSource();
        var server = new PipeServer(pipe, new ForbiddenReader(), _ => { }, noise: f.Noise);
        var serving = server.RunAsync(lifetime.Token);
        try
        {
            var read = await HostClient.RequestAsync(pipe, "noise.calibration.status", lifetime.Token);
            var s = read.NoiseCalibration!;
            var begun = await HostClient.RequestAsync(pipe, new HostRequest(1, Guid.NewGuid(), "noise.calibration.command",
                NoiseCalibration: new("begin", s.InstanceId, s.Revision, Secret)), lifetime.Token);
            Assert.Equal("Succeeded", begun.Outcome);
            Assert.DoesNotContain(Secret, JsonSerializer.Serialize(begun, Protocol.Json));
            var old = await HostClient.RequestAsync(pipe, "noise.status", lifetime.Token);
            Assert.Null(old.NoiseCalibration); Assert.NotNull(old.Noise);
        }
        finally { lifetime.Cancel(); try { await serving; } catch (OperationCanceledException) { } }
    }
    private sealed class ForbiddenReader : ILessonStatusReader
    { public Task<StatusResult> ReadAsync(StatusQuery query, CancellationToken cancellationToken) => throw new InvalidOperationException(); }
}

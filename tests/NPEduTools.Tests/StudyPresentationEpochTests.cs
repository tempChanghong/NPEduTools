using System.Reflection;
using NPEduTools.Contracts;
using NPEduTools.Core;
using NPEduTools.Host;

namespace NPEduTools.Tests;

public sealed class StudyPresentationEpochTests
{
    private sealed class Clock : TimeProvider
    {
        public long Ms;
        public override long TimestampFrequency => 1000;
        public override long GetTimestamp() => Ms;
        public override DateTimeOffset GetUtcNow() => new DateTimeOffset(2026, 10, 23, 11, 0, 0, TimeSpan.Zero).AddMilliseconds(Ms);
    }
    private sealed class Fixture
    {
        public readonly Clock Time = new();
        public StudyHostState State;
        public readonly StudyDisplayService Service;
        public Fixture()
        {
            var start = new DateTimeOffset(2026, 10, 23, 19, 15, 0, TimeSpan.Zero);
            State = new(new string('a', 64), "APPLIED", "NOT_INTEGRATED", "ACTIVE", "Active", "WINDOW_ACTIVE", true,
                start.AddMinutes(10), new(start, start.AddMinutes(135)), null,
                new(new("Asia/Shanghai", "2026-10-23", null, [], []), new("StatusTrend", 180),
                    new(false, "OwnClass", [], 60), new(true, -40, -50, 600, 5), new(false, 3, 30, 30, true, false)));
            Service = new(() => ("school/class", true, State), Time, inactivity: () => TimeSpan.Zero);
        }
        public StudyPresentationEpoch Epoch()
        {
            var reply = Service.Handle(new(1, Guid.NewGuid(), "study.presentation.epoch"));
            Assert.Equal("Succeeded", reply.Outcome);
            return Assert.IsType<StudyPresentationEpoch>(reply.PresentationEpoch);
        }
        public HostRequest Pulse(Guid? epoch, string state = "Hidden", Guid? app = null, long sequence = 1)
        {
            var current = Service.Snapshot();
            return new(1, Guid.NewGuid(), "study.presentation", StudyPresentation: new(current.Display!.InstanceId,
                app ?? Guid.NewGuid(), sequence, current.Version!, current.Window!, current.Phase, state,
                state == "Visible" ? 100 : null, epoch));
        }
        public string Shown => Service.ObservePresentation()!["status"]!["state"]!.GetValue<string>();
        public int Count => ((System.Collections.IDictionary)typeof(StudyDisplayService)
            .GetField("_presentations", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(Service)!).Count;
    }

    [Fact]
    public void One_hundred_sequential_App_rebuilds_recover_without_restarting_display()
    {
        var f = new Fixture(); var instance = f.Service.Snapshot().Display!.InstanceId;
        HostRequest? first = null; int rotations = 0;
        for (int i = 0; i < 100; i++)
        {
            f.Time.Ms += 6000;
            var pulse = f.Pulse(f.Epoch().Epoch, "Visible"); first ??= pulse;
            var reply = f.Service.Handle(pulse);
            if (reply.ErrorCode == "STUDY_OBSERVATION_CHANGED")
            {
                rotations++;
                Assert.Equal("Unknown", f.Shown);
                var next = f.Epoch();
                pulse = f.Pulse(next.Epoch, "Visible", pulse.StudyPresentation!.AppInstanceId, 2);
                reply = f.Service.Handle(pulse);
            }
            Assert.Equal("Succeeded", reply.Outcome);
            Assert.Equal("Visible", f.Shown);
            Assert.Equal(instance, f.Service.Snapshot().Display!.InstanceId);
            Assert.InRange(f.Count, 1, 16);
        }
        Assert.Equal(6, rotations);
        Assert.Equal("STUDY_OBSERVATION_CHANGED", f.Service.Handle(first!).ErrorCode);
    }

    [Fact]
    public void Fresh_concurrent_clients_remain_bounded_and_keep_valid_visibility()
    {
        var f = new Fixture(); var epoch = f.Epoch().Epoch;
        for (int i = 0; i < 16; i++) Assert.Equal("Succeeded", f.Service.Handle(f.Pulse(epoch, "Visible")).Outcome);
        f.Time.Ms = 4899;
        Assert.Equal("STUDY_CLIENT_LIMIT", f.Service.Handle(f.Pulse(epoch)).ErrorCode);
        Assert.Equal(epoch, f.Epoch().Epoch); Assert.Equal("Visible", f.Shown); Assert.Equal(16, f.Count);
        f.Time.Ms = 4900;
        Assert.Equal("STUDY_OBSERVATION_CHANGED", f.Service.Handle(f.Pulse(epoch)).ErrorCode);
        Assert.NotEqual(epoch, f.Epoch().Epoch); Assert.Equal("Unknown", f.Shown);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void One_persistent_App_does_not_block_ninety_nine_successive_rebuilds(bool legacySurvivor)
    {
        var f = new Fixture(); var firstEpoch = f.Epoch(); var survivor = Guid.NewGuid();
        Guid? survivorEpoch = legacySurvivor ? null : firstEpoch.Epoch;
        Assert.Equal("Succeeded", f.Service.Handle(f.Pulse(survivorEpoch, "Visible", survivor)).Outcome);
        HostRequest? retired = null;
        for (int i = 1; i <= 99; i++)
        {
            f.Time.Ms += 6000;
            Assert.Equal("Succeeded", f.Service.Handle(f.Pulse(survivorEpoch, "Visible", survivor, i + 1)).Outcome);
            var pulse = f.Pulse(f.Epoch().Epoch, "Hidden"); retired ??= pulse;
            var reply = f.Service.Handle(pulse);
            if (reply.ErrorCode == "STUDY_OBSERVATION_CHANGED")
            {
                Assert.Equal("Visible", f.Shown); // A live observer never disappears during recycling.
                var next = f.Epoch().Epoch;
                Assert.Equal("STUDY_OBSERVATION_CHANGED", f.Service.Handle(pulse).ErrorCode);
                Assert.Equal(next, f.Epoch().Epoch); // Replaying the trigger cannot rotate again.
                reply = f.Service.Handle(f.Pulse(next, "Hidden", pulse.StudyPresentation!.AppInstanceId, 2));
            }
            Assert.Equal("Succeeded", reply.Outcome);
            Assert.Equal("Visible", f.Shown); Assert.InRange(f.Count, 2, 16);
            Assert.Equal(firstEpoch.InstanceId, f.Service.Snapshot().Display!.InstanceId);
            Assert.True(f.Service.Snapshot().Display!.Available);
        }
        Assert.Equal("STUDY_OBSERVATION_CHANGED", f.Service.Handle(retired!).ErrorCode);
        var latestEpoch = f.Epoch().Epoch; Assert.NotEqual(firstEpoch.Epoch, latestEpoch);
        Assert.Equal("Succeeded", f.Service.Handle(f.Pulse(latestEpoch, "Visible", survivor, 101)).Outcome);
        // A surviving App can adopt the current generation, but cannot go back afterwards.
        Assert.Equal("STUDY_OBSERVATION_CHANGED", f.Service.Handle(f.Pulse(survivorEpoch, "Visible", survivor, 102)).ErrorCode);
        Assert.Equal("Visible", f.Shown);
    }

    [Fact]
    public void Late_legacy_and_old_epoch_packets_cannot_revive_or_rotate_the_new_epoch()
    {
        var f = new Fixture(); var epoch = f.Epoch().Epoch; var legacy = f.Pulse(null);
        Assert.Equal("Succeeded", f.Service.Handle(legacy).Outcome);
        for (int i = 1; i < 16; i++) f.Service.Handle(f.Pulse(epoch));
        f.Time.Ms = 5000;
        var trigger = f.Pulse(epoch);
        Assert.Equal("STUDY_OBSERVATION_CHANGED", f.Service.Handle(trigger).ErrorCode);
        var next = f.Epoch().Epoch;
        for (int i = 0; i < 20; i++)
        {
            Assert.Equal("STUDY_OBSERVATION_CHANGED", f.Service.Handle(trigger).ErrorCode);
            Assert.Equal("STUDY_OBSERVATION_CHANGED", f.Service.Handle(legacy with
            { StudyPresentation = legacy.StudyPresentation! with { Sequence = 100 + i } }).ErrorCode);
            Assert.Equal(next, f.Epoch().Epoch);
        }
        Assert.Equal(0, f.Count); Assert.Equal("Unknown", f.Shown);
        var fresh = f.Pulse(next, "Visible"); Assert.Equal("Succeeded", f.Service.Handle(fresh).Outcome);
        f.Time.Ms += 4900;
        Assert.Equal("Suppressed", f.Service.Handle(fresh).Outcome);
        Assert.Equal(next, f.Epoch().Epoch); Assert.Equal("Unknown", f.Shown);
    }

    [Fact]
    public void Recycling_observations_preserves_temporary_hide_and_display_identity()
    {
        var f = new Fixture(); var state = f.Service.Snapshot(); var epoch = f.Epoch().Epoch;
        Assert.Equal("Succeeded", f.Service.Handle(new(1, Guid.NewGuid(), "study.hide",
            StudyHide: new(state.Display!.InstanceId, state.Version!, state.Window!))).Outcome);
        for (int i = 0; i < 16; i++) f.Service.Handle(f.Pulse(epoch));
        f.Time.Ms = 5000;
        Assert.Equal("STUDY_OBSERVATION_CHANGED", f.Service.Handle(f.Pulse(epoch)).ErrorCode);
        var next = f.Epoch(); Assert.Equal(state.Display.InstanceId, next.InstanceId);
        Assert.Equal("Succeeded", f.Service.Handle(f.Pulse(next.Epoch)).Outcome);
        var hidden = f.Service.Snapshot();
        Assert.False(hidden.Display!.Available); Assert.True(hidden.Display.HiddenRemainingSeconds > 0);
        Assert.Equal("ACTIVE", hidden.CaptureState); Assert.Equal("Hidden", f.Shown);
    }

    [Fact]
    public void Read_only_handshake_does_not_refresh_observations_or_emit_fields_on_legacy_status()
    {
        var f = new Fixture(); var epoch = f.Epoch();
        var pulse = f.Pulse(epoch.Epoch, "Visible"); f.Service.Handle(pulse); f.Time.Ms = 4900;
        for (int i = 0; i < 20; i++) Assert.Equal(epoch, f.Epoch());
        Assert.Equal("Unknown", f.Shown); Assert.Equal(1, f.Count);
        Assert.Null(f.Service.Handle(new(1, Guid.NewGuid(), "study.status")).PresentationEpoch);
        Assert.Null(f.Service.Handle(pulse).PresentationEpoch);
        f.State = f.State with { Window = new(f.State.Window!.Start.AddDays(1), f.State.Window.End.AddDays(1)) };
        Assert.NotEqual(epoch.InstanceId, f.Epoch().InstanceId);
        Assert.Equal("STUDY_STATE_CHANGED", f.Service.Handle(pulse).ErrorCode);
        Assert.Equal("Succeeded", f.Service.Handle(f.Pulse(null)).Outcome);
    }

    private sealed class NeverRead : ILessonStatusReader
    {
        public Task<StatusResult> ReadAsync(StatusQuery query, CancellationToken token) =>
            throw new InvalidOperationException("No physical school bridge allowed");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Actual_Host_pipe_routes_both_epoch_capabilities_without_starting_capture(bool manual)
    {
        if (!OperatingSystem.IsWindows()) return;
        var f = new Fixture();
        await using var study = new StudySettingsService(() => throw new InvalidOperationException("No clock bridge allowed"), () => false, f.Time, false);
        await using var noise = new NoiseService(_ => throw new InvalidOperationException("No microphone capture allowed"),
            () => [new("synthetic", "Synthetic")], f.Time);
        string folder = Path.Combine(Path.GetTempPath(), "presentation-epoch-" + Guid.NewGuid().ToString("N"));
        await using var manualStudy = new ManualStudyService(noise, () => (null, false, f.State), () => false, folder, f.Time, false);
        if (manual)
        {
            var initial = manualStudy.Status(); var start = f.Time.GetUtcNow().AddMinutes(1);
            Assert.Equal("Succeeded", manualStudy.Handle(new(1, Guid.NewGuid(), "manual.study.command",
                ManualStudy: new("start", initial.InstanceId, initial.Revision, DeviceId: "synthetic", StartUtc: start, EndUtc: start.AddHours(1)))).Outcome);
        }
        string pipe = "NPEduTools.Test.presentation-epoch." + Guid.NewGuid().ToString("N");
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        var server = new PipeServer(pipe, new NeverRead(), _ => { }, study: study, studyDisplay: f.Service, manualStudy: manualStudy).RunAsync(stop.Token);
        try
        {
            string capability = manual ? "manual.study.presentation" : "study.presentation";
            var read = await HostClient.RequestAsync(pipe, capability + ".epoch", stop.Token);
            Assert.Equal("Succeeded", read.Outcome); Assert.NotNull(read.PresentationEpoch);
            var state = manual ? manualStudy.Display.Snapshot() : f.Service.Snapshot();
            Assert.Equal(state.Display!.InstanceId, read.PresentationEpoch.InstanceId);
            var pulse = new StudyPresentationPulse(state.Display.InstanceId, Guid.NewGuid(), 1, state.Version!, state.Window!,
                state.Phase, "Visible", 0, read.PresentationEpoch.Epoch);
            var reply = await HostClient.RequestAsync(pipe, new HostRequest(1, Guid.NewGuid(), capability, StudyPresentation: pulse), stop.Token);
            Assert.Equal("Succeeded", reply.Outcome); Assert.Null(reply.PresentationEpoch);
            var old = await HostClient.RequestAsync(pipe, new HostRequest(1, Guid.NewGuid(), capability, StudyPresentation: pulse with
                { AppInstanceId = Guid.NewGuid(), ObservationEpoch = null }), stop.Token);
            Assert.Equal("Succeeded", old.Outcome); Assert.Null(old.PresentationEpoch);
            Assert.Null(noise.Snapshot().SessionId);
        }
        finally { await stop.CancelAsync(); try { await server; } catch (OperationCanceledException) { } }
    }
}

using System.Text.Json.Nodes;
using NPEduTools.Contracts;
using NPEduTools.Core;
using NPEduTools.Host;
using NPEduTools.Integrations.Npep;

namespace NPEduTools.Tests;

public sealed class ManualStudyDeadlineTests
{
    private sealed class Clock : TimeProvider
    {
        public long Ms;
        public override long TimestampFrequency => 1000;
        public override long GetTimestamp() => Interlocked.Read(ref Ms);
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.Parse("2026-10-13T10:00:00Z").AddMilliseconds(GetTimestamp());
        public readonly TaskCompletionSource<Timer> Pending = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new Timer(callback, state);
            Pending.TrySetResult(timer);
            return timer;
        }
        public sealed class Timer(TimerCallback callback, object? state) : ITimer
        {
            private int _disposed;
            public void Fire() { if (Volatile.Read(ref _disposed) == 0) callback(state); }
            public bool Change(TimeSpan dueTime, TimeSpan period) => Volatile.Read(ref _disposed) == 0;
            public void Dispose() => Interlocked.Exchange(ref _disposed, 1);
            public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
        }
    }
    private sealed class Capture : INoiseCapture
    {
        public string DeviceName => "Synthetic deadline capture";
        public bool Disposed;
        public event Action<NoiseFrame>? Frame;
        public event Action<string>? Failed;
        public void Start() { }
        public void Emit() => Frame?.Invoke(new(.1, .0001, .02, 0, false));
        public void Fault() => Failed?.Invoke("Synthetic capture fault");
        public void Dispose() => Disposed = true;
    }

    [Theory]
    [InlineData(true, "watch", "PlannedEnd")]
    [InlineData(false, "watch", "ThreeHourLimit")]
    [InlineData(true, "manual", "PlannedEnd")]
    [InlineData(false, "manual", "ThreeHourLimit")]
    [InlineData(true, "user", "UserEnded")]
    [InlineData(true, "fault", "CaptureFault")]
    [InlineData(true, "takeover", "ScheduledTakeover")]
    public async Task EndReasonAndSavedReportDoNotDependOnWhichDeadlineLoopRunsFirst(bool planned, string trigger, string expected)
    {
        string directory = Path.Combine(Path.GetTempPath(), "manual-study-deadline-" + Guid.NewGuid());
        var clock = new Clock(); var capture = new Capture();
        var noise = new NoiseService(_ => capture, () => [new("mic", "Synthetic")], clock);
        // Match Host subscription order: the upload outbox observes completion before the manual UI owner.
        var transport = new NoiseTransport(noise, Path.Combine(directory, "transport"));
        transport.Bind("synthetic-school-scope");
        var manual = new ManualStudyService(noise,
            () => (null, false, new(null, "UNAVAILABLE", "", "", "Outside", "", false, null, null, null, null)),
            () => false, directory, clock, background: false);
        try
        {
            long seconds = planned ? 60 : 10800;
            var initial = manual.Status();
            var start = new HostRequest(1, Guid.NewGuid(), "manual.study.command", ManualStudy:
                new("start", initial.InstanceId, initial.Revision, DeviceId: "mic", EndUtc: planned ? clock.GetUtcNow().AddSeconds(seconds) : null));
            Assert.Equal("Succeeded", manual.Handle(start).Outcome);
            await Until(() => noise.Snapshot().State == "Active");
            var timer = await clock.Pending.Task.WaitAsync(TimeSpan.FromSeconds(5));
            capture.Emit(); manual.Tick();
            Interlocked.Exchange(ref clock.Ms, (trigger is "user" or "fault" or "takeover" ? seconds - 1 : seconds) * 1000);
            switch (trigger)
            {
                case "watch": timer.Fire(); break; // Deliberately do not call ManualStudyService.Tick.
                case "manual": manual.Tick(); break;
                case "user":
                    var active = manual.Status();
                    Assert.Equal("Succeeded", manual.Handle(new(1, Guid.NewGuid(), "manual.study.command", ManualStudy:
                        new("end", active.InstanceId, active.Revision, active.SessionId))).Outcome);
                    break;
                case "fault": capture.Fault(); break;
                default: noise.SetScheduledPriority(true); break;
            }
            await Until(() => manual.Status().State == "Ended" && !noise.CaptureBusy);
            Assert.Equal(expected, manual.Status().Reason);
            Assert.True(capture.Disposed);
            var upload = Assert.Single(transport.Reports());
            Assert.Equal("Manual", upload!["sessionKind"]!.GetValue<string>());
            // The established wire format labels unrequested faults Interrupted;
            // the local manual report explains them as CaptureFault.
            Assert.Equal(trigger == "fault" ? "Interrupted" : expected, upload["endReason"]!.GetValue<string>());
            Assert.Equal(trigger == "fault" ? "Faulted" : "Stopped", upload["outcome"]!.GetValue<string>());
            NpepNoiseProtocol.Validate("report", upload.AsObject());
            var saved = JsonNode.Parse(File.ReadAllText(Assert.Single(Directory.GetFiles(directory, "*.json"))))!;
            Assert.Equal(expected, saved["reason"]!.GetValue<string>());
            Assert.Contains(manual.Report().Data!.Notes, n => n.Contains(expected switch
            {
                "PlannedEnd" => "指定结束时间", "ThreeHourLimit" => "三小时上限", "CaptureFault" => "异常结束",
                "ScheduledTakeover" => "定时自习接管", _ => "主动结束"
            }));
        }
        finally
        {
            await manual.DisposeAsync(); await noise.DisposeAsync();
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }
    private static async Task Until(Func<bool> condition)
    {
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!condition()) await Task.Delay(5, stop.Token);
    }
}

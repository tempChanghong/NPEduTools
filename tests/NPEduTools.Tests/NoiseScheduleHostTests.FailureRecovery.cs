using System.Text.Json.Nodes;

namespace NPEduTools.Tests;

public sealed partial class NoiseScheduleHostTests
{
    [Theory]
    [InlineData(false, false, false)] [InlineData(false, false, true)]
    [InlineData(true, false, false)] [InlineData(true, false, true)]
    [InlineData(true, true, false)] [InlineData(true, true, true)]
    public async Task Changing_microphone_before_the_next_schedule_tick_blocks_only_the_failed_device(bool study, bool exam, bool restart)
    {
        await using var f = new Fixture();
        JsonObject? config = study ? StudyConfig(f) : null;
        f.ModeExam = exam;
        if (config is not null)
        {
            config["effective"]!["settings"]!["examSchedule"] = config["effective"]!["settings"]!["schedule"]!.DeepClone();
            f.Tick();
            f.Scheduler.SelectStudy(config, f.Scope);
            var grant = Grant(config); if (exam) grant["mode"] = "Exam";
            f.Scheduler.ConfirmStudyExecution(config, grant);
        }
        await f.Start();
        var originalSession = f.Noise.Snapshot().SessionId;
        f.Captures[0].Break();
        await Until(() => f.Noise.Snapshot().State == "Faulted");
        // The UI can select a replacement as soon as cleanup finishes, before the 500 ms scheduler poll.
        await Until(() => f.Noise.Handle(f.Command("select", "replacement")).Outcome == "Succeeded");
        f.Tick();
        Assert.NotEqual("WINDOW_FAILED", f.Scheduler.Observe()["reason"]!.GetValue<string>());
        if (restart)
        {
            await f.Scheduler.DisposeAsync();
            f.Scheduler = f.NewScheduler(); f.Scheduler.Bind(f.Scope, true);
            f.Tick();
            if (config is null) f.Scheduler.Confirm(f.Policy());
            else
            {
                f.Scheduler.SelectStudy(config, f.Scope);
                var grant = Grant(config); if (exam) grant["mode"] = "Exam";
                f.Scheduler.ConfirmStudyExecution(config, grant);
            }
        }
        await Until(() => { f.Tick(); return f.Captures.Count >= 2 && f.Noise.Snapshot().State == "Active"; });
        var recovered = f.Noise.Snapshot();
        Assert.Equal("replacement", recovered.DeviceId);
        Assert.NotEqual(originalSession, recovered.SessionId);
        f.Captures[^1].Sample(); f.Tick();
        Assert.Equal("WINDOW_ACTIVE", f.Scheduler.Observe()["reason"]!.GetValue<string>());

        // The failed original remains suppressed, including after reloading the persisted schedule journal.
        f.Noise.StopScheduled(recovered.SessionId!.Value);
        await Until(() => f.Noise.Snapshot().State == "Stopped");
        await Until(() => f.Noise.Handle(f.Command("select", "synthetic")).Outcome == "Succeeded");
        f.Tick();
        Assert.Equal("WINDOW_FAILED", f.Scheduler.Observe()["reason"]!.GetValue<string>());
        f.Tick(); Assert.All(f.Captures, capture => Assert.True(capture.Disposed));
    }
}

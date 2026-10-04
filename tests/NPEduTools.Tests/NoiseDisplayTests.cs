using System.Text.Json.Nodes;
using NPEduTools.Contracts;
using NPEduTools.Host;

namespace NPEduTools.Tests;

public sealed class NoiseDisplayTests
{
    private sealed class Clock : TimeProvider
    {
        public long Ms;
        public DateTimeOffset Utc = DateTimeOffset.Parse("2026-10-04T11:00:00Z");
        public override long TimestampFrequency => 1000;
        public override long GetTimestamp() => Ms;
        public override DateTimeOffset GetUtcNow() => Utc;
        public void Advance(int seconds) { Ms += seconds * 1000; Utc = Utc.AddSeconds(seconds); }
    }
    private sealed class Fixture : IDisposable
    {
        public readonly string Directory = Path.Combine(Path.GetTempPath(), "noise-display-" + Guid.NewGuid());
        public readonly Clock Clock = new();
        public SchoolNoiseWindow? Window = new(DateTimeOffset.Parse("2026-10-04T19:00:00Z"), DateTimeOffset.Parse("2026-10-04T20:00:00Z"));
        public NoiseState Noise = new(Guid.NewGuid(), 1, "Active", "synthetic", "test", "synthetic", DateTimeOffset.UtcNow,
            -57, "Good", null, [], SessionId: Guid.NewGuid());
        public bool Exam;
        public NoiseDisplayService Service;
        public Fixture() { Service = Create(); Service.Bind("test-school-binding"); }
        public NoiseDisplayService Create() => new(() => (Noise, Window), () => Exam, Directory, Clock);
        public void Confirm(string state = "UNKNOWN", double age = 0, int minutes = 10, double remaining = 0, Guid? id = null) =>
            Service.Confirm(Window!, Noise.InstanceId, Noise.SessionId!.Value, state, age, minutes, remaining, id);
        public HostRequest Return(Guid? id = null) => new(1, id ?? Guid.NewGuid(), "noise.display.return",
            NoiseDisplay: new("return", Noise.InstanceId, Noise.SessionId!.Value));
        public void Dispose() { if (System.IO.Directory.Exists(Directory)) System.IO.Directory.Delete(Directory, true); }
    }
    [Fact]
    public void Unknown_capability_does_not_misdiagnose_an_old_service_as_closed_browser()
    {
        using var f = new Fixture(); f.Service.Snapshot(); f.Clock.Advance(60);
        Assert.Equal("NEGOTIATING", f.Service.Snapshot().Phase);
        f.Service.Failed("DISPLAY_UNSUPPORTED"); Assert.False(f.Service.Snapshot().Fallback);
        f.Confirm(); f.Clock.Advance(21); Assert.True(f.Service.Snapshot().Fallback);
    }
    [Fact]
    public void Missing_heartbeats_and_hidden_page_have_a_bounded_grace_without_renewal()
    {
        using var f = new Fixture(); f.Confirm("DISPLAY_VISIBLE"); f.Clock.Advance(14);
        Assert.Equal("WEB_VISIBLE", f.Service.Snapshot().Phase);
        f.Clock.Advance(7); Assert.True(f.Service.Snapshot().Fallback);
        f.Confirm("HIDDEN"); f.Clock.Advance(10); f.Confirm("HIDDEN");
        Assert.False(f.Service.Snapshot().Fallback);
        f.Clock.Advance(11); f.Confirm("HIDDEN"); Assert.True(f.Service.Snapshot().Fallback);
    }
    [Fact]
    public void Fresh_editor_notification_blocking_suppresses_but_stale_blocking_expires()
    {
        using var f = new Fixture(); f.Service.Snapshot(); f.Clock.Advance(60);
        f.Confirm("BLOCKED"); Assert.Equal("BLOCKED", f.Service.Snapshot().Phase);
        f.Clock.Advance(21); Assert.True(f.Service.Snapshot().Fallback);
        f.Confirm("DISPLAY_VISIBLE"); Assert.False(f.Service.Snapshot().Fallback);
    }
    [Fact]
    public void Returns_use_monotonic_time_and_repeated_closes_cannot_extend_them()
    {
        using var f = new Fixture(); f.Confirm(minutes: 5);
        var request = f.Return(); Assert.Null(Protocol.Validate(request));
        Assert.Equal("Succeeded", f.Service.Handle(request).Outcome);
        Assert.Equal("Active", f.Noise.State); // The display service has no microphone mutation dependency.
        f.Clock.Advance(60); f.Clock.Utc = f.Clock.Utc.AddDays(-2);
        Assert.Equal(240, f.Service.Snapshot().ReturnRemainingSeconds);
        f.Service.Handle(f.Return()); Assert.Equal(240, f.Service.Snapshot().ReturnRemainingSeconds);
        f.Clock.Advance(241); f.Service.Handle(request); Assert.Equal(0, f.Service.Snapshot().ReturnRemainingSeconds);
    }
    [Fact]
    public void Capture_replacement_in_same_window_does_not_renew_return_and_stale_request_rejected()
    {
        using var f = new Fixture(); f.Confirm(); var old = f.Return(); f.Service.Handle(old); f.Clock.Advance(40);
        f.Noise = f.Noise with { SessionId = Guid.NewGuid(), Revision = 2 };
        Assert.Equal(560, f.Service.Snapshot().ReturnRemainingSeconds);
        Assert.Equal("DISPLAY_STATE_CHANGED", f.Service.Handle(old).ErrorCode);
        f.Confirm(remaining: 600, id: Guid.NewGuid()); Assert.Equal(560, f.Service.Snapshot().ReturnRemainingSeconds);
    }
    [Fact]
    public void Brief_capture_gap_and_wall_clock_adjustment_do_not_reanchor_monotonic_return()
    {
        using var f = new Fixture(); f.Confirm(); f.Service.Handle(f.Return()); f.Clock.Advance(40);
        f.Noise = f.Noise with { State = "Stopped" }; Assert.Equal("INACTIVE", f.Service.Snapshot().Phase);
        f.Clock.Utc = f.Clock.Utc.AddSeconds(-30);
        f.Noise = f.Noise with { State = "Active", SessionId = Guid.NewGuid() };
        Assert.Equal(560, f.Service.Snapshot().ReturnRemainingSeconds);
    }
    [Fact]
    public void Changed_admin_minutes_do_not_corrupt_an_existing_longer_server_lease_cache()
    {
        using var f = new Fixture(); f.Confirm(); f.Service.Handle(f.Return()); f.Clock.Advance(30);
        var body = f.Service.ObserveDisplay()!; var id = f.Service.PendingReturn()!["requestId"]!.GetValue<string>();
        var reply = new JsonObject { ["supported"] = true, ["serverNow"] = "2026-10-04T11:00:30.000Z", ["returnMinutes"] = 1,
            ["presence"] = new JsonObject { ["state"] = "RETURNING", ["ageMs"] = 0 },
            ["activeReturn"] = new JsonObject { ["requestId"] = id, ["window"] = body["window"]!.DeepClone(),
                ["startedAt"] = "2026-10-04T11:00:00.000Z", ["expiresAt"] = "2026-10-04T11:10:00.000Z", ["returnMinutes"] = 10, ["remainingSeconds"] = 570 } };
        f.Service.ConfirmDisplay(body, reply, 0); Assert.Equal(1, f.Service.Snapshot().ReturnMinutes);
        f.Service = f.Create(); f.Service.Bind("test-school-binding");
        Assert.Null(f.Service.Snapshot().ErrorCode); Assert.Equal(570, f.Service.Snapshot().ReturnRemainingSeconds);
    }
    [Fact]
    public void School_return_reply_cannot_extend_an_already_running_local_deadline()
    {
        using var f = new Fixture(); f.Confirm(); f.Service.Handle(f.Return()); f.Clock.Advance(50);
        f.Confirm(remaining: 600, id: Guid.NewGuid()); Assert.Equal(550, f.Service.Snapshot().ReturnRemainingSeconds);
        f.Confirm(remaining: 30, id: Guid.NewGuid()); Assert.Equal(30, f.Service.Snapshot().ReturnRemainingSeconds);
    }
    [Fact]
    public void Restart_restores_pending_original_intent_and_wall_clock_rollback_discards_it()
    {
        using var f = new Fixture(); f.Confirm(); var request = f.Return(); f.Service.Handle(request);
        f.Clock.Advance(40); f.Service = f.Create(); f.Service.Bind("test-school-binding");
        Assert.Equal(560, f.Service.Snapshot().ReturnRemainingSeconds);
        Assert.Equal(request.RequestId.ToString("D"), f.Service.PendingReturn()!["requestId"]!.GetValue<string>());
        f.Clock.Utc = f.Clock.Utc.AddDays(-1); f.Service = f.Create(); f.Service.Bind("test-school-binding");
        Assert.Equal(0, f.Service.Snapshot().ReturnRemainingSeconds);
    }
    [Fact]
    public void Exam_end_and_new_binding_do_not_revive_old_return()
    {
        using var f = new Fixture(); f.Confirm(); f.Service.Handle(f.Return()); f.Exam = true;
        Assert.Equal("INACTIVE", f.Service.Snapshot().Phase); Assert.Null(f.Service.PendingReturn());
        f.Exam = false; f.Service.Bind("different-class"); f.Clock.Advance(60);
        Assert.False(f.Service.Snapshot().Fallback); Assert.Equal(0, f.Service.Snapshot().ReturnRemainingSeconds);
        f.Window = null; Assert.Equal("INACTIVE", f.Service.Snapshot().Phase); // Manual capture has no owned window.
    }
    [Fact]
    public void Corrupt_journal_is_preserved_and_return_is_rejected_without_stopping_capture()
    {
        using var f = new Fixture(); System.IO.Directory.CreateDirectory(f.Directory);
        string file = Path.Combine(f.Directory, "noise-display-return.json"); File.WriteAllText(file, "{broken}");
        f.Service = f.Create(); f.Service.Bind("test-school-binding"); f.Confirm();
        Assert.Equal("DISPLAY_STORE_UNAVAILABLE", f.Service.Handle(f.Return()).ErrorCode);
        Assert.Equal("{broken}", File.ReadAllText(file)); Assert.Equal("Active", f.Noise.State);
    }
    [Fact]
    public void Late_reply_from_previous_window_or_session_cannot_suppress_new_display()
    {
        using var f = new Fixture(); f.Confirm(); f.Clock.Advance(21); var old = f.Service.ObserveDisplay()!;
        var reply = new JsonObject { ["supported"] = true, ["serverNow"] = "2026-10-04T11:00:00.000Z", ["returnMinutes"] = 10,
            ["presence"] = new JsonObject { ["state"] = "DISPLAY_VISIBLE", ["ageMs"] = 0 }, ["activeReturn"] = null };
        f.Noise = f.Noise with { SessionId = Guid.NewGuid() };
        f.Service.ConfirmDisplay(old, reply, 0); Assert.True(f.Service.Snapshot().Fallback);
    }
    [Fact]
    public void Null_journal_written_during_scope_changes_is_readable_on_restart()
    {
        using var f = new Fixture(); f.Confirm(); f.Service.Handle(f.Return()); f.Service.Bind(null);
        f.Service = f.Create(); f.Service.Bind("test-school-binding"); f.Confirm();
        Assert.Null(f.Service.Snapshot().ErrorCode);
    }
    [Fact]
    public void Journal_write_failure_does_not_silently_accept_or_hide_display()
    {
        using var f = new Fixture(); f.Confirm(); f.Clock.Advance(21);
        System.IO.Directory.CreateDirectory(f.Directory);
        System.IO.Directory.CreateDirectory(Path.Combine(f.Directory, "noise-display-return.json.tmp"));
        Assert.Equal("Rejected", f.Service.Handle(f.Return()).Outcome);
        Assert.Equal(0, f.Service.Snapshot().ReturnRemainingSeconds);
        Assert.True(f.Service.Snapshot().Fallback);
    }
    [Fact]
    public void Expired_offline_registration_is_consumed_once_without_creating_new_return()
    {
        using var f = new Fixture(); f.Confirm(minutes: 1); var request = f.Return(); f.Service.Handle(request); f.Clock.Advance(61);
        var body = f.Service.ObserveDisplay()!;
        body["requestId"] = request.RequestId.ToString("D"); body["offlineStartedAt"] = "2026-10-04T11:00:00.000Z";
        var reply = new JsonObject { ["supported"] = true, ["serverNow"] = "2026-10-04T11:01:01.000Z", ["returnMinutes"] = 1,
            ["presence"] = new JsonObject { ["state"] = "UNKNOWN", ["ageMs"] = null },
            ["activeReturn"] = new JsonObject { ["requestId"] = request.RequestId.ToString("D"), ["window"] = body["window"]!.DeepClone(),
                ["startedAt"] = "2026-10-04T11:00:00.000Z", ["expiresAt"] = "2026-10-04T11:01:00.000Z", ["returnMinutes"] = 1, ["remainingSeconds"] = 0 } };
        f.Service.ConfirmDisplay(body, reply, 0); Assert.Null(f.Service.PendingReturn());
        Assert.Equal(0, f.Service.Snapshot().ReturnRemainingSeconds);
        f.Service.Handle(request); Assert.Equal(0, f.Service.Snapshot().ReturnRemainingSeconds);
        f.Service = f.Create(); f.Service.Bind("test-school-binding"); Assert.Null(f.Service.Snapshot().ErrorCode);
    }
}

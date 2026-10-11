using NPEduTools.Core;

namespace NPEduTools.Tests;

public sealed class GuardWakeTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "NPEduTools.GuardWakeTests", Guid.NewGuid().ToString("N"));
    private readonly Clock _clock = new();
    private readonly GuardRegistration _registration = new(Guid.NewGuid(), new(10, 100), null);
    private readonly Processes _processes = new();
    private readonly GuardFiles _files;
    private readonly GuardSupervisor _guard;
    public GuardWakeTests()
    {
        _files = new(_directory); _files.Write("registration.json", _registration);
        _guard = new(_files, _registration, _processes, _clock);
    }
    private GuardWakePlan Plan(params GuardWakeWindow[] windows) => new(_registration.Generation, new(20, 200), _clock.Utc, windows);
    private void Publish(GuardWakePlan plan) => _files.Write("wake.json", plan);
    [Fact]
    public async Task Normal_manual_exit_during_preparation_does_not_skip_the_upcoming_school_window()
    {
        var end=_clock.Utc.AddHours(1);
        await using var host=new GuardHostSession(_files,()=>false,(gen,p)=>new(gen,p,_clock.Utc,[new(_clock.Utc.AddMinutes(1),end)],end));
        Assert.True(host.ExpectedStop(false));Assert.Null(_files.Read<GuardExpectedExit>("expected-exit.json")!.SkipUntilUtc);
        _guard.Tick(0,true);_guard.Tick(2,true);Assert.True(Assert.Single(_processes.Starts));Assert.Null(_files.Read<GuardRecoveryEvent>("recovery.json"));
    }
    [Fact]
    public void Armed_crash_writes_one_incident_across_both_process_restarts_but_planned_wake_does_not()
    {
        var host=new GuardProcess(20,200);_processes.Alive.Add(host);_processes.Alive.Add(_registration.App);
        var end=_clock.Utc.AddHours(1);Publish(Plan(new GuardWakeWindow(_clock.Utc,end)) with{CurrentEndUtc=end,ContextKey=new string('a',64)});
        _files.Write("lease.json",new GuardLease(_registration.Generation,host,1,true));_guard.Tick(0,true);
        _processes.Alive.Clear();_guard.Tick(1,true);_guard.Tick(3,true);
        var incident=_files.Read<GuardRecoveryEvent>("recovery.json");Assert.NotNull(incident);Assert.Equal(host,incident.Host);Assert.Equal(_registration.App,incident.App);
        _guard.Tick(4,true);_guard.Tick(9,true);Assert.Equal(incident.Id,_files.Read<GuardRecoveryEvent>("recovery.json")!.Id);Assert.Equal(new[]{true,false},_processes.Starts);
    }
    [Fact]
    public void Expected_stop_and_preparation_wake_never_claim_abnormal_recovery()
    {
        var end=_clock.Utc.AddHours(1);Publish(Plan(new GuardWakeWindow(_clock.Utc,end)) with{CurrentEndUtc=end,ContextKey=new string('a',64)});
        _guard.Tick(0,true);_guard.Tick(2,true);Assert.Null(_files.Read<GuardRecoveryEvent>("recovery.json"));
        _files.Write("expected-exit.json",new GuardExpectedExit(_registration.Generation,new(20,200),end));
        _guard.Tick(10,true);Assert.Null(_files.Read<GuardRecoveryEvent>("recovery.json"));
    }
    [Fact]
    public void Empty_standby_survives_both_processes_exiting_and_does_not_restart_manual_capture()
    {
        Assert.True(_guard.Tick(0, true)); Assert.True(_guard.Tick(5000, true));
        Assert.Empty(_processes.Starts); Assert.Equal("Watching", _files.Read<GuardStatus>("status.json")!.Phase);
    }
    [Fact]
    public void Preparation_boundary_wakes_app_and_host_independently_and_end_is_exclusive()
    {
        var start = _clock.Utc.AddMinutes(5); var end = start.AddHours(1);
        Publish(Plan(new GuardWakeWindow(start, end))); _guard.Tick(0, true); Assert.Empty(_processes.Starts);
        _clock.Utc = start.AddMinutes(-2).AddTicks(-1); _guard.Tick(1, true); Assert.Empty(_processes.Starts);
        _clock.Utc = start.AddMinutes(-2); _guard.Tick(2, true); _guard.Tick(4, true);
        Assert.Equal(new[] { true }, _processes.Starts);
        _guard.Tick(5, true); _guard.Tick(10, true); Assert.Equal(new[] { true, false }, _processes.Starts);
        _processes.Alive.Clear(); _clock.Utc = end; _guard.Tick(11, true); _guard.Tick(40, true);
        Assert.Equal(2, _processes.Starts.Count);
    }
    [Fact]
    public void Verified_exit_suppresses_current_window_across_guard_reload_but_next_window_wakes()
    {
        var start = _clock.Utc; var end = start.AddHours(1); var next = start.AddDays(1);
        Publish(Plan(new GuardWakeWindow(start, end), new GuardWakeWindow(next, next.AddHours(1))));
        _files.Write("expected-exit.json", new GuardExpectedExit(_registration.Generation, new(20, 200), end));
        var reload = new GuardSupervisor(_files, _registration, _processes, _clock);
        Assert.True(reload.Tick(0, true)); reload.Tick(30, true); Assert.Empty(_processes.Starts);
        _clock.Utc = next.AddMinutes(-2); reload.Tick(100, true); reload.Tick(102, true);
        Assert.True(Assert.Single(_processes.Starts)); Assert.False(_files.Stopped(_registration.Generation));
    }
    [Fact]
    public void Published_revocation_clears_previous_hint_before_process_death()
    {
        _processes.Alive.Add(new(20, 200)); Publish(Plan(new GuardWakeWindow(_clock.Utc, _clock.Utc.AddHours(1)))); _guard.Tick(0, true);
        Publish(Plan()); _guard.Tick(1, true); _processes.Alive.Clear(); _guard.Tick(2, true); _guard.Tick(20, true);
        Assert.Empty(_processes.Starts);
    }
    [Fact]
    public void A_resident_guard_wakes_next_window_after_observing_a_normal_host_exit()
    {
        var host = new GuardProcess(20, 200); _processes.Alive.Add(host);
        _files.Write("lease.json", new GuardLease(_registration.Generation, host, 1, false));
        var start = _clock.Utc; var end = start.AddHours(1); var next = start.AddDays(1);
        Publish(Plan(new GuardWakeWindow(start, end), new GuardWakeWindow(next, next.AddHours(1)))); _guard.Tick(0, true);
        _files.Write("expected-exit.json", new GuardExpectedExit(_registration.Generation, host, end)); _processes.Alive.Clear();
        _guard.Tick(1, true); _guard.Tick(20, true); Assert.Empty(_processes.Starts);
        _clock.Utc = next.AddMinutes(-2); _guard.Tick(100, true); _guard.Tick(101, true); _guard.Tick(102, true);
        Assert.True(Assert.Single(_processes.Starts));
        _guard.Tick(103, true); _guard.Tick(108, true); Assert.Equal(new[] { true, false }, _processes.Starts);
    }
    [Fact]
    public void A_schedule_hint_cannot_revive_after_suspend_without_fresh_host_observation()
    {
        Publish(Plan(new GuardWakeWindow(_clock.Utc, _clock.Utc.AddHours(1)))); _guard.Tick(0, true); _guard.Suspend();
        _guard.Tick(2, true); _guard.Tick(10, true); Assert.Empty(_processes.Starts);
    }
    [Fact]
    public void Power_resume_allows_next_scheduled_wake_without_reviving_a_pre_sleep_capture_lease()
    {
        Publish(Plan(new GuardWakeWindow(_clock.Utc, _clock.Utc.AddHours(1))));
        _guard.Tick(0, true); _guard.Suspend(); _guard.Resume(); _guard.Tick(2, true); _guard.Tick(4, true);
        Assert.True(Assert.Single(_processes.Starts));
        // A resume with no schedule hint never restores the old armed lease.
        var policy = new GuardRecoveryPolicy(); policy.Observe(new(_registration.Generation, new(20, 200), 1, true), 0);
        policy.Suspend(); Assert.False(policy.MayRestart(true, true, 3));
    }
    [Fact]
    public void Continuous_failures_have_a_cooldown_instead_of_a_permanent_three_attempt_limit()
    {
        _processes.Fail = true; Publish(Plan(new GuardWakeWindow(_clock.Utc, _clock.Utc.AddHours(1))));
        foreach (int second in new[] { 0, 2, 3, 8, 9, 24, 25, 100, 308 }) _guard.Tick(second, true);
        Assert.Equal(3, _processes.Starts.Count);
        _guard.Tick(324, true); _guard.Tick(326, true); Assert.Equal(4, _processes.Starts.Count);
    }
    [Fact]
    public void Corrupt_foreign_or_future_issued_cache_cannot_trigger_a_launch()
    {
        Publish(Plan(new GuardWakeWindow(_clock.Utc, _clock.Utc.AddHours(1))) with { Generation = Guid.NewGuid() }); _guard.Tick(0, true);
        Publish(Plan(new GuardWakeWindow(_clock.Utc, _clock.Utc.AddHours(1))) with { IssuedUtc = _clock.Utc.AddHours(1) }); _guard.Tick(10, true);
        Publish(Plan(new GuardWakeWindow(_clock.Utc, _clock.Utc.AddHours(4)))); _guard.Tick(20, true); _guard.Tick(30, true);
        Assert.Empty(_processes.Starts);
    }
    [Fact]
    public async Task Expected_stop_write_failure_is_rejected_and_does_not_disable_heartbeat()
    {
        await using var host = new GuardHostSession(_files, () => true);
        Directory.CreateDirectory(Path.Combine(_directory, "expected-exit.json"));
        Assert.False(host.ExpectedStop()); Assert.True(_files.Read<GuardLease>("lease.json")!.Armed);
        Assert.False(_files.Stopped(_registration.Generation));
    }
    public void Dispose() => Directory.Delete(_directory, true);
    private sealed class Clock : TimeProvider
    { public DateTimeOffset Utc = new(2026, 10, 12, 11, 0, 0, TimeSpan.Zero); public override DateTimeOffset GetUtcNow() => Utc; }
    private sealed class Processes : IGuardProcesses
    {
        public readonly HashSet<GuardProcess> Alive = [];
        public readonly List<bool> Starts = [];
        public bool Fail;
        public bool Matches(GuardProcess p, bool app) => Alive.Contains(p);
        public bool IsAlive(GuardProcess p) => Alive.Contains(p);
        public GuardProcess Start(bool app)
        {
            Starts.Add(app); if (Fail) throw new IOException("Synthetic launcher failure.");
            var p = new GuardProcess(30 + Starts.Count, 300 + Starts.Count); Alive.Add(p); return p;
        }
    }
}

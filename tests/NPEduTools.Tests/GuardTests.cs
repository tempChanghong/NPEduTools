using NPEduTools.Core;

namespace NPEduTools.Tests;

public sealed class GuardTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "NPEduTools.GuardTests", Guid.NewGuid().ToString("N"));
    private readonly GuardRegistration _registration = new(Guid.NewGuid(), new(10, 100), null);
    private readonly FakeProcesses _processes = new();
    private readonly GuardFiles _files;
    private readonly GuardSupervisor _supervisor;
    public GuardTests()
    {
        _files = new(_directory); _files.Write("registration.json", _registration);
        _processes.Alive.Add(_registration.App); _processes.Alive.Add(new(20, 200));
        _supervisor = new(_files, _registration, _processes);
    }
    private void Lease(bool armed, long sequence = 1, GuardProcess? host = null, Guid? generation = null) =>
        _files.Write("lease.json", new GuardLease(generation ?? _registration.Generation, host ?? new(20, 200), sequence, armed));
    private GuardStatus Status => _files.Read<GuardStatus>("status.json")!;

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void Only_actual_scheduled_lease_can_restore_missing_host(bool armed)
    {
        Lease(armed); _supervisor.Tick(0, true); _processes.Alive.Remove(new(20, 200));
        _supervisor.Tick(1, true); _supervisor.Tick(3, true);
        Assert.Equal(armed ? 1 : 0, _processes.Launches.Count);
        if (armed) Assert.False(_processes.Launches[0]);
    }
    [Theory]
    [InlineData("Maintenance")] [InlineData("Upgrade")] [InlineData("WindowsSessionEnding")] [InlineData("HostNormalStop")]
    public void Expected_stop_survives_process_death_and_guard_reload(string reason)
    {
        Lease(true); _supervisor.Tick(0, true); _files.Stop(_registration.Generation, reason);
        _processes.Alive.Clear();
        Assert.False(_supervisor.Tick(2, true)); Assert.Empty(_processes.Launches);
        Assert.False(new GuardSupervisor(_files, _registration, _processes).Tick(4, true));
        Assert.Equal("Stopped", Status.Phase);
    }
    [Fact]
    public void Old_sequence_does_not_keep_a_dead_session_armed_forever()
    {
        Lease(true); _supervisor.Tick(0, true); _supervisor.Tick(29, true);
        _processes.Alive.Remove(new(20, 200)); _supervisor.Tick(31, true); _supervisor.Tick(40, true);
        Assert.Empty(_processes.Launches);
    }
    [Fact]
    public void Disarmed_lease_cancels_pending_recovery_and_late_armed_heartbeat_cannot_revive_it()
    {
        Lease(true, 10); _supervisor.Tick(0, true); _processes.Alive.Remove(_registration.App);
        _supervisor.Tick(1, true); // Recovery is due at second 3.
        Lease(false, 11); _supervisor.Tick(2, true);
        Lease(true, 10); _supervisor.Tick(3, true); _supervisor.Tick(20, true);
        Assert.Empty(_processes.Launches); Assert.Equal("Watching", Status.Phase);
    }
    [Fact]
    public void Late_lease_from_dead_previous_host_cannot_rearm_the_replacement()
    {
        Lease(true); _supervisor.Tick(0, true); _processes.Alive.Remove(new(20, 200));
        _supervisor.Tick(1, true); _supervisor.Tick(3, true);
        var replacement = _processes.Last;
        Lease(false, host: replacement); _supervisor.Tick(4, true);
        Lease(true, 999, new(20, 200)); _supervisor.Tick(5, true);
        _processes.Alive.Remove(replacement); _supervisor.Tick(6, true); _supervisor.Tick(20, true);
        Assert.Single(_processes.Launches); Assert.Equal("Watching", Status.Phase);
    }
    [Fact]
    public void Suspend_discards_permission_and_requires_new_host_lease()
    {
        Lease(true); _supervisor.Tick(0, true); _supervisor.Suspend();
        _processes.Alive.Remove(new(20, 200)); _supervisor.Tick(1, true); _supervisor.Tick(5, true);
        Assert.Empty(_processes.Launches);
        Lease(true, 2); _supervisor.Tick(6, true); _supervisor.Tick(8, true);
        Assert.Single(_processes.Launches);
    }
    [Fact]
    public void Locked_desktop_does_not_launch_and_unlock_starts_new_backoff()
    {
        Lease(true); _supervisor.Tick(0, true); _processes.Alive.Remove(new(20, 200));
        _supervisor.Tick(1, false); _supervisor.Tick(5, false); Assert.Empty(_processes.Launches);
        _supervisor.Tick(6, true); _supervisor.Tick(7, true); Assert.Empty(_processes.Launches);
        _supervisor.Tick(8, true); Assert.Single(_processes.Launches);
    }
    [Fact]
    public void Live_or_hung_process_is_not_replaced()
    { Lease(true); for (int i = 0; i < 20; i++) _supervisor.Tick(i, true); Assert.Empty(_processes.Launches); }
    [Fact]
    public void Invalid_process_identity_and_other_generation_cannot_arm_recovery()
    {
        Lease(true, host: new(999, 999)); _supervisor.Tick(0, true);
        Lease(true, generation: Guid.NewGuid()); _processes.Alive.Remove(new(20, 200));
        _supervisor.Tick(1, true); _supervisor.Tick(5, true); Assert.Empty(_processes.Launches);
    }
    [Fact]
    public void New_host_disarmed_lease_does_not_inherit_permission_to_capture()
    {
        Lease(true); _supervisor.Tick(0, true); _processes.Alive.Remove(new(20, 200));
        _supervisor.Tick(1, true); _supervisor.Tick(3, true);
        Lease(false, host: _processes.Last); _supervisor.Tick(4, true);
        _processes.Alive.Remove(_processes.Last); _supervisor.Tick(6, true); _supervisor.Tick(20, true);
        Assert.Single(_processes.Launches); Assert.Equal("Watching", Status.Phase);
    }
    [Fact]
    public void Three_failed_attempts_are_bounded_and_backed_off()
    {
        _processes.Fail = true; Lease(true); _supervisor.Tick(0, true); _processes.Alive.Remove(new(20, 200));
        _supervisor.Tick(1, true); _supervisor.Tick(2, true); Assert.Empty(_processes.Launches);
        _supervisor.Tick(3, true); Assert.Single(_processes.Launches);
        _supervisor.Tick(4, true); _supervisor.Tick(8, true); Assert.Single(_processes.Launches);
        _supervisor.Tick(9, true); Assert.Equal(2, _processes.Launches.Count);
        _supervisor.Tick(10, true); _supervisor.Tick(24, true); Assert.Equal(2, _processes.Launches.Count);
        _supervisor.Tick(25, true); _supervisor.Tick(26, true); Assert.Equal(3, _processes.Launches.Count);
        Assert.Equal("RecoveryLimit", Status.Phase);
    }
    [Fact]
    public void App_death_recovers_app_without_launching_another_live_host()
    {
        Lease(true); _supervisor.Tick(0, true); _processes.Alive.Remove(_registration.App);
        _supervisor.Tick(1, true); _supervisor.Tick(3, true);
        Assert.True(Assert.Single(_processes.Launches));
    }
    [Fact]
    public void A_new_registration_generation_ends_the_old_guard()
    {
        Lease(true); _supervisor.Tick(0, true);
        _files.Write("registration.json", _registration with { Generation = Guid.NewGuid() });
        _processes.Alive.Clear(); Assert.False(_supervisor.Tick(1, true)); Assert.Empty(_processes.Launches);
    }
    [Fact]
    public void App_session_query_pauses_launch_and_cancel_keeps_guard_available()
    {
        Lease(true); _supervisor.Tick(0, true);
        _files.SessionQuery(_registration.Generation, true); // Same shared path as WPF OnSessionEnding.
        _processes.Alive.Remove(new(20, 200));
        _supervisor.Tick(1, true); _supervisor.Tick(5, true);
        Assert.Empty(_processes.Launches); Assert.False(_files.Stopped(_registration.Generation));
        _supervisor.CancelSessionEnd(); _supervisor.Tick(6, true); _supervisor.Tick(8, true);
        Assert.Single(_processes.Launches);
    }
    [Fact]
    public async Task Host_publishes_disarm_and_expected_stop_before_disposal()
    {
        bool armed = true;
        await using var session = new GuardHostSession(_files, () => armed);
        Assert.True(_files.Read<GuardLease>("lease.json")!.Armed);
        armed = false;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (_files.Read<GuardLease>("lease.json")!.Armed) await Task.Delay(20, deadline.Token);
        Assert.True(session.ExpectedStop()); Assert.True(_files.Stopped(_registration.Generation));
    }
    [Fact]
    public void Malformed_and_oversized_records_are_not_trusted()
    {
        File.WriteAllText(Path.Combine(_directory, "lease.json"), "{broken"); Assert.Null(_files.Read<GuardLease>("lease.json"));
        File.WriteAllText(Path.Combine(_directory, "lease.json"), new string(' ', 32769)); Assert.Null(_files.Read<GuardLease>("lease.json"));
        Assert.True(_supervisor.Tick(0, true)); Assert.Equal("Watching", Status.Phase);
    }
    [Theory]
    [InlineData("{}")]
    [InlineData("{\"Generation\":\"11111111-1111-4111-8111-111111111111\",\"Host\":null,\"Sequence\":1,\"Armed\":true}")]
    [InlineData("{\"Generation\":\"11111111-1111-4111-8111-111111111111\",\"Host\":{\"Id\":1,\"StartedUtcTicks\":1},\"Sequence\":0,\"Armed\":true}")]
    public void Incomplete_lifecycle_records_are_rejected(string json)
    {
        File.WriteAllText(Path.Combine(_directory, "lease.json"), json);
        Assert.Null(_files.Read<GuardLease>("lease.json")); Assert.True(_supervisor.Tick(0, true));
        Assert.Empty(_processes.Launches);
    }
    public void Dispose() { if (Directory.Exists(_directory)) Directory.Delete(_directory, true); }
    private sealed class FakeProcesses : IGuardProcesses
    {
        public HashSet<GuardProcess> Alive { get; } = [];
        public List<bool> Launches { get; } = [];
        public bool Fail { get; set; }
        public GuardProcess Last { get; private set; } = new(30, 300);
        public bool Matches(GuardProcess p, bool app) => Alive.Contains(p);
        public bool IsAlive(GuardProcess p) => Alive.Contains(p);
        public GuardProcess Start(bool app)
        {
            Launches.Add(app); if (Fail) throw new IOException("Synthetic startup failure.");
            Last = new(30 + Launches.Count, 300 + Launches.Count); Alive.Add(Last); return Last;
        }
    }
}

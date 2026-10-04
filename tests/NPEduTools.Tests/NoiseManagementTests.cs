using NPEduTools.Contracts;
using NPEduTools.Core;
using NPEduTools.Host;

namespace NPEduTools.Tests;

public sealed class NoiseManagementTests
{
    private const string Secret = "teacher-local-2026";
    private sealed class Clock : TimeProvider
    {
        public long Ms;
        public override long TimestampFrequency => 1000;
        public override long GetTimestamp() => Ms;
        public override ITimer CreateTimer(TimerCallback c, object? state, TimeSpan due, TimeSpan period) => new NoTimer();
        private sealed class NoTimer : ITimer
        {
            public bool Change(TimeSpan dueTime, TimeSpan period) => true;
            public void Dispose() { }
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }
    private sealed class Fixture : IAsyncDisposable
    {
        public readonly string Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "noise-management-" + Guid.NewGuid());
        public readonly Clock Time = new();
        public readonly NoiseManagementStore Store;
        public readonly NoiseService Noise;
        public int Skips;
        public bool CanSave = true;
        public Fixture(bool configured = true, ManualResetEventSlim? startGate = null)
        {
            Store = new(Path, Time);
            if (configured) Assert.Null(Store.Configure(null, Secret, false));
            Noise = new(_ => new Capture(startGate), () => [], Time, management: Store);
            Noise.ManualStopping = _ => { if (CanSave) Skips++; return CanSave; };
            Noise.Handle(Command("select", "synthetic"));
        }
        public HostRequest Command(string action, string? mic = null)
        { var s = Noise.Snapshot(); return new(1, Guid.NewGuid(), "noise.command", Noise: new(action, s.InstanceId, s.Revision, mic)); }
        public async Task Start(bool scheduled = true)
        {
            Assert.Equal("Accepted", (scheduled ? Noise.StartScheduled(Guid.NewGuid()) : Noise.Handle(Command("start", "synthetic"))).Outcome);
            await Until(() => Noise.Snapshot().State == "Active");
        }
        public HostRequest Grant(HostRequest r, string secret = Secret)
        {
            var state = Noise.Protection();
            var response = Noise.ManagementHandle(new(1, Guid.NewGuid(), "noise.management.command",
                NoiseManagement: new("authorize", secret, Purpose: Protocol.NoisePurpose(r), TargetRequestId: r.RequestId,
                    InstanceId: state.InstanceId, SessionId: state.SessionId)));
            Assert.Equal("Succeeded", response.Outcome);
            return r with { NoiseAuthorization = response.NoiseProtection!.Ticket };
        }
        public async ValueTask DisposeAsync()
        { await Noise.DisposeAsync(); if (Directory.Exists(Path)) Directory.Delete(Path, true); }
    }
    private sealed class Capture(ManualResetEventSlim? gate) : INoiseCapture
    {
        public string DeviceName => "Synthetic; no microphone";
        public event Action<NoiseFrame>? Frame { add { } remove { } }
        public event Action<string>? Failed { add { } remove { } }
        public void Start() => gate?.Wait();
        public void Dispose() { }
    }
    private static async Task Until(Func<bool> check)
    { using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(4)); while (!check()) await Task.Delay(10, deadline.Token); }

    [Fact]
    public async Task Untrusted_pipe_stop_and_environment_interruptions_cannot_cancel_schedule()
    {
        await using var f = new Fixture(); await f.Start();
        Assert.Equal("MANAGEMENT_REQUIRED", f.Noise.Handle(f.Command("stop")).ErrorCode);
        var requests = new HostRequest[] {
            new(1, Guid.NewGuid(), "host.stop"),
            new(1, Guid.NewGuid(), "npep.command", Npep: new("pause", 0)),
            new(1, Guid.NewGuid(), "npep.command", Npep: new("unpair", 0)),
            new(1, Guid.NewGuid(), "classroom.set", ExpectedRevision: 0, ClassroomMode: new("Exam")),
            new(1, Guid.NewGuid(), "classisland.config.set", ExecutablePath: "fixture.exe", ExpectedRevision: 0),
            new(1, Guid.NewGuid(), "examaware.quit")
        };
        foreach (var r in requests) { Assert.Null(Protocol.Validate(r)); Assert.Equal("MANAGEMENT_REQUIRED", f.Noise.AuthorizeInterruption(r)!.ErrorCode); }
        Assert.Equal("Active", f.Noise.Snapshot().State); Assert.Equal(0, f.Skips);
    }
    [Fact]
    public async Task Grant_is_scoped_to_request_purpose_and_current_session_then_consumed()
    {
        await using var f = new Fixture(); await f.Start();
        var stop = f.Grant(f.Command("stop"));
        Assert.Equal("MANAGEMENT_REQUIRED", f.Noise.Handle(stop with { RequestId = Guid.NewGuid() }).ErrorCode);
        var wrongAction = new HostRequest(1, stop.RequestId, "host.stop", NoiseAuthorization: stop.NoiseAuthorization);
        Assert.Equal("MANAGEMENT_REQUIRED", f.Noise.AuthorizeInterruption(wrongAction)!.ErrorCode);
        Assert.Equal("Accepted", f.Noise.Handle(stop).Outcome); Assert.Equal(1, f.Skips);
        await Until(() => f.Noise.Snapshot().State == "Stopped");
        // A reused grant cannot stop a later scheduled capture.
        await Until(() => f.Noise.StartScheduled(Guid.NewGuid()).Outcome == "Accepted");
        await Until(() => f.Noise.Snapshot().State == "Active");
        Assert.Equal("MANAGEMENT_REQUIRED", f.Noise.Handle(f.Command("stop") with { RequestId = stop.RequestId, NoiseAuthorization = stop.NoiseAuthorization }).ErrorCode);
    }
    [Fact]
    public async Task Expired_grant_uses_elapsed_time_and_does_not_stop()
    {
        await using var f = new Fixture(); await f.Start(); var stop = f.Grant(f.Command("stop"));
        f.Time.Ms += 60000;
        Assert.Equal("MANAGEMENT_REQUIRED", f.Noise.Handle(stop).ErrorCode); Assert.Equal(0, f.Skips);
    }
    [Fact]
    public async Task Invalid_password_is_throttled_and_rotation_revokes_issued_grants()
    {
        await using var f = new Fixture(); await f.Start();
        var s = f.Noise.Protection();
        var auth = new HostRequest(1, Guid.NewGuid(), "noise.management.command", NoiseManagement:
            new("authorize", "wrong-password", Purpose: "stop", TargetRequestId: Guid.NewGuid(), InstanceId: s.InstanceId, SessionId: s.SessionId));
        for (int i = 0; i < 5; i++) Assert.Equal("MANAGEMENT_SECRET_INVALID", f.Noise.ManagementHandle(auth).ErrorCode);
        Assert.Equal("MANAGEMENT_RATE_LIMITED", f.Noise.ManagementHandle(auth with { NoiseManagement = auth.NoiseManagement! with { Secret = Secret } }).ErrorCode);
        f.Time.Ms += 60000;
        var stop = f.Grant(f.Command("stop"));
        Assert.Equal("Succeeded", f.Noise.ManagementHandle(new(1, Guid.NewGuid(), "noise.management.command", NoiseManagement: new("configure", Secret, "new-teacher-local"))).Outcome);
        Assert.Equal("MANAGEMENT_REQUIRED", f.Noise.Handle(stop).ErrorCode);
    }
    [Fact]
    public async Task Initial_configuration_cannot_unlock_an_already_running_schedule()
    {
        await using var f = new Fixture(false); await f.Start();
        Assert.False(f.Noise.Protection().Configured); Assert.True(f.Noise.Protection().Protected);
        Assert.Equal("MANAGEMENT_NOT_CONFIGURED", f.Noise.ManagementHandle(new(1, Guid.NewGuid(), "noise.management.command", NoiseManagement: new("configure", NewSecret: Secret))).ErrorCode);
        Assert.Equal("MANAGEMENT_REQUIRED", f.Noise.Handle(f.Command("stop")).ErrorCode);
    }
    [Fact]
    public async Task Start_is_protected_before_microphone_open_completes()
    {
        using var gate = new ManualResetEventSlim(false); await using var f = new Fixture(startGate: gate);
        try
        {
            f.Noise.StartScheduled(Guid.NewGuid()); Assert.Equal("Starting", f.Noise.Snapshot().State);
            Assert.Equal("MANAGEMENT_REQUIRED", f.Noise.Handle(f.Command("stop")).ErrorCode); Assert.Equal(0, f.Skips);
        }
        finally { gate.Set(); }
    }
    [Fact]
    public async Task Journal_failure_rejects_normal_stop_before_cancelling_capture()
    {
        await using var f = new Fixture(); await f.Start(); var stop = f.Grant(f.Command("stop")); f.CanSave = false;
        Assert.Equal("SCHEDULE_STORE_UNAVAILABLE", f.Noise.Handle(stop).ErrorCode);
        Assert.Equal("Active", f.Noise.Snapshot().State); Assert.Equal(0, f.Skips);
    }
    [Fact]
    public async Task Prepared_exit_fences_start_and_correlates_final_shutdown()
    {
        await using var f = new Fixture(); await f.Start(); var stopHost = f.Grant(new(1, Guid.NewGuid(), "host.stop"));
        var prepare = stopHost with { Capability = "noise.management.prepare-exit" };
        Assert.Equal("Succeeded", f.Noise.ManagementHandle(prepare).Outcome);
        Assert.Equal("Succeeded", f.Noise.ManagementHandle(prepare).Outcome); Assert.Equal(1, f.Skips);
        await Until(() => f.Noise.Snapshot().State == "Stopped");
        Assert.Equal("NoiseBusy", f.Noise.StartScheduled(Guid.NewGuid()).ErrorCode);
        Assert.Equal("MANAGEMENT_REQUIRED", f.Noise.AuthorizeInterruption(new(1, Guid.NewGuid(), "host.stop"))!.ErrorCode);
        Assert.Null(f.Noise.AuthorizeInterruption(stopHost));
        f.Time.Ms += 120000; Assert.Equal("NoiseBusy", f.Noise.StartScheduled(Guid.NewGuid()).ErrorCode);
    }
    [Fact]
    public async Task Manual_sessions_and_internal_schedule_end_are_not_blocked()
    {
        await using var f = new Fixture(); await f.Start(false);
        Assert.False(f.Noise.Protection().Protected); Assert.Equal("Accepted", f.Noise.Handle(f.Command("stop")).Outcome);
        await Until(() => f.Noise.StartScheduled(Guid.NewGuid()).Outcome == "Accepted"); await Until(() => f.Noise.Snapshot().State == "Active");
        int skips = f.Skips; f.Noise.StopScheduled(f.Noise.Snapshot().SessionId!.Value);
        await Until(() => f.Noise.Snapshot().State == "Stopped"); Assert.Equal(skips, f.Skips); Assert.False(f.Noise.Protection().Protected);
    }
    [Fact]
    public async Task Only_internal_verified_remote_path_can_end_protected_session()
    {
        await using var f = new Fixture(); await f.Start(); var s = f.Noise.Snapshot();
        Assert.Equal("MANAGEMENT_REQUIRED", f.Noise.RemoteCommand(Guid.NewGuid(), "STOP", s.InstanceId, s.Revision, s.SessionId, 60).ErrorCode);
        Assert.Equal("Accepted", f.Noise.RemoteAuthorizedCommand(Guid.NewGuid(), "STOP", s.InstanceId, s.Revision, s.SessionId, 60).Outcome);
        Assert.Equal(1, f.Skips);
    }
    [Fact]
    public async Task Credential_is_hashed_survives_restart_and_invalid_file_is_preserved()
    {
        await using var f = new Fixture(); string file = System.IO.Path.Combine(f.Path, "noise-management.json");
        Assert.DoesNotContain(Secret, File.ReadAllText(file)); Assert.True(new NoiseManagementStore(f.Path).Configured);
        File.WriteAllText(file, "corrupt-original"); var broken = new NoiseManagementStore(f.Path);
        Assert.False(broken.Configured); Assert.Equal("MANAGEMENT_STORE_UNAVAILABLE", broken.Configure(null, Secret, false));
        Assert.Equal("corrupt-original", File.ReadAllText(file));
    }
    [Fact]
    public void Management_contract_rejects_scope_free_or_unrelated_ticket_injection()
    {
        Assert.Equal("InvalidNoiseManagementCommand", Protocol.Validate(new(1, Guid.NewGuid(), "noise.management.command",
            NoiseManagement: new("authorize", Secret, Purpose: "stop"))));
        Assert.Equal("UnexpectedParameters", Protocol.Validate(new(1, Guid.NewGuid(), "noise.status", NoiseAuthorization: Guid.NewGuid())));
        Assert.Equal("InvalidNoiseManagementCommand", Protocol.Validate(new(1, Guid.NewGuid(), "host.stop", NoiseManagement: new("configure", NewSecret: Secret))));
    }
    [Theory]
    [InlineData("{\"Version\":1}")]
    [InlineData("{\"Version\":1,\"Salt\":null,\"Hash\":null}")]
    [InlineData("{\"Version\":1,\"Salt\":\"AA==\",\"Hash\":null}")]
    public void Incomplete_credential_does_not_crash_or_reset_protection(string contents)
    {
        string directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "noise-management-" + Guid.NewGuid());
        Directory.CreateDirectory(directory);
        try
        {
            string file = System.IO.Path.Combine(directory, "noise-management.json"); File.WriteAllText(file, contents);
            var store = new NoiseManagementStore(directory);
            Assert.False(store.Configured); Assert.Equal("MANAGEMENT_STORE_UNAVAILABLE", store.Error);
            Assert.Equal("MANAGEMENT_STORE_UNAVAILABLE", store.Configure(null, Secret, false));
            Assert.Equal(contents, File.ReadAllText(file));
        }
        finally { Directory.Delete(directory, true); }
    }
}

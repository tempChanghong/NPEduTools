using NPEduTools.Contracts;
using NPEduTools.Host;

namespace NPEduTools.Tests;

public sealed partial class ManualStudyTests
{
    private const string Scope = "synthetic-original-class";
    private static readonly string Version = new('a', 64);
    private static readonly string NextVersion = new('b', 64);

    private static HostRequest StartRequest(Fixture f, bool reserved)
    {
        var state = f.Manual.Status();
        var command = new ManualStudyCommand("start", state.InstanceId, state.Revision, DeviceId: "mic",
            StartUtc: reserved ? f.Time.GetUtcNow().AddMinutes(5) : null);
        return new(1, Guid.NewGuid(), "manual.study.command", ManualStudy: command);
    }

    private static HostResponse Grant(Fixture f, HostRequest target, string? secret = null)
    {
        var state = f.Noise.Protection();
        var command = new NoiseManagementCommand("authorize", secret, Purpose: Protocol.NoisePurpose(target),
            InstanceId: state.InstanceId, SessionId: state.SessionId, ClassroomPolicyVersion: Version,
            TargetRequestId: target.RequestId, ManualStudyTarget: target.ManualStudy);
        var request = new HostRequest(1, Guid.NewGuid(), "noise.management.command", NoiseManagement: command);
        Assert.Null(Protocol.Validate(request));
        return f.Noise.ManagementHandle(request);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Original_manual_start_is_required_for_immediate_and_reserved_study(bool reserved)
    {
        await using var f = new Fixture(managed: true);
        f.Noise.SetClassroomActionPolicy(Version, "allow", "allow", null, Scope, startManualStudy: "deny");
        var target = StartRequest(f, reserved);
        Assert.Equal("CLASSROOM_ACTION_DENIED", f.Manual.Handle(target).ErrorCode);
        Assert.Equal("CLASSROOM_ACTION_DENIED", Grant(f, target).ErrorCode);
        f.Noise.SetClassroomActionPolicy(Version, "allow", "allow", null, Scope, startManualStudy: "allow");
        Assert.Equal("MANAGEMENT_REQUIRED", f.Manual.Handle(target).ErrorCode);
        Assert.False(Protocol.NoiseInterruption(target));
        var ticket = Assert.IsType<Guid>(Grant(f,target).NoiseProtection?.Ticket);
        target = target with { NoiseAuthorization = ticket };
        Assert.Null(Protocol.Validate(target));
        foreach (var different in new[] { target.ManualStudy! with { StartUtc = f.Time.GetUtcNow().AddMinutes(8) },
            target.ManualStudy! with { EndUtc = f.Time.GetUtcNow().AddMinutes(30) },
            target.ManualStudy! with { DeviceId = "another-mic" }, target.ManualStudy! with { HideSeconds = 10 } })
            Assert.Equal("MANAGEMENT_REQUIRED", f.Manual.Handle(target with { ManualStudy = different }).ErrorCode);
        Assert.Equal("MANAGEMENT_REQUIRED", f.Manual.Handle(target with { RequestId = Guid.NewGuid() }).ErrorCode);
        Assert.Empty(f.Captures);
        Assert.Equal("Succeeded", f.Manual.Handle(target).Outcome);
        if (reserved) { Assert.Equal("Waiting", f.Manual.Status().State); f.Time.Ms += 300000; }
        await f.Active();
        f.Noise.SetClassroomActionPolicy(NextVersion, "deny", "deny", null, Scope, startManualStudy: "deny");
        f.Manual.Tick();
        Assert.Equal("Active", f.Manual.Status().State);
        Assert.Equal("Succeeded", f.Manual.Handle(target).Outcome);
        Assert.Single(f.Captures);
        Assert.Equal("Succeeded", f.Manual.Handle(f.End(f.Manual.Status())).Outcome);
        await f.Ended();
    }

    [Theory]
    [InlineData("usb", false, false)]
    [InlineData("either", false, false)]
    [InlineData("either", true, true)]
    [InlineData("password", true, true)]
    [InlineData("both", true, false)]
    public async Task Reservation_checks_original_binding_and_only_credentials_used_at_acceptance(
        string mode, bool usedPassword, bool rotatePassword)
    {
        await using var f = new Fixture(managed: true);
        Assert.Null(f.Noise.BindSchoolManagement(Scope));
        Assert.Null(f.Noise.ApplySchoolManagement(Scope, SchoolManagementTests.Credential()));
        bool usb = false;
        void Publish(string version, string otherRule) => f.Noise.SetClassroomActionPolicy(version,
            otherRule, otherRule, () => usb, Scope, otherRule, mode, "original-action-and-binding");
        Publish(Version, "allow");
        var target = StartRequest(f, reserved: true);
        const string password = "school-password";
        bool usbRequired = mode == "both" || !usedPassword;
        if (usbRequired) Assert.Equal("CLASSROOM_USB_REQUIRED", Grant(f,target,usedPassword ? password : null).ErrorCode);
        usb = true;
        if (mode == "both") Assert.Equal("MANAGEMENT_REQUIRED", Grant(f,target).ErrorCode);
        if (usedPassword) Assert.Equal("MANAGEMENT_SECRET_INVALID", Grant(f,target,"wrong-password").ErrorCode);
        var ticket = Assert.IsType<Guid>(Grant(f,target,usedPassword ? password : null).NoiseProtection?.Ticket);
        target = target with { NoiseAuthorization = ticket };
        if (usbRequired)
        {
            usb = false;
            Assert.Equal("CLASSROOM_USB_REQUIRED", f.Manual.Handle(target).ErrorCode);
            usb = true;
        }
        Assert.Equal("Succeeded", f.Manual.Handle(target).Outcome);
        usb = false;
        Publish(NextVersion, "deny");
        if (rotatePassword || !usedPassword)
            Assert.Null(f.Noise.ApplySchoolManagement(Scope, SchoolManagementTests.Credential(2)));
        f.Time.Ms += 300000;
        f.Manual.Tick();
        if (rotatePassword)
        {
            Assert.Equal("Cancelled", f.Manual.Status().State);
            Assert.Equal("CLASSROOM_POLICY_CHANGED", f.Manual.Status().Reason);
            Assert.Empty(f.Captures);
        }
        else { await f.Active(); Assert.Single(f.Captures); }
    }

    [Theory]
    [InlineData("scope")] [InlineData("rule")] [InlineData("revoke-then-restore")]
    public async Task Original_reservation_cannot_cross_assignment_or_policy_changes(string change)
    {
        await using var f = new Fixture(managed: true);
        f.Noise.SetClassroomActionPolicy(Version, "allow", "allow", null, Scope, startManualStudy:"allow", manualStudyScopeVersion:"original");
        var target = StartRequest(f, reserved:true);
        target = target with { NoiseAuthorization = Assert.IsType<Guid>(Grant(f,target).NoiseProtection?.Ticket) };
        Assert.Equal("Succeeded", f.Manual.Handle(target).Outcome);
        f.Time.Ms += 200000; f.Manual.Tick();
        Assert.Equal("Preparing", f.Manual.Status().State);
        f.Noise.SetClassroomActionPolicy(NextVersion, "allow", "allow", null, change == "scope" ? "other" : Scope,
            startManualStudy: change == "scope" ? "allow" : "deny", manualStudyScopeVersion: "new-action-or-binding");
        if (change == "revoke-then-restore")
            f.Noise.SetClassroomActionPolicy(Version,"allow","allow",null, Scope, startManualStudy:"allow",manualStudyScopeVersion:"original");
        f.Time.Ms += 100000; f.Manual.Tick();
        Assert.Equal("Cancelled", f.Manual.Status().State);
        Assert.Equal("CLASSROOM_POLICY_CHANGED", f.Manual.Status().Reason);
        Assert.Empty(f.Captures);
    }

    [Fact]
    public async Task Old_status_is_compatible_and_default_reservation_cannot_become_managed_without_confirmation()
    {
        await using var f = new Fixture(managed:true);
        void Publish(string rule) => f.Noise.SetClassroomActionPolicy(Version, "deny", "deny", null, Scope,
            switchClassroomMode:"both", startManualStudy:rule, manualStudyScopeVersion:"class");
        Publish("default");
        Assert.Equal("Succeeded", f.Manual.Handle(StartRequest(f,true)).Outcome);
        Publish("allow");
        foreach (var suffix in new[] { "", ".v2", ".v4" })
        {
            var request = new HostRequest(1,Guid.NewGuid(),"noise.management.status.classroom-policy"+suffix);
            Assert.Null(Protocol.Validate(request));
            var result = Assert.IsType<ClassroomActionPolicyState>(f.Noise.ManagementHandle(request).NoiseProtection!.ClassroomPolicy);
            Assert.Equal(suffix == ".v4" ? "allow" : null, result.StartManualStudy);
            Assert.Equal(suffix == "" ? null : "both", result.SwitchClassroomMode);
        }
        f.Manual.Tick();
        Assert.Equal("CLASSROOM_POLICY_CHANGED", f.Manual.Status().Reason);
        Assert.Empty(f.Captures);
    }

    [Fact, System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public async Task Manual_start_pipe_does_not_interrupt_school_capture_and_reservation_end_needs_no_ticket()
    {
        await using var f = new Fixture(managed:true);
        string pipe = "NPEduTools.Test.manual-policy." + Guid.NewGuid().ToString("N");
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var serve = new PipeServer(pipe,new NoSchoolRead(),_ => {},noise:f.Noise,manualStudy:f.Manual).RunAsync(stop.Token);
        try
        {
            f.Noise.SetClassroomActionPolicy(Version,"deny","deny",null,Scope,startManualStudy:"allow");
            var state = (await HostClient.RequestAsync(pipe,"noise.management.status.classroom-policy.v4",stop.Token)).NoiseProtection!;
            var target = StartRequest(f,true);
            var authorize = new HostRequest(1,Guid.NewGuid(),"noise.management.command",NoiseManagement:
                new("authorize",Purpose:"start-manual-study",TargetRequestId:target.RequestId,InstanceId:state.InstanceId,
                    SessionId:state.SessionId,ClassroomPolicyVersion:Version,ManualStudyTarget:target.ManualStudy));
            Assert.Equal("MANAGEMENT_REQUIRED",(await HostClient.RequestAsync(pipe,target,stop.Token)).ErrorCode);
            var ticket = (await HostClient.RequestAsync(pipe,authorize,stop.Token)).NoiseProtection!.Ticket;
            Assert.NotNull(ticket);
            var accepted = await HostClient.RequestAsync(pipe,target with { NoiseAuthorization=ticket },stop.Token);
            Assert.Equal("Waiting",accepted.ManualStudy!.State);
            var cancelled = await HostClient.RequestAsync(pipe,f.End(accepted.ManualStudy),stop.Token);
            Assert.Equal("CancelledBeforeStart",cancelled.ManualStudy!.Reason);
            Assert.Empty(f.Captures);
            var schoolSession = Guid.NewGuid();
            Assert.Null(f.Noise.SelectSchoolMicrophone("mic"));
            Assert.Equal("Accepted", f.Noise.StartScheduled(schoolSession).Outcome);
            target = StartRequest(f,false);
            authorize = authorize with {RequestId=Guid.NewGuid(),NoiseManagement=authorize.NoiseManagement! with {
                TargetRequestId=target.RequestId,SessionId=schoolSession,ManualStudyTarget=target.ManualStudy }};
            var schoolTicket = (await HostClient.RequestAsync(pipe,authorize,stop.Token)).NoiseProtection!.Ticket;
            Assert.NotNull(schoolTicket);
            var busy = await HostClient.RequestAsync(pipe,target with {NoiseAuthorization=schoolTicket},stop.Token);
            Assert.Equal("MANUAL_BUSY", busy.ErrorCode);
            Assert.Equal(schoolSession,busy.Noise!.SessionId);
            Assert.True(f.Noise.Protection().Protected);
        }
        finally { await stop.CancelAsync(); await serve; }
    }
}

using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using NPEduTools.Contracts;

namespace NPEduTools.Tests;

public sealed partial class NoiseManagementTests
{
    private static readonly string ClassroomVersion = new('a', 64);
    private static readonly string SecondVersion = new('b', 64);
    private const string SchoolScope = "synthetic-original-school-and-binding";
    private const string SchoolSecret = "fixture-classroom-credential";

    private static void ApplyCredential(Fixture f, long version = 1)
    {
        Assert.Null(f.Noise.BindSchoolManagement(SchoolScope));
        var salt = RandomNumberGenerator.GetBytes(16);
        var hash = Rfc2898DeriveBytes.Pbkdf2(SchoolSecret, salt, 210000, HashAlgorithmName.SHA256, 32);
        Assert.Null(f.Noise.ApplySchoolManagement(SchoolScope, new JsonObject
        {
            ["revision"] = version,
            ["verifier"] = new JsonObject
            { ["salt"] = Convert.ToBase64String(salt), ["hash"] = Convert.ToBase64String(hash), ["iterations"] = 210000 }
        }));
    }

    private static HostResponse PolicyGrant(Fixture f, HostRequest target, string? secret = null,
        string? version = null)
    {
        var state = f.Noise.Protection();
        var request = new HostRequest(1, Guid.NewGuid(), "noise.management.command",
            NoiseManagement: new("authorize", secret, Purpose: Protocol.NoisePurpose(target), TargetRequestId: target.RequestId,
                InstanceId: state.InstanceId, SessionId: state.SessionId, ClassroomPolicyVersion: version ?? ClassroomVersion));
        Assert.Null(Protocol.Validate(request));
        return f.Noise.ManagementHandle(request);
    }

    [Theory]
    [InlineData("allow", false, null, null)]
    [InlineData("deny", true, SchoolSecret, "CLASSROOM_ACTION_DENIED")]
    [InlineData("usb", false, null, "CLASSROOM_USB_REQUIRED")]
    [InlineData("usb", true, null, null)]
    [InlineData("password", true, null, "MANAGEMENT_REQUIRED")]
    [InlineData("password", true, "other-secret", "MANAGEMENT_SECRET_INVALID")]
    [InlineData("password", false, SchoolSecret, null)]
    [InlineData("either", false, SchoolSecret, null)]
    [InlineData("either", true, null, null)]
    [InlineData("either", false, null, "CLASSROOM_USB_REQUIRED")]
    [InlineData("both", false, SchoolSecret, "CLASSROOM_USB_REQUIRED")]
    [InlineData("both", true, null, "MANAGEMENT_REQUIRED")]
    [InlineData("both", true, "other-secret", "MANAGEMENT_SECRET_INVALID")]
    [InlineData("both", true, SchoolSecret, null)]
    public async Task Managed_idle_exit_enforces_all_modes_and_binds_one_request(string mode, bool present, string? secret, string? expected)
    {
        await using var f = new Fixture(false);
        ApplyCredential(f);
        f.Noise.SetClassroomActionPolicy(ClassroomVersion, "deny", mode, () => present);
        var shutdown = new HostRequest(1, Guid.NewGuid(), "host.stop");
        var plain = f.Noise.ManagementHandle(new(1, Guid.NewGuid(), "noise.management.status"));
        Assert.False(plain.NoiseProtection!.Protected);
        Assert.DoesNotContain("classroomPolicy", JsonSerializer.Serialize(plain, Protocol.Json), StringComparison.OrdinalIgnoreCase);
        var optedIn = f.Noise.ManagementHandle(new(1, Guid.NewGuid(), "noise.management.status.classroom-policy"));
        Assert.Equal(new ClassroomActionPolicyState(ClassroomVersion, "deny", mode), optedIn.NoiseProtection!.ClassroomPolicy);
        Assert.NotNull(f.Noise.AuthorizeInterruption(shutdown));
        var grant = PolicyGrant(f, shutdown, secret);
        Assert.Equal(expected, grant.ErrorCode);
        if (expected is not null)
        {
            Assert.Null(grant.NoiseProtection!.Ticket);
            return;
        }
        var authorized = shutdown with { NoiseAuthorization = grant.NoiseProtection!.Ticket };
        Assert.NotNull(authorized.NoiseAuthorization);
        Assert.Equal("MANAGEMENT_REQUIRED", f.Noise.AuthorizeInterruption(authorized with { RequestId = Guid.NewGuid() })!.ErrorCode);
        Assert.Null(f.Noise.AuthorizeInterruption(authorized));
        Assert.Equal("MANAGEMENT_REQUIRED", f.Noise.AuthorizeInterruption(authorized)!.ErrorCode);
    }

    [Fact]
    public async Task Confirmed_exit_checks_usb_at_prepare_and_commit_without_stopping_capture_during_preparation()
    {
        await using var f = new Fixture(false);
        await f.Start();
        bool present = true;
        f.Noise.SetClassroomActionPolicy(ClassroomVersion, "deny", "usb", () => present);
        var stop = new HostRequest(1, Guid.NewGuid(), "host.stop");
        stop = stop with { NoiseAuthorization = PolicyGrant(f, stop).NoiseProtection!.Ticket };
        var prepare = stop with { Capability = "noise.management.prepare-exit" };
        present = false;
        Assert.Equal("CLASSROOM_USB_REQUIRED", f.Noise.ManagementHandle(prepare).ErrorCode);
        present = true;
        Assert.Equal("Succeeded", f.Noise.ManagementHandle(prepare).Outcome);
        Assert.Equal("Succeeded", f.Noise.ManagementHandle(prepare).Outcome);
        Assert.Equal(0, f.Skips);
        Assert.Equal("Active", f.Noise.Snapshot().State);
        present = false;
        Assert.Equal("CLASSROOM_USB_REQUIRED", f.Noise.AuthorizeInterruption(stop)!.ErrorCode);
        Assert.Equal(0, f.Skips);
        present = true;
        Assert.Null(f.Noise.AuthorizeInterruption(stop));
        Assert.Equal(1, f.Skips);
    }

    [Fact]
    public async Task Changed_classroom_rule_or_rotated_password_invalidates_prepared_exit()
    {
        await using var f = new Fixture(false);
        ApplyCredential(f);
        f.Noise.SetClassroomActionPolicy(ClassroomVersion, "allow", "password", null);
        var exit = new HostRequest(1, Guid.NewGuid(), "host.stop");
        var authorized = exit with { NoiseAuthorization = PolicyGrant(f, exit, SchoolSecret).NoiseProtection!.Ticket };
        Assert.Equal("Succeeded", f.Noise.ManagementHandle(authorized with { Capability = "noise.management.prepare-exit" }).Outcome);
        ApplyCredential(f, 2);
        Assert.Equal("MANAGEMENT_REQUIRED", f.Noise.AuthorizeInterruption(authorized)!.ErrorCode);
        f.Noise.SetClassroomActionPolicy(SecondVersion, "allow", "deny", null);
        Assert.Equal("CLASSROOM_ACTION_DENIED", f.Noise.AuthorizeInterruption(authorized)!.ErrorCode);
        f.Noise.SetClassroomActionPolicy(null, null, null, null);
        Assert.Equal("MANAGEMENT_REQUIRED", f.Noise.AuthorizeInterruption(authorized)!.ErrorCode);
    }

    [Fact]
    public async Task Usb_observation_cannot_authorize_another_policy_and_does_not_hold_the_capture_lock()
    {
        await using var f = new Fixture(false);
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        f.Noise.SetClassroomActionPolicy(ClassroomVersion, "allow", "usb", () =>
        {
            entered.Set();
            return release.Wait(TimeSpan.FromSeconds(5));
        });
        var target = new HostRequest(1, Guid.NewGuid(), "host.stop");
        var authorizing = Task.Run(() => PolicyGrant(f, target));
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
            f.Noise.SetClassroomActionPolicy(SecondVersion, "allow", "usb", () => true);
        }
        finally { release.Set(); }
        var result = await authorizing;
        Assert.Equal("CLASSROOM_POLICY_CHANGED", result.ErrorCode);
        Assert.Null(result.NoiseProtection!.Ticket);
    }

    [Fact]
    public async Task Stop_targets_live_scheduled_capture_and_separate_environment_or_exit_authority_cannot_supply_stop()
    {
        await using var f = new Fixture(false);
        ApplyCredential(f);
        f.Noise.SetClassroomActionPolicy(ClassroomVersion, "both", "allow", () => true);
        await f.Start(false);
        Assert.Equal("Accepted", f.Noise.Handle(f.Command("stop")).Outcome);
        await Until(() => f.Noise.Snapshot().State == "Stopped");
        await f.Start();
        var target = f.Command("stop");
        var exit = new HostRequest(1, target.RequestId, "host.stop");
        var wrong = PolicyGrant(f, exit).NoiseProtection!.Ticket;
        Assert.Equal("MANAGEMENT_REQUIRED", f.Noise.Handle(target with { NoiseAuthorization = wrong }).ErrorCode);
        var env = new HostRequest(1, Guid.NewGuid(), "npep.command", Npep: new("pause", 0));
        Assert.Equal("CLASSROOM_STOP_REQUIRED", f.Noise.AuthorizeInterruption(env)!.ErrorCode);
        var authorized = target with { NoiseAuthorization = PolicyGrant(f, target, SchoolSecret).NoiseProtection!.Ticket };
        f.Noise.UpdateScheduledWindow(f.Noise.Protection().SessionId!.Value, () => { });
        target = f.Command("stop") with { RequestId = target.RequestId };
        Assert.Equal("MANAGEMENT_REQUIRED", f.Noise.Handle(target with { NoiseAuthorization = authorized.NoiseAuthorization }).ErrorCode);
        var fresh = target with { NoiseAuthorization = PolicyGrant(f, target, SchoolSecret).NoiseProtection!.Ticket };
        Assert.Equal("Accepted", f.Noise.Handle(fresh).Outcome);
    }

    [Fact]
    public async Task School_password_rule_never_uses_an_existing_local_credential()
    {
        await using var f = new Fixture();
        f.Noise.SetClassroomActionPolicy(ClassroomVersion, "allow", "password", null);
        Assert.Equal("SCHOOL_MANAGEMENT_REQUIRED", PolicyGrant(f, new(1, Guid.NewGuid(), "host.stop"), Secret).ErrorCode);
    }
}

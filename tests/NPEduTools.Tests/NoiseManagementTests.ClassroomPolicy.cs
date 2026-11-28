using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using NPEduTools.Contracts;
using NPEduTools.Host;

namespace NPEduTools.Tests;

public sealed partial class NoiseManagementTests
{
    private static readonly string ClassroomVersion = new('a', 64);
    private static readonly string SecondVersion = new('b', 64);
    private const string SchoolScope = "synthetic-original-school-and-binding";
    private const string SchoolSecret = "fixture-classroom-credential";

    private static JsonObject CredentialRow(long version)
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        var hash = Rfc2898DeriveBytes.Pbkdf2(SchoolSecret, salt, 210000, HashAlgorithmName.SHA256, 32);
        return new JsonObject
        {
            ["revision"] = version,
            ["verifier"] = new JsonObject
            { ["salt"] = Convert.ToBase64String(salt), ["hash"] = Convert.ToBase64String(hash), ["iterations"] = 210000 }
        };
    }
    private static void ApplyCredential(Fixture f, long version = 1)
    {
        Assert.Null(f.Noise.BindSchoolManagement(SchoolScope));
        Assert.Null(f.Noise.ApplySchoolManagement(SchoolScope, CredentialRow(version)));
    }

    private static HostResponse PolicyGrant(Fixture f, HostRequest target, string? secret = null,
        string? version = null) => PolicyGrant(f.Noise, target, secret, version);

    private static HostResponse PolicyGrant(NoiseService noise, HostRequest target, string? secret = null, string? version = null)
    {
        var state = noise.Protection();
        var request = new HostRequest(1, Guid.NewGuid(), "noise.management.command",
            NoiseManagement: new("authorize", secret, Purpose: Protocol.NoisePurpose(target), TargetRequestId: target.RequestId,
                InstanceId: state.InstanceId, SessionId: state.SessionId, ClassroomPolicyVersion: version ?? ClassroomVersion));
        Assert.Null(Protocol.Validate(request));
        return noise.ManagementHandle(request);
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

    [Theory]
    [InlineData("missing")]
    [InlineData("wrong-type")]
    [InlineData("invalid-verifier")]
    public async Task Negative_school_credential_observation_survives_restart_and_only_invalidates_the_password_factor(string change)
    {
        await using var f = new Fixture(false);
        ApplyCredential(f);
        f.Noise.SetClassroomActionPolicy(ClassroomVersion, "deny", "either", () => true, SchoolScope);
        var byPassword = new HostRequest(1, Guid.NewGuid(), "host.stop");
        byPassword = byPassword with { NoiseAuthorization = PolicyGrant(f, byPassword, SchoolSecret).NoiseProtection!.Ticket };
        var byUsb = new HostRequest(1, Guid.NewGuid(), "host.stop");
        byUsb = byUsb with { NoiseAuthorization = PolicyGrant(f, byUsb).NoiseProtection!.Ticket };
        Assert.NotNull(byPassword.NoiseAuthorization);
        Assert.NotNull(byUsb.NoiseAuthorization);

        void Receive()
        {
            if (change == "missing")
                Assert.Equal("MANAGEMENT_NOT_CONFIGURED", f.Noise.ApplySchoolManagement(SchoolScope, null));
            else if (change == "wrong-type")
                Assert.Throws<InvalidOperationException>(() => f.Noise.ApplySchoolManagement(SchoolScope, new JsonArray()));
            else
            {
                var row = CredentialRow(2);
                row["verifier"]!["iterations"] = 1;
                Assert.Equal("INVALID_RESPONSE", f.Noise.ApplySchoolManagement(SchoolScope, row));
            }
        }
        Receive();
        Assert.False(f.Noise.ClassroomProtection().Configured);
        Assert.Equal("MANAGEMENT_NOT_CONFIGURED", PolicyGrant(f, byPassword, SchoolSecret).ErrorCode);
        Assert.Equal("MANAGEMENT_REQUIRED", f.Noise.AuthorizeInterruption(byPassword)!.ErrorCode);
        Assert.Equal("Succeeded", f.Noise.ManagementHandle(byUsb with { Capability = "noise.management.prepare-exit" }).Outcome);
        var marker = Path.Combine(f.Path, "noise-management.json.classroom-password-unavailable");
        var firstWrite = File.GetLastWriteTimeUtc(marker);
        Receive();
        Assert.Equal(firstWrite, File.GetLastWriteTimeUtc(marker));

        await using var reopened = new NoiseService(_ => new Capture(null), () => [], f.Time,
            management: new NoiseManagementStore(f.Path, f.Time));
        reopened.SetClassroomActionPolicy(ClassroomVersion, "deny", "either", () => true, SchoolScope);
        Assert.Equal("MANAGEMENT_NOT_CONFIGURED", PolicyGrant(reopened, byPassword, SchoolSecret).ErrorCode);
        Assert.Equal("Succeeded", PolicyGrant(reopened, byUsb).Outcome);
        Assert.Null(reopened.ApplySchoolManagement(SchoolScope, CredentialRow(2)));
        Assert.True(reopened.ClassroomProtection().Configured);
        Assert.False(File.Exists(marker));
        Assert.Equal("Succeeded", PolicyGrant(reopened, byPassword, SchoolSecret).Outcome);
    }

    [Fact]
    public async Task Withdrawn_school_verifier_also_blocks_legacy_management_and_calibration()
    {
        await using var fixture = new Fixture(false);
        ApplyCredential(fixture);
        var store = new NoiseManagementStore(fixture.Path, fixture.Time);
        var instance = Guid.NewGuid();
        var session = Guid.NewGuid();
        var request = Guid.NewGuid();
        const string purpose = "stop";
        var command = new NoiseManagementCommand("authorize", Secret: SchoolSecret, InstanceId: instance,
            SessionId: session, Purpose: purpose, TargetRequestId: request);
        Assert.Null(store.VerifyCalibration(SchoolSecret));
        var existing = store.Issue(command, instance, session);
        Assert.NotNull(existing.Ticket);

        Assert.Equal("MANAGEMENT_NOT_CONFIGURED", store.ApplySchool(SchoolScope, null));
        store.SetClassroomPasswordReady(false, withdrawn: true);
        Assert.False(store.Configured);
        Assert.False(store.Consume(existing.Ticket, instance, session, purpose, request));
        Assert.Equal("MANAGEMENT_NOT_CONFIGURED", store.Issue(command, instance, session).Error);
        Assert.Equal("MANAGEMENT_NOT_CONFIGURED", fixture.Noise.ApplySchoolManagement(SchoolScope, null));
        Assert.Equal("MANAGEMENT_NOT_CONFIGURED", fixture.Noise.VerifyCalibrationSecret(SchoolSecret));

        store = new(fixture.Path, fixture.Time);
        Assert.Equal("MANAGEMENT_NOT_CONFIGURED", store.VerifyCalibration(SchoolSecret));
        Assert.Null(fixture.Noise.ApplySchoolManagement(SchoolScope, CredentialRow(2)));
        Assert.Null(fixture.Noise.VerifyCalibrationSecret(SchoolSecret));
        Assert.Equal("POLICY_CHANGED", fixture.Noise.ApplySchoolManagement(SchoolScope, CredentialRow(1)));
        Assert.Null(fixture.Noise.VerifyCalibrationSecret(SchoolSecret));
        Assert.Equal("INVALID_RESPONSE", fixture.Noise.ApplySchoolManagement(SchoolScope, null, credentialIncluded: false));
        Assert.Null(fixture.Noise.VerifyCalibrationSecret(SchoolSecret));
    }

    [Fact]
    public async Task Scope_mismatch_between_cached_password_and_effective_policy_requires_current_class_credential()
    {
        await using var f = new Fixture(false);
        ApplyCredential(f);
        const string targetScope = "synthetic-other-binding";
        f.Noise.SetClassroomActionPolicy(SecondVersion, "deny", "password", null, targetScope);
        var target = new HostRequest(1, Guid.NewGuid(), "host.stop");
        Assert.False(f.Noise.ClassroomProtection().Configured);
        Assert.Equal("MANAGEMENT_NOT_CONFIGURED", PolicyGrant(f, target, SchoolSecret, SecondVersion).ErrorCode);
        Assert.Null(f.Noise.BindSchoolManagement(targetScope));
        Assert.Null(f.Noise.ApplySchoolManagement(targetScope, CredentialRow(1)));
        Assert.True(f.Noise.ClassroomProtection().Configured);
        Assert.Equal("Succeeded", PolicyGrant(f, target, SchoolSecret, SecondVersion).Outcome);
    }
}

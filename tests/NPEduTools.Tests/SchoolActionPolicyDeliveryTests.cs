using System.Security.Cryptography;
using System.Text.Json.Nodes;
using NPEduTools.Contracts;
using NPEduTools.Host;
using NPEduTools.Integrations.Npep;

namespace NPEduTools.Tests;

public sealed class SchoolActionPolicyDeliveryTests
{
    private static readonly string FirstId = new('a', 64), SecondId = new('b', 64);

    private static JsonObject Rule(string mode, string? factor = null) => new() { ["mode"] = mode, ["factor"] = factor };

    private static JsonObject Policy(long revision, string policyId, string source, JsonObject? stop = null, JsonObject? exit = null) => new()
    {
        ["schemaVersion"] = 1, ["revision"] = revision, ["policyId"] = policyId, ["source"] = source,
        ["actions"] = stop is null ? null : new JsonObject { ["stopScheduledMonitoring"] = stop, ["exitBackground"] = exit },
        ["usbKeys"] = new JsonArray()
    };

    private sealed class Screen : IAsyncDisposable
    {
        private readonly JsonObject _credential;
        private readonly string _directory = Path.Combine(Path.GetTempPath(), "classroom-actions-" + Guid.NewGuid().ToString("N"));
        private readonly bool _installed;
        public NoiseService Noise { get; private set; }
        public SchoolManagementService School { get; private set; }
        public string PolicyCache => Path.Combine(_installed ? ProtectedDirectory : _directory, "classroom-action-policy.json");
        public string ProtectedDirectory => Path.Combine(_directory, "machine");
        public string LegacyPolicyCache => Path.Combine(_directory, "classroom-action-policy.json");
        public readonly JsonObject Identity = new()
        {
            ["serverInstanceId"] = Guid.NewGuid().ToString(), ["deploymentEpoch"] = Guid.NewGuid().ToString(),
            ["deviceId"] = Guid.NewGuid().ToString(), ["bindingRevision"] = 1L, ["credentialGeneration"] = 1L
        };

        public Screen(bool installed = false)
        {
            _installed = installed;
            var salt = RandomNumberGenerator.GetBytes(16);
            _credential = new JsonObject { ["revision"] = 1L, ["verifier"] = new JsonObject { ["iterations"] = 210000,
                ["salt"] = Convert.ToBase64String(salt), ["hash"] = Convert.ToBase64String(Rfc2898DeriveBytes.Pbkdf2(
                    "fixture-class-secret", salt, 210000, HashAlgorithmName.SHA256, 32)) } };
            (Noise, School) = Load();
            School.BindSchool(Identity.ToJsonString());
        }

        private (NoiseService, SchoolManagementService) Load()
        {
            string? machine = _installed ? ProtectedDirectory : null;
            Func<string, bool>? authorize = _installed ? scope => scope == Identity.ToJsonString() : null;
            var noise = new NoiseService(_ => throw new InvalidOperationException("No classroom hardware in fixture"), () => [],
                directory: _directory, management: new NoiseManagementStore(_directory, null, machine, _installed, authorize));
            return (noise, new SchoolManagementService(noise, new StudyDeviceService(() => [], _directory), _directory,
                null, machine, _installed, authorize, _installed ? (_, origin) => origin == "https://fixture.example" : null));
        }

        // Recreate services from disk before making any simulated network reads.
        public async Task ReloadOffline()
        {
            await Noise.DisposeAsync();
            (Noise, School) = Load();
        }

        public JsonObject Envelope(JsonObject? policy, JsonObject? identity = null)
        {
            var envelope = new JsonObject { ["identity"] = (identity ?? Identity).DeepClone(), ["credential"] = _credential.DeepClone(),
                ["equipment"] = new JsonObject { ["revision"] = 0L, ["locked"] = false, ["microphoneId"] = null, ["cameraId"] = null } };
            if (policy is not null) envelope["actionPolicy"] = policy.DeepClone();
            return envelope;
        }

        public ClassroomActionPolicyState? Status()
        {
            var reply = Noise.ManagementHandle(new(Protocol.Version, Guid.NewGuid(), "noise.management.status.classroom-policy"));
            Assert.Equal("Succeeded", reply.Outcome);
            return Assert.IsType<NoiseProtectionState>(reply.NoiseProtection).ClassroomPolicy;
        }

        public JsonObject Receipt() => Assert.IsType<JsonObject>(School.ObserveSchool()["actionPolicyReceipt"]);

        public void AssertApplied(long revision, string policyId)
        {
            var receipt = Receipt();
            Assert.Equal(1, receipt["schemaVersion"]!.GetValue<int>());
            Assert.Equal(revision, receipt["revision"]!.GetValue<long>());
            Assert.Equal(policyId, receipt["policyId"]!.GetValue<string>());
            Assert.True(receipt.ContainsKey("error"));
            Assert.Null(receipt["error"]);
        }

        public async ValueTask DisposeAsync()
        {
            await Noise.DisposeAsync();
            if (Directory.Exists(_directory)) Directory.Delete(_directory, true);
        }
    }

    [Fact]
    public void Legacy_uninstall_evidence_requires_the_single_candidate_frozen_by_setup()
    {
        if (!OperatingSystem.IsWindows()) return;
        var one = new Dictionary<string, string> { ["original-user"] = FirstId };
        Assert.True(ManagedUninstall.MatchesLegacyEvidence(one, "original-user", FirstId));
        Assert.False(ManagedUninstall.MatchesLegacyEvidence(one, "other-user", FirstId));
        Assert.False(ManagedUninstall.MatchesLegacyEvidence(one, "original-user", SecondId));
        Assert.False(ManagedUninstall.MatchesLegacyEvidence(one, "original-user", null));
        Assert.False(ManagedUninstall.MatchesLegacyEvidence(null, "original-user", FirstId));
        one.Add("other-user", SecondId);
        Assert.False(ManagedUninstall.MatchesLegacyEvidence(one, "original-user", FirstId));
    }

    [Fact]
    public void An_unresolved_or_locally_detached_binding_does_not_release_policy_authority()
    {
        if (!OperatingSystem.IsWindows()) return;
        Assert.False(ManagedUninstall.CanUpdatePolicyAuthority("UNKNOWN", "UNKNOWN", false, false));
        Assert.False(ManagedUninstall.CanUpdatePolicyAuthority("UNKNOWN", "MANAGED", false, true));
        Assert.False(ManagedUninstall.CanUpdatePolicyAuthority("MANAGED", "MANAGED", false, false));
        Assert.True(ManagedUninstall.CanUpdatePolicyAuthority("MANAGED", "MANAGED", false, true));
        Assert.True(ManagedUninstall.CanUpdatePolicyAuthority("MANAGED", "MANAGED", true, false));
        Assert.True(ManagedUninstall.CanUpdatePolicyAuthority("PENDING", "PENDING", false, false));
    }

    [Fact]
    public async Task Installed_host_ignores_legacy_user_cache_and_restarts_offline_with_machine_policy()
    {
        await using var screen = new Screen(installed: true);
        var original = screen.Identity.ToJsonString();
        var open = Policy(100, SecondId, "CLASS", Rule("ALLOW"), Rule("ALLOW"));
        var forged = System.Text.Json.JsonSerializer.Serialize(new { SchemaVersion = 1, Scope = original, Policy = open, Hold = false });
        File.WriteAllText(screen.LegacyPolicyCache, forged);

        await screen.ReloadOffline();
        Assert.Equal("deny", screen.Status()!.ExitHost);
        Assert.NotNull(screen.Noise.VerifyCalibrationSecret("fixture-class-secret"));
        screen.School.ObserveActionBinding(original);
        screen.School.BindSchool(original);
        screen.School.ApplySchoolFrom(screen.Envelope(Policy(12, FirstId, "CLASS", Rule("DENY"), Rule("VERIFY", "CLASS_PASSWORD"))),
            "https://fixture.example");
        Assert.Equal("password", screen.Status()!.ExitHost);
        Assert.Null(screen.Noise.VerifyCalibrationSecret("fixture-class-secret"));

        File.WriteAllText(screen.LegacyPolicyCache, forged);
        await screen.ReloadOffline();
        // An unreadable, absent or mismatched profile is not a new authorization.
        screen.School.ObserveActionBinding(null);
        screen.School.BindSchool(null);
        Assert.Equal("password", screen.Status()!.ExitHost);
        Assert.Equal("deny", screen.Status()!.StopScheduled);
        screen.School.ObserveActionBinding("{}");
        screen.School.BindSchool("{}");
        Assert.Equal("password", screen.Status()!.ExitHost);
        Assert.Throws<NpepException>(() => screen.School.ApplySchoolFrom(screen.Envelope(open), "https://fixture.example"));
        screen.School.ObserveActionBinding(original);
        Assert.Throws<NpepException>(() => screen.School.ApplySchoolFrom(screen.Envelope(open), "https://other.example"));
        Assert.Equal("password", screen.Status()!.ExitHost);
    }

    [Fact]
    public async Task Same_revision_upgrade_restores_only_rows_projected_as_denied()
    {
        await using var screen = new Screen();
        var old = Policy(7, FirstId, "CLASS", Rule("DENY"), Rule("DENY"));
        old["schemaVersion"] = 2;
        old["actions"]!["switchClassroomMode"] = Rule("VERIFY", "USB");
        screen.School.ApplySchool(screen.Envelope(old));

        var full = old.DeepClone().AsObject();
        full["schemaVersion"] = 3;
        full["policyId"] = SecondId;
        full["actions"]!["exitBackground"] = null;
        screen.School.ApplySchool(screen.Envelope(full));
        Assert.Equal(3, screen.Receipt()["schemaVersion"]!.GetValue<int>());
        Assert.Equal(SecondId, screen.Receipt()["policyId"]!.GetValue<string>());

        await screen.ReloadOffline();
        var reply = screen.Noise.ManagementHandle(new(1, Guid.NewGuid(), "noise.management.status.classroom-policy.v2"));
        var policy = Assert.IsType<ClassroomActionPolicyState>(reply.NoiseProtection!.ClassroomPolicy);
        Assert.Equal(("deny", "default", "usb"), (policy.StopScheduled, policy.ExitHost, policy.SwitchClassroomMode));
        Assert.Null(screen.Noise.AuthorizeInterruption(new(1, Guid.NewGuid(), "host.stop")));
    }

    [Fact]
    public async Task Same_revision_upgrade_reveals_a_class_key_used_only_for_manual_study()
    {
        await using var screen = new Screen();
        var original = Policy(8, FirstId, "LEGACY");
        original["schemaVersion"] = 3;
        original["actions"] = new JsonObject
        {
            ["stopScheduledMonitoring"] = null, ["exitBackground"] = null, ["switchClassroomMode"] = null
        };
        screen.School.ApplySchool(screen.Envelope(original));

        var full = original.DeepClone().AsObject();
        full["schemaVersion"] = 4;
        full["policyId"] = SecondId;
        full["source"] = "CLASS";
        full["actions"]!["startManualStudy"] = Rule("VERIFY", "USB");
        full["usbKeys"]!.AsArray().Add(new JsonObject
        {
            ["keyId"] = Guid.NewGuid().ToString("D"), ["mediaHash"] = FirstId, ["secretHash"] = SecondId
        });
        screen.School.ApplySchool(screen.Envelope(full));
        Assert.Equal(4, screen.Receipt()["schemaVersion"]!.GetValue<int>());
        Assert.Equal(SecondId, screen.Receipt()["policyId"]!.GetValue<string>());
        Assert.Null(screen.Receipt()["error"]);

        screen.School.ApplySchool(screen.Envelope(original));
        Assert.False(screen.School.ObserveSchool().ContainsKey("actionPolicyReceipt"));
        await screen.ReloadOffline();
        var reply = screen.Noise.ManagementHandle(new(1, Guid.NewGuid(), "noise.management.status.classroom-policy.v4"));
        Assert.Equal("usb", reply.NoiseProtection!.ClassroomPolicy!.StartManualStudy);
    }

    [Fact]
    public async Task Same_revision_upgrade_adds_recording_without_replacing_manual_study()
    {
        await using var screen = new Screen();
        var original = Policy(9, FirstId, "GRADE", Rule("DENY"), Rule("ALLOW"));
        original["schemaVersion"] = 4;
        var previous = original["actions"]!.AsObject();
        previous["switchClassroomMode"] = null;
        previous["startManualStudy"] = Rule("DENY");
        screen.School.ApplySchool(screen.Envelope(original));

        var next = original.DeepClone().AsObject();
        next["schemaVersion"] = 5;
        next["policyId"] = SecondId;
        next["source"] = "CLASS";
        next["actions"]!["startManualRecording"] = Rule("VERIFY", "CLASS_PASSWORD");
        var conflicting = next.DeepClone().AsObject();
        conflicting["actions"]!["startManualStudy"] = Rule("ALLOW");
        screen.School.ApplySchool(screen.Envelope(conflicting));
        Assert.Equal(FirstId, screen.Receipt()["policyId"]!.GetValue<string>());
        Assert.NotNull(screen.Receipt()["error"]);

        screen.School.ApplySchool(screen.Envelope(next));
        Assert.Equal(5, screen.Receipt()["schemaVersion"]!.GetValue<int>());
        Assert.Null(screen.Receipt()["error"]);
        screen.School.ApplySchool(screen.Envelope(original));
        Assert.False(screen.School.ObserveSchool().ContainsKey("actionPolicyReceipt"));
        await screen.ReloadOffline();
        var reply = screen.Noise.ManagementHandle(new(1, Guid.NewGuid(), "noise.management.status.classroom-policy.v5"));
        var policy = Assert.IsType<ClassroomActionPolicyState>(reply.NoiseProtection!.ClassroomPolicy);
        Assert.Equal(("deny", "password"), (policy.StartManualStudy, policy.StartManualRecording));
    }

    [Fact]
    public async Task Upgrading_the_same_policy_revision_protects_local_mode_after_restart()
    {
        await using var screen = new Screen();
        var original = Policy(6, FirstId, "GRADE", Rule("ALLOW"), Rule("ALLOW"));
        screen.School.ApplySchool(screen.Envelope(original));
        var mode = new HostRequest(Protocol.Version, Guid.NewGuid(), "classroom.set", ExpectedRevision: 0, ClassroomMode: new("Exam"));
        Assert.Null(screen.Noise.AuthorizeInterruption(mode));

        var complete = original.DeepClone().AsObject();
        complete["schemaVersion"] = 2;
        complete["policyId"] = SecondId;
        complete["actions"]!["switchClassroomMode"] = Rule("DENY");
        screen.School.ApplySchool(screen.Envelope(complete));
        Assert.Equal(2, screen.Receipt()["schemaVersion"]!.GetValue<int>());
        Assert.Equal(SecondId, screen.Receipt()["policyId"]!.GetValue<string>());
        Assert.Null(screen.Status()!.SwitchClassroomMode);
        await screen.ReloadOffline();
        screen.School.BindSchool(screen.Identity.ToJsonString());
        screen.School.ApplySchool(screen.Envelope(original));
        Assert.Equal("CLASSROOM_ACTION_DENIED", screen.Noise.AuthorizeInterruption(mode)!.ErrorCode);
        Assert.Null(screen.Noise.AuthorizeInterruption(new(Protocol.Version, Guid.NewGuid(), "classroom.status")));
    }

    [Fact]
    public async Task Classroom_override_survives_lost_ack_offline_reload_and_duplicate_delivery_until_explicitly_replaced()
    {
        await using var screen = new Screen();
        var grade = Policy(6, FirstId, "GRADE", Rule("VERIFY", "CLASS_PASSWORD"), Rule("ALLOW"));
        var classroom = Policy(7, SecondId, "CLASS", Rule("ALLOW"), Rule("DENY"));
        screen.School.ApplySchool(screen.Envelope(grade));
        Assert.Equal("password", screen.Status()!.StopScheduled);
        screen.School.ApplySchool(screen.Envelope(classroom));
        screen.AssertApplied(7, SecondId);
        var applied = Assert.IsType<ClassroomActionPolicyState>(screen.Status());
        Assert.Equal(("allow", "deny"), (applied.StopScheduled, applied.ExitHost));

        await screen.ReloadOffline();
        var offline = Assert.IsType<ClassroomActionPolicyState>(screen.Status());
        Assert.Equal(("allow", "deny"), (offline.StopScheduled, offline.ExitHost));
        screen.School.BindSchool(screen.Identity.ToJsonString());
        screen.School.ApplySchool(screen.Envelope(classroom));
        screen.AssertApplied(7, SecondId);

        var legacy = Policy(8, new string('c', 64), "LEGACY");
        screen.School.ApplySchool(screen.Envelope(legacy));
        var released = screen.Status();
        Assert.True(released is null || released is { StopScheduled: "default", ExitHost: "default" });
        screen.AssertApplied(8, new string('c', 64));
    }

    [Fact]
    public async Task Malformed_class_verifier_cannot_keep_a_superseded_permission_active()
    {
        await using var screen = new Screen();
        screen.School.ApplySchool(screen.Envelope(Policy(10, FirstId, "GRADE", Rule("ALLOW"), Rule("ALLOW"))));
        var update = screen.Envelope(Policy(11, SecondId, "CLASS", Rule("DENY"), Rule("DENY")));
        update["credential"] = new JsonArray();
        screen.School.ApplySchool(update);
        Assert.Equal("INVALID_RESPONSE", screen.School.ObserveSchool()["error"]!.GetValue<string>());
        screen.AssertApplied(11, SecondId);
        await screen.ReloadOffline();
        Assert.Equal("deny", screen.Status()!.ExitHost);
    }

    [Theory]
    [InlineData("old-kv")]
    [InlineData("stale")]
    [InlineData("same-revision-different-policy")]
    [InlineData("same-id-different-rules")]
    [InlineData("unknown-factor")]
    public async Task Untrusted_or_legacy_update_cannot_turn_a_published_denial_into_permission(string variant)
    {
        await using var screen = new Screen();
        screen.School.ApplySchool(screen.Envelope(Policy(10, FirstId, "CLASS", Rule("DENY"), Rule("DENY"))));
        var candidate = variant switch
        {
            "old-kv" => null,
            "stale" => Policy(9, SecondId, "GRADE", Rule("ALLOW"), Rule("ALLOW")),
            "same-revision-different-policy" => Policy(10, SecondId, "CLASS", Rule("ALLOW"), Rule("ALLOW")),
            "same-id-different-rules" => Policy(11, FirstId, "CLASS", Rule("ALLOW"), Rule("ALLOW")),
            _ => Policy(11, SecondId, "CLASS", Rule("VERIFY", "UNKNOWN_FACTOR"), Rule("ALLOW"))
        };
        screen.School.ApplySchool(screen.Envelope(candidate));
        Assert.Equal("deny", screen.Status()!.StopScheduled);
        Assert.Equal("deny", screen.Status()!.ExitHost);
        if (candidate is null) Assert.False(screen.School.ObserveSchool().ContainsKey("actionPolicyReceipt"));
        else
        {
            Assert.Equal(FirstId, screen.Receipt()["policyId"]!.GetValue<string>());
            Assert.NotNull(screen.Receipt()["error"]);
        }
        await screen.ReloadOffline();
        Assert.Equal("deny", screen.Status()!.ExitHost);
    }

    [Fact]
    public async Task Unrelated_publication_updates_receipt_sequence_without_invalidating_this_classrooms_authorization_context()
    {
        await using var screen = new Screen();
        screen.School.ApplySchool(screen.Envelope(Policy(6, FirstId, "GRADE", Rule("VERIFY", "CLASS_PASSWORD"), Rule("DENY"))));
        var before = Assert.IsType<ClassroomActionPolicyState>(screen.Status());
        screen.School.ApplySchool(screen.Envelope(Policy(7, FirstId, "GRADE", Rule("VERIFY", "CLASS_PASSWORD"), Rule("DENY"))));
        var after = Assert.IsType<ClassroomActionPolicyState>(screen.Status());
        Assert.Equal(before, after);
        screen.AssertApplied(7, FirstId);
    }

    [Fact]
    public async Task A_failed_durable_write_reports_the_previous_version_then_retries_without_changing_other_equipment()
    {
        await using var screen = new Screen();
        var original = screen.Envelope(Policy(4, FirstId, "CLASS", Rule("ALLOW"), Rule("ALLOW")));
        var next = screen.Envelope(Policy(5, SecondId, "CLASS", Rule("ALLOW"), Rule("DENY")));
        screen.School.ApplySchool(original);
        var originalCache = File.ReadAllBytes(screen.PolicyCache);
        Directory.CreateDirectory(screen.PolicyCache + ".tmp");
        screen.School.ApplySchool(next);
        Assert.Equal(originalCache, File.ReadAllBytes(screen.PolicyCache));
        var receipt = screen.Receipt();
        Assert.NotEqual(SecondId, receipt["policyId"]?.GetValue<string>());
        Assert.Equal("CLASSROOM_POLICY_STORE_UNAVAILABLE", receipt["error"]!.GetValue<string>());
        Assert.Equal(("deny", "deny"), (screen.Status()!.StopScheduled, screen.Status()!.ExitHost));
        Directory.Delete(screen.PolicyCache + ".tmp");
        // A late response to a request for the original ALLOW rule is no proof of recovery.
        screen.School.ApplySchool(original);
        Assert.Equal("deny", screen.Status()!.ExitHost);
        Assert.NotNull(screen.Receipt()["error"]);
        screen.School.ApplySchool(next);
        screen.AssertApplied(5, SecondId);
        Assert.Equal(("allow", "deny"), (screen.Status()!.StopScheduled, screen.Status()!.ExitHost));
        Assert.Equal(0, screen.School.ObserveSchool()["appliedRevision"]!.GetValue<long>());
    }

    [Fact]
    public async Task Capability_of_an_old_school_server_does_not_change_unmanaged_installation_defaults()
    {
        await using var screen = new Screen();
        screen.School.ApplySchool(screen.Envelope(null));
        Assert.Null(screen.Status());
        Assert.False(screen.School.ObserveSchool().ContainsKey("actionPolicyReceipt"));
        await screen.ReloadOffline();
        Assert.Null(screen.Status());
    }

    [Fact]
    public async Task Corrupted_cache_requires_verified_policy_before_returning_to_ordinary_operation()
    {
        await using var screen = new Screen();
        var envelope = screen.Envelope(Policy(12, FirstId, "GRADE", Rule("ALLOW"), Rule("DENY")));
        screen.School.ApplySchool(envelope);
        File.WriteAllText(screen.PolicyCache, "{");
        await screen.ReloadOffline();
        Assert.Equal(("deny", "deny"), (screen.Status()!.StopScheduled, screen.Status()!.ExitHost));
        screen.School.BindSchool(screen.Identity.ToJsonString());
        screen.School.ApplySchool(screen.Envelope(null));
        Assert.Equal("deny", screen.Status()!.StopScheduled);
        screen.School.ApplySchool(envelope);
        screen.AssertApplied(12, FirstId);
        Assert.Equal("allow", screen.Status()!.StopScheduled);
    }

    [Fact]
    public async Task Known_rebinding_discards_old_permissions_and_late_original_scope_delivery()
    {
        await using var screen = new Screen();
        var oldPolicy = Policy(21, FirstId, "CLASS", Rule("ALLOW"), Rule("ALLOW"));
        screen.School.ApplySchool(screen.Envelope(oldPolicy));
        Assert.Equal("allow", screen.Status()!.ExitHost);
        var next = screen.Identity.DeepClone().AsObject(); next["bindingRevision"] = 2L;
        screen.School.BindSchool(next.ToJsonString());
        Assert.Equal("deny", screen.Status()!.ExitHost);
        var err = Assert.Throws<NpepException>(() => screen.School.ApplySchool(screen.Envelope(oldPolicy)));
        Assert.Equal("SESSION_SUPERSEDED", err.Code);
        Assert.Equal("deny", screen.Status()!.ExitHost);

        var correct = Policy(1, SecondId, "GRADE", Rule("DENY"), Rule("ALLOW"));
        screen.School.ApplySchool(screen.Envelope(correct, next));
        Assert.Equal(("deny", "allow"), (screen.Status()!.StopScheduled, screen.Status()!.ExitHost));
        screen.AssertApplied(1, SecondId);
    }

    [Fact]
    public async Task Confirmed_current_registration_has_precedence_over_a_stale_http_cycle_or_missing_offline_registration()
    {
        await using var screen = new Screen();
        string scope = screen.Identity.ToJsonString();
        var original = screen.Envelope(Policy(3, FirstId, "GRADE", Rule("ALLOW"), Rule("ALLOW")));
        screen.School.ObserveActionBinding(scope);
        screen.School.ApplySchool(original);
        Assert.Equal("allow", screen.Status()!.ExitHost);

        // A local unpair/revoke or damaged registration observed by native startup wins over an already-started poll.
        screen.School.ObserveActionBinding(null);
        screen.School.BindSchool(scope);
        var error = Assert.Throws<NpepException>(() => screen.School.ApplySchool(original));
        Assert.Equal("SESSION_SUPERSEDED", error.Code);
        Assert.Equal("deny", screen.Status()!.ExitHost);
        await screen.ReloadOffline();
        screen.School.ObserveActionBinding(null);
        Assert.Equal("deny", screen.Status()!.ExitHost);
        var next = screen.Identity.DeepClone().AsObject(); next["deviceId"] = Guid.NewGuid().ToString();
        screen.School.ObserveActionBinding(next.ToJsonString());
        screen.School.BindSchool(next.ToJsonString());
        screen.School.ApplySchool(screen.Envelope(Policy(4, SecondId, "CLASS", Rule("ALLOW"), Rule("DENY")), next));
        screen.AssertApplied(4, SecondId);
    }
}

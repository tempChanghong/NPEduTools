using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using NPEduTools.Contracts;
using NPEduTools.Host;

namespace NPEduTools.Tests;

public sealed partial class NoiseCalibrationTests
{
    private static async Task ConfirmQuick(Fixture f)
    {
        Assert.Equal("Succeeded", f.Command("begin").Outcome);
        await f.SampleAsync("Acceptable", -55); await f.SampleAsync("Discipline", -20);
        Assert.Equal("Succeeded", f.Command("confirm", medium: -40, high: -30).Outcome);
    }

    [Fact]
    public async Task Empty_v2_store_is_readable_and_can_begin_a_new_draft()
    {
        await using var f = new Fixture();
        string path = Path.Combine(f.DirectoryPath, "noise-quick-calibration.json");
        const string empty = "{\"version\":2,\"draft\":null,\"profile\":null}";
        File.WriteAllText(path, empty);
        var state = f.State;
        Assert.Null(state.Error); Assert.Null(state.Profile); Assert.Null(state.DraftId);
        Assert.Empty(state.Samples); Assert.False(state.Applied);
        Assert.Equal(empty, File.ReadAllText(path)); Assert.Equal(0, f.Creates);
        Assert.Equal("Succeeded", f.Command("begin").Outcome);
        Assert.NotNull(f.State.DraftId);
        var stored = JsonNode.Parse(File.ReadAllText(path))!;
        Assert.Equal(2, stored["version"]!.GetValue<int>());
        Assert.Equal(2, stored["draft"]!["contextVersion"]!.GetValue<int>());
    }

    [Fact]
    public async Task Clearing_v2_profile_stays_cleared_after_reload_and_keeps_samples()
    {
        await using var f = new Fixture(); await ConfirmQuick(f);
        var draftId = f.State.DraftId;
        Assert.Equal("Succeeded", f.Command("clear").Outcome);
        await using var reloaded = new NoiseService(_ => new Capture(), () => [], f.Time, f.DirectoryPath, f.Management);
        var state = reloaded.CalibrationHandle(new(1, Guid.NewGuid(), "noise.calibration.status"), Scope, Study).NoiseCalibration!;
        Assert.Null(state.Error); Assert.Null(state.Profile); Assert.False(state.Applied);
        Assert.Equal(draftId, state.DraftId); Assert.Equal(2, state.Samples.Count);
        Assert.Equal(Policy, reloaded.ResolveCalibration(Scope, Study.Version, Policy));
        Assert.Equal(2, JsonNode.Parse(File.ReadAllText(Path.Combine(f.DirectoryPath,
            "noise-quick-calibration.json")))!["version"]!.GetValue<int>());
    }

    [Theory]
    [InlineData("homework")]
    [InlineData("presentation")]
    [InlineData("schedule")]
    [InlineData("reminder-timing")]
    public async Task Non_acoustic_school_changes_keep_confirmed_lines_and_the_sampling_draft(string changed)
    {
        await using var f = new Fixture(); await ConfirmQuick(f);
        var settings = Study.Settings!;
        settings = changed switch
        {
            "homework" => settings with { Homework = settings.Homework with { RotationSeconds = 120 } },
            "presentation" => settings with { Presentation = settings.Presentation with { NoiseDisplay = "Status" } },
            "schedule" => settings with { Schedule = settings.Schedule with { EffectiveFrom = "2026-10-12" } },
            _ => settings with { Noise = Policy with { DisciplineCooldownSeconds = 900, HighContinuousSeconds = 3 } }
        };
        var updated = Study with { Version = new string('b', 64), Settings = settings };
        var before = f.State;
        var after = f.Noise.CalibrationHandle(new(1, Guid.NewGuid(), "noise.calibration.status"), Scope, updated).NoiseCalibration!;
        Assert.Equal(before.Context, after.Context); Assert.Equal(before.Revision, after.Revision);
        Assert.Equal(before.DraftId, after.DraftId); Assert.Equal(before.Samples, after.Samples); Assert.True(after.Applied);
        var effective = f.Noise.ResolveCalibration(Scope, updated.Version, settings.Noise);
        Assert.Equal(-40, effective.MediumLineDbfs); Assert.Equal(-30, effective.RedLineDbfs);
        Assert.Equal(settings.Noise.DisciplineCooldownSeconds, effective.DisciplineCooldownSeconds);
        Assert.Equal(settings.Noise.HighContinuousSeconds, effective.HighContinuousSeconds);
        Assert.Equal(("Saved", (string?)null), (f.Noise.CalibrationStatusSnapshot(Scope, updated).State,
            f.Noise.CalibrationStatusSnapshot(Scope, updated).Reason));
        Assert.Equal("Succeeded", f.Command("confirm", medium: -40, high: -30, study: updated).Outcome);
        Assert.Equal(2, f.Creates); // No resampling or new device is needed.
    }

    [Theory]
    [InlineData("medium")]
    [InlineData("high")]
    [InlineData("estimated")]
    public async Task School_threshold_or_unit_changes_still_withdraw_old_quick_overrides(string changed)
    {
        await using var f = new Fixture(); await ConfirmQuick(f);
        var policy = changed switch
        {
            "medium" => Policy with { MediumLineDbfs = -28 },
            "high" => Policy with { RedLineDbfs = -12 },
            _ => Policy with { Estimated = new(65, 50) }
        };
        var state = Study with { Version = new string('b', 64), Settings = Study.Settings! with { Noise = policy } };
        Assert.Equal(policy, f.Noise.ResolveCalibration(Scope, state.Version, policy));
        var status = f.Noise.CalibrationStatusSnapshot(Scope, state);
        Assert.Equal(("Invalidated", "SCHOOL_CONFIGURATION_CHANGED"), (status.State, status.Reason));
        Assert.Equal("CALIBRATION_CHANGED", f.Command("confirm", medium: -40, high: -30, study: state).ErrorCode);
        Assert.Equal(2, f.State.Samples.Count); Assert.NotNull(f.State.Profile);
    }

    [Fact]
    public async Task Binding_or_missing_confirmed_policy_cannot_reuse_another_quick_context()
    {
        await using var f = new Fixture(); await ConfirmQuick(f);
        Assert.Equal(Policy, f.Noise.ResolveCalibration("replacement-binding", Study.Version, Policy));
        Assert.Equal(Policy, f.Noise.ResolveCalibration(null, Study.Version, Policy));
        Assert.Equal(Policy, f.Noise.ResolveCalibration(Scope, null, Policy));
        var status = f.Noise.CalibrationStatusSnapshot(Scope, Study with { Settings = null });
        Assert.Equal(("Saved", "SCHOOL_CONFIGURATION_UNCONFIRMED"), (status.State, status.Reason));
        Assert.False(f.Noise.CalibrationHandle(new(1, Guid.NewGuid(), "noise.calibration.status"), Scope,
            Study with { PolicyState = "UNAVAILABLE" }).NoiseCalibration!.Applied);
    }

    [Fact]
    public async Task Manual_session_keeps_real_threshold_statistics_across_unrelated_version_then_honors_school_threshold_edit()
    {
        await using var f = new Fixture(); await ConfirmQuick(f); var school = Study;
        await using var manual = new ManualStudyService(f.Noise, () => (Scope, true, school), () => false,
            Path.Combine(f.DirectoryPath, "manual"), f.Time, false);
        var initial = manual.Status();
        Assert.Equal("Succeeded", manual.Handle(new(1, Guid.NewGuid(), "manual.study.command", ManualStudy:
            new("start", initial.InstanceId, initial.Revision, DeviceId: "mic"))).Outcome);
        await Until(() => f.Noise.Snapshot().State == "Active"); manual.Tick();
        var session = f.Noise.Snapshot().SessionId;
        void Frames() { for (int i = 0; i < 50; i++) { f.Time.Milliseconds += 100; f.Capture!.Emit(-20); manual.Tick(); } }
        Frames();
        Assert.Equal(5, f.Noise.StudyReportSnapshot()!.Raw.HighSeconds, 6);
        school = school with { Version = new string('b', 64), Settings = school.Settings! with {
            Homework = school.Settings!.Homework with { RotationSeconds = 120 } } };
        manual.Tick(); Frames();
        Assert.Equal(10, f.Noise.StudyReportSnapshot()!.Raw.HighSeconds, 6);
        school = school with { Version = new string('c', 64), Settings = school.Settings! with {
            Noise = Policy with { RedLineDbfs = -10 } } };
        manual.Tick(); Frames();
        var report = f.Noise.StudyReportSnapshot()!;
        Assert.Equal(15, report.Raw.SampledSeconds, 6); Assert.Equal(10, report.Raw.HighSeconds, 6);
        Assert.Equal(session, f.Noise.Snapshot().SessionId); Assert.Equal("Active", f.Noise.Snapshot().State);
        Assert.Equal(3, f.Creates); // Two calibration samples, one continuous manual capture.
    }

    private static string WriteLegacyQuick(Fixture f)
    {
        string path = Path.Combine(f.DirectoryPath, "noise-quick-calibration.json");
        var saved = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        string legacy = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(Scope + "\n" + Study.Version)));
        saved["version"] = 1;
        saved["profile"]!["version"] = 1; saved["profile"]!["context"] = legacy;
        saved["draft"]!["context"] = legacy; saved["draft"]!.AsObject().Remove("contextVersion");
        File.WriteAllText(path, saved.ToJsonString()); return saved.ToJsonString();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Legacy_upgrade_preserves_samples_and_profile_only_when_the_old_full_context_is_proven(bool matches)
    {
        await using var f = new Fixture(); await ConfirmQuick(f);
        var originalProfile = f.State.Profile!; var originalDraft = f.State.DraftId;
        string legacy = WriteLegacyQuick(f), path = Path.Combine(f.DirectoryPath, "noise-quick-calibration.json");
        Capture? capture = null;
        await using var reloaded = new NoiseService(_ => capture = new(), () => [new("mic", "Synthetic")], f.Time, f.DirectoryPath, f.Management);
        var current = matches ? Study : Study with { Version = new string('b', 64) };
        var state = reloaded.CalibrationHandle(new(1, Guid.NewGuid(), "noise.calibration.status"), Scope, current).NoiseCalibration!;
        Assert.Null(state.Error); Assert.False(state.Applied); Assert.Equal(2, state.Samples.Count);
        Assert.Equal(originalDraft, state.DraftId); Assert.Equal(originalProfile.ConfirmedAt, state.Profile!.ConfirmedAt);
        Assert.Equal(originalProfile.MediumDbfs, state.Profile.MediumDbfs); Assert.Equal(originalProfile.HighDbfs, state.Profile.HighDbfs);
        Assert.Equal(matches ? 2 : 1, state.Profile.Version);
        if (!matches) Assert.Equal(legacy, File.ReadAllText(path));
        else Assert.Equal(2, JsonNode.Parse(File.ReadAllText(path))!["version"]!.GetValue<int>());
        Assert.Equal("Accepted", reloaded.StartScheduled(Guid.NewGuid()).Outcome);
        await Until(() => capture is { Started: true, Disposed: false } && reloaded.Snapshot().State == "Active");
        f.Time.Milliseconds += 100; capture!.Emit(-20);
        Assert.Equal(matches ? -40 : Policy.MediumLineDbfs, reloaded.ResolveCalibration(Scope, current.Version, Policy).MediumLineDbfs);
        // Successful migration survives subsequent unrelated updates without the v1 context.
        Assert.Equal(matches ? -40 : Policy.MediumLineDbfs, reloaded.ResolveCalibration(Scope, new string('c', 64), Policy).MediumLineDbfs);
        if (!matches) Assert.Equal(legacy, File.ReadAllText(path));
    }

    [Fact]
    public async Task Legacy_migration_write_failure_preserves_original_and_requires_successful_reload()
    {
        await using var f = new Fixture(); await ConfirmQuick(f);
        string legacy = WriteLegacyQuick(f), path = Path.Combine(f.DirectoryPath, "noise-quick-calibration.json");
        await using (var reloaded = new NoiseService(_ => new Capture(), () => [], f.Time, f.DirectoryPath, f.Management))
        using (var locked = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            var state = reloaded.CalibrationHandle(new(1, Guid.NewGuid(), "noise.calibration.status"), Scope, Study).NoiseCalibration!;
            Assert.Equal("CALIBRATION_STORE_UNAVAILABLE", state.Error); Assert.Equal(1, state.Profile!.Version);
            Assert.False(state.Applied); Assert.Equal(Policy, reloaded.ResolveCalibration(Scope, Study.Version, Policy));
            Assert.Equal(legacy, File.ReadAllText(path));
        }
        await using var retry = new NoiseService(_ => new Capture(), () => [], f.Time, f.DirectoryPath, f.Management);
        var restored = retry.CalibrationHandle(new(1, Guid.NewGuid(), "noise.calibration.status"), Scope, Study).NoiseCalibration!;
        Assert.Null(restored.Error); Assert.Equal(2, restored.Profile!.Version); Assert.Equal(2, restored.Samples.Count);
        Assert.False(restored.Applied); // Reload alone never proves the live microphone signature.
    }
}

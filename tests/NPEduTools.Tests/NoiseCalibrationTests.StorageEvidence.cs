using System.Text.Json.Nodes;
using NPEduTools.Contracts;
using NPEduTools.Host;

namespace NPEduTools.Tests;

public sealed partial class NoiseCalibrationTests
{
    [Theory]
    [InlineData("interrupted-profile")]
    [InlineData("unknown-profile-quality")]
    [InlineData("missing-acceptable")]
    [InlineData("missing-discipline")]
    [InlineData("duplicate-profile-id")]
    [InlineData("duplicate-draft-id")]
    [InlineData("unknown-draft-quality")]
    public async Task ReloadCannotApplyQuickThresholdsFromInvalidConfirmationEvidence(string variant)
    {
        await using var f = new Fixture();
        Assert.Equal("Succeeded", f.Command("begin").Outcome);
        await f.SampleAsync("Acceptable", -55); await f.SampleAsync("Discipline", -20);
        Assert.Equal("Succeeded", f.Command("confirm", medium: -40, high: -30).Outcome);
        string path = Path.Combine(f.DirectoryPath, "noise-quick-calibration.json");
        var saved = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        var profile = saved["profile"]!["samples"]!.AsArray();
        var draft = saved["draft"]!["samples"]!.AsArray();
        switch (variant)
        {
            case "interrupted-profile": profile[0]!["quality"] = "Interrupted"; break;
            case "unknown-profile-quality": profile[0]!["quality"] = "UnknownQuality"; break;
            case "missing-acceptable": profile[0]!["label"] = "Discipline"; break;
            case "missing-discipline": profile[1]!["label"] = "Acceptable"; break;
            case "duplicate-profile-id": profile[1]!["id"] = profile[0]!["id"]!.DeepClone(); break;
            case "duplicate-draft-id": draft[1]!["id"] = draft[0]!["id"]!.DeepClone(); break;
            default: draft[0]!["quality"] = "UnknownQuality"; break;
        }
        string original = saved.ToJsonString(); File.WriteAllText(path, original);
        Capture? capture = null;
        await using var reloaded = new NoiseService(_ => capture = new Capture(), () => [new("mic", "Synthetic")],
            f.Time, f.DirectoryPath, f.Management);
        var statusRequest = new HostRequest(1, Guid.NewGuid(), "noise.calibration.status");
        var loaded = reloaded.CalibrationHandle(statusRequest, Scope, Study).NoiseCalibration!;
        var state = reloaded.Snapshot();
        Assert.Equal("Accepted", reloaded.Handle(new(1, Guid.NewGuid(), "noise.command",
            Noise: new("start", state.InstanceId, state.Revision, "mic"))).Outcome);
        await Until(() => capture is { Started: true, Disposed: false } && reloaded.Snapshot().State == "Active");
        // A matching fresh input signature must not make the malformed saved evidence valid.
        f.Time.Milliseconds += 100; capture!.Emit(-50);
        var resolved = reloaded.ResolveCalibration(Scope, Study.Version, Policy);
        Assert.True(loaded.Error == "CALIBRATION_STORE_UNAVAILABLE" && loaded.Profile is null && !loaded.Applied && resolved == Policy,
            $"{variant}: malformed quick store remained trusted; error={loaded.Error ?? "null"}, profile={loaded.Profile is not null}, resolved={resolved.MediumLineDbfs}/{resolved.RedLineDbfs}");
        Assert.Equal(original, File.ReadAllText(path));
        var command = new NoiseCalibrationCommand("begin", loaded.InstanceId, loaded.Revision, Secret);
        Assert.Equal("CALIBRATION_STORE_UNAVAILABLE", reloaded.CalibrationHandle(new(1, Guid.NewGuid(), "noise.calibration.command",
            NoiseCalibration: command), Scope, Study).ErrorCode);
    }

    [Fact]
    public async Task ReloadPreservesManualOverlapConfirmationAndValidInterruptedDraftSamples()
    {
        await using var f = new Fixture(); f.Command("begin");
        await f.SampleAsync("Acceptable", -40); await f.SampleAsync("Discipline", -39);
        Assert.False(f.State.Suggestion.Ready);
        Assert.Equal("Succeeded", f.Command("confirm", medium: -35, high: -25).Outcome);
        Assert.Equal("Accepted", f.Command("start", "Visual").Outcome);
        await Until(() => f.Capture is { Started: true, Disposed: false });
        Assert.Equal("Succeeded", f.Command("cancel").Outcome);
        await Until(() => !f.Noise.CaptureBusy);
        Assert.Equal("Interrupted", f.State.Samples.Last().Quality);
        string path = Path.Combine(f.DirectoryPath, "noise-quick-calibration.json");
        string original = File.ReadAllText(path);
        Capture? capture = null;
        await using var reloaded = new NoiseService(_ => capture = new Capture(), () => [new("mic", "Synthetic")],
            f.Time, f.DirectoryPath, f.Management);
        var loaded = reloaded.CalibrationHandle(new(1, Guid.NewGuid(), "noise.calibration.status"), Scope, Study).NoiseCalibration!;
        Assert.Null(loaded.Error); Assert.NotNull(loaded.Profile); Assert.Equal(3, loaded.Samples.Count);
        Assert.False(loaded.Applied); // No fresh input signature yet.
        var state = reloaded.Snapshot();
        reloaded.Handle(new(1, Guid.NewGuid(), "noise.command", Noise: new("start", state.InstanceId, state.Revision, "mic")));
        await Until(() => capture is { Started: true, Disposed: false } && reloaded.Snapshot().State == "Active");
        f.Time.Milliseconds += 100; capture!.Emit(-50);
        Assert.Equal(-35, reloaded.ResolveCalibration(Scope, Study.Version, Policy).MediumLineDbfs);
        Assert.Equal(-25, reloaded.ResolveCalibration(Scope, Study.Version, Policy).RedLineDbfs);
        Assert.Equal(original, File.ReadAllText(path));
    }
}

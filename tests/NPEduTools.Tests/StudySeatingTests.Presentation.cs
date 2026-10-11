using System.Text.Json.Nodes;
using NPEduTools.Contracts;
using NPEduTools.Host;

namespace NPEduTools.Tests;

public sealed partial class StudySeatingTests
{
    private sealed class PresentationFixture : IAsyncDisposable
    {
        public readonly Clock Time = new();
        public readonly StudySettingsService Service;
        public readonly StudyHostState Display;
        public readonly JsonObject Manifest;
        public readonly string Hash;
        public PresentationFixture()
        {
            var config = JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures/study-device-cases.json")))![0]!["value"]!.DeepClone().AsObject();
            long sequence = 0; var connection = Guid.NewGuid(); var bridge = Guid.NewGuid();
            var now = new DateTimeOffset(2026, 10, 12, 18, 58, 0, TimeSpan.Zero);
            Service = new(() => new(connection, bridge, ++sequence, 0, now.AddMilliseconds(Time.Ms), 0, "Advancing", "synthetic"), () => false, Time, false);
            Service.BindStudy(config["identity"]!.ToJsonString()); Service.ApplyStudy(config);
            Time.Ms += 500; Service.ApplyStudy(config); Time.Ms += 500; Service.ApplyStudy(config);
            var bytes = new byte[100]; Hash = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes));
            Manifest = new() { ["version"] = Service.Snapshot().Version, ["classId"] = config["scope"]!["classId"]!.DeepClone(), ["revision"] = 1,
                ["asset"] = new JsonObject { ["hash"] = Hash, ["byteCount"] = bytes.Length, ["width"] = 100, ["height"] = 100, ["chunks"] = 1 } };
            Service.ApplySeating(Manifest, bytes);
            Display = Service.Snapshot() with { Display = new(Guid.NewGuid(), true, true, 0) };
        }
        public HostResponse Pulse(Guid app, long sequence, bool visible, int? renderAge = null) => Service.ReceiveSeating(
            new(1, Guid.NewGuid(), "study.seating.presentation", SeatingPresentation: new(Display.Display!.InstanceId, app, sequence,
                Display.Version!, visible ? 1 : 0, visible ? Hash : null, visible, renderAge)), Display);
        public ValueTask DisposeAsync() => Service.DisposeAsync();
    }

    [Fact]
    public async Task HiddenAppCannotOverwriteAnotherAppsFreshVisibleSeatingObservation()
    {
        await using var f = new PresentationFixture(); var shown = Guid.NewGuid(); var hidden = Guid.NewGuid();
        Assert.Equal("Succeeded", f.Pulse(shown, 1, true, 100).Outcome);
        f.Time.Ms += 100; Assert.Equal("Succeeded", f.Pulse(hidden, 1, false).Outcome);
        var observed = f.Service.ObserveSeating()!;
        Assert.True(observed["visible"]!.GetValue<bool>());
        Assert.Equal(shown.ToString(), observed["appInstanceId"]!.GetValue<string>());
        Assert.Equal(f.Hash, observed["appliedHash"]!.GetValue<string>());
        Assert.Equal(200, observed["renderAgeMs"]!.GetValue<int>());
        // At the exact render-age boundary, the newer hidden observation is the fallback.
        f.Time.Ms += 800;
        observed = f.Service.ObserveSeating()!;
        Assert.False(observed["visible"]!.GetValue<bool>());
        Assert.Equal(hidden.ToString(), observed["appInstanceId"]!.GetValue<string>());
        f.Time.Ms += 200; Assert.Null(f.Service.ObserveSeating());
    }

    [Fact]
    public async Task NewerStaleRenderedAppCannotReplaceAnOlderFreshVisibleApp()
    {
        await using var f = new PresentationFixture(); var fresh = Guid.NewGuid(); var stale = Guid.NewGuid();
        f.Pulse(fresh, 1, true, 0); f.Time.Ms += 50; f.Pulse(stale, 1, true, 999); f.Time.Ms += 2;
        var observed = f.Service.ObserveSeating()!;
        Assert.True(observed["visible"]!.GetValue<bool>());
        Assert.Equal(fresh.ToString(), observed["appInstanceId"]!.GetValue<string>());
        Assert.Equal(52, observed["renderAgeMs"]!.GetValue<int>());
    }

    [Fact]
    public async Task PerAppSequenceFenceAndBoundedClientsPreserveFreshVisibleObservation()
    {
        await using var f = new PresentationFixture(); var shown = Guid.NewGuid();
        Assert.Equal("Succeeded", f.Pulse(shown, 2, true, 0).Outcome);
        Assert.Equal("Rejected", f.Pulse(shown, 1, false).Outcome);
        for (int i = 0; i < 15; i++) Assert.Equal("Succeeded", f.Pulse(Guid.NewGuid(), 1, false).Outcome);
        Assert.Equal("TOO_MANY_APPS", f.Pulse(Guid.NewGuid(), 1, false).ErrorCode);
        Assert.Equal(shown.ToString(), f.Service.ObserveSeating()!["appInstanceId"]!.GetValue<string>());
        f.Time.Ms += 1000; Assert.Null(f.Service.ObserveSeating());
        Assert.Equal("Rejected", f.Pulse(shown, 2, true, 0).Outcome); // Expiration must not forget sequence tombstones.
        Assert.Equal("Succeeded", f.Pulse(shown, 3, true, 0).Outcome);
    }

    [Fact]
    public async Task ClosingTheOnlyVisibleAppWithdrawsItsPreviousVisibleObservation()
    {
        await using var f = new PresentationFixture(); var app = Guid.NewGuid();
        f.Pulse(app, 1, true, 0); f.Time.Ms += 100;
        Assert.Equal("Succeeded", f.Pulse(app, 2, false).Outcome);
        Assert.False(f.Service.ObserveSeating()!["visible"]!.GetValue<bool>());
    }

    [Fact]
    public async Task WithdrawalClearsEveryAppsSeatingObservation()
    {
        await using var f = new PresentationFixture(); f.Pulse(Guid.NewGuid(), 1, true, 0); f.Pulse(Guid.NewGuid(), 1, false);
        var withdrawal = f.Manifest.DeepClone().AsObject(); withdrawal["revision"] = 2; withdrawal["asset"] = null;
        f.Service.ApplySeating(withdrawal, null);
        Assert.Null(f.Service.ObserveSeating());
        Assert.Equal("NOT_PUBLISHED", f.Service.Snapshot().Seating!.State);
    }
}

using System.Text.Json.Nodes;
using NPEduTools.Contracts;
using NPEduTools.Core;
using NPEduTools.Host;
using NPEduTools.Integrations.Npep;

namespace NPEduTools.Tests;
public sealed class NoiseTransportTests
{
    private sealed class Capture : INoiseCapture
    {
        public string DeviceName => "Synthetic mic";
        public int Starts; public bool Disposed;
        public event Action<NoiseFrame>? Frame;
        public event Action<string>? Failed;
        public void Start() { Starts++; Frame?.Invoke(new(0.1, 0.25, 0.5, 0, false)); }
        public void Dispose() { Disposed = true; }
        public void Fail() => Failed?.Invoke("Synthetic failure");
    }
    private static string DirectoryName() => Path.Combine(Path.GetTempPath(), "NPEduTools.Tests", Guid.NewGuid().ToString("N"));
    private static void Select(NoiseService service) { var s = service.Snapshot(); Assert.Equal("Succeeded", service.Handle(new(Protocol.Version, Guid.NewGuid(), "noise.command", Noise: new("select", s.InstanceId, s.Revision, "mic"))).Outcome); }
    private static JsonObject Command(NoiseService service, string action = "START") {
        var s = service.Snapshot(); return new() { ["commandId"] = Guid.NewGuid().ToString("D"), ["action"] = action, ["instanceId"] = s.InstanceId.ToString("D"),
            ["revision"] = s.Revision, ["sessionId"] = s.SessionId?.ToString("D"), ["durationSeconds"] = 60, ["expiresAt"] = DateTimeOffset.UtcNow.AddSeconds(30).ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'") };
    }
    private static async Task Until(Func<bool> condition) { using var stop = new CancellationTokenSource(5000); while (!condition()) await Task.Delay(10, stop.Token); }
    [Fact]
    public async Task Duplicate_start_only_opens_once_and_statistics_survive_offline_restart()
    {
        var capture = new Capture(); var directory = DirectoryName();
        await using var service = new NoiseService(_ => capture, () => []);
        Select(service); var transport = new NoiseTransport(service, directory); transport.Bind("class-A");
        var command = Command(service); transport.Execute(command, () => {}); transport.Execute(command, () => {});
        await Until(() => service.Snapshot().State == "Active"); Assert.Equal(1, capture.Starts);
        NpepNoiseProtocol.Validate("status", transport.Observe());
        transport.Execute(Command(service, "STOP"), () => {});
        await Until(() => transport.Reports().Count == 1); Assert.True(capture.Disposed);
        var report = (JsonObject)transport.Reports()[0]!; NpepNoiseProtocol.Validate("report", report);
        await using var restarted = new NoiseService(_ => new Capture(), () => []);
        var persisted = new NoiseTransport(restarted, directory); Assert.Single(persisted.Reports());
        persisted.Bind("class-B"); Assert.Empty(persisted.Reports());
    }
    [Fact]
    public async Task Stale_stop_does_not_stop_a_later_session_and_expired_authorization_has_no_effect()
    {
        var capture = new Capture(); await using var service = new NoiseService(_ => capture, () => []);
        Select(service); var transport = new NoiseTransport(service, DirectoryName()); transport.Bind("class-A");
        Assert.Throws<NpepException>(() => transport.Execute(Command(service), () => throw new NpepException("COMMAND_EXPIRED")));
        Assert.Equal(0, capture.Starts); transport.Execute(Command(service), () => {});
        await Until(() => service.Snapshot().State == "Active");
        var stop = Command(service, "STOP"); stop["sessionId"] = Guid.NewGuid().ToString("D");
        transport.Execute(stop, () => {}); Assert.Equal("Active", service.Snapshot().State);
        Assert.Equal("REJECTED", transport.Receipts().Last()!["outcome"]!.GetValue<string>());
    }
    [Fact]
    public async Task Binding_change_during_capture_does_not_relabel_the_old_session()
    {
        var capture = new Capture(); await using var service = new NoiseService(_ => capture, () => []);
        Select(service); var transport = new NoiseTransport(service, DirectoryName()); transport.Bind("class-A");
        transport.Execute(Command(service), () => {}); await Until(() => service.Snapshot().State == "Active");
        transport.Bind("class-B");
        await Until(() => service.Snapshot().State == "Stopped");
        Assert.Empty(transport.Reports()); Assert.True(capture.Disposed);
    }
    [Fact]
    public async Task Mic_selection_persists_without_starting_and_corrupt_outbox_blocks_remote_start()
    {
        var directory = DirectoryName(); var capture = new Capture();
        await using var service = new NoiseService(_ => capture, () => [], directory: directory); Select(service);
        Assert.Equal(0, capture.Starts);
        await using var restarted = new NoiseService(_ => capture, () => [], directory: directory);
        Assert.Equal("mic", restarted.Snapshot().SelectedDeviceId);
        File.WriteAllText(Path.Combine(directory, "noise-outbox.json"), "broken");
        var transport = new NoiseTransport(restarted, directory);
        Assert.Equal("NOISE_STORE_UNAVAILABLE", transport.Observe()["uploadError"]!.GetValue<string>());
        transport.Execute(Command(restarted), () => {}); Assert.Equal(0, capture.Starts);
    }

    [Fact]
    public async Task Crash_checkpoint_becomes_one_interrupted_report_and_never_restarts_capture()
    {
        string original = DirectoryName(), crashed = DirectoryName();
        Directory.CreateDirectory(crashed);
        try
        {
            Guid? session;
            await using (var service = new NoiseService(_ => new Capture(), () => []))
            {
                Select(service); var transport = new NoiseTransport(service, original); transport.Bind("class-A");
                transport.Execute(Command(service), () => {});
                await Until(() => service.Snapshot().State == "Active"); session = service.Snapshot().SessionId;
                transport.Observe();
                File.Copy(Path.Combine(original, "noise-outbox.json"), Path.Combine(crashed, "noise-outbox.json"));
            }
            int captures = 0;
            await using var restarted = new NoiseService(_ => { captures++; return new Capture(); }, () => []);
            var recovered = new NoiseTransport(restarted, crashed); recovered.Bind("class-A");
            var report = Assert.IsType<JsonObject>(Assert.Single(recovered.Reports()));
            Assert.Equal(session!.Value.ToString("D"), report["sessionId"]!.GetValue<string>());
            Assert.Equal("Interrupted", report["outcome"]!.GetValue<string>());
            Assert.Equal(1, report["summary"]!["frames"]!.GetValue<long>());
            var loadedAgain = new NoiseTransport(restarted, crashed);
            Assert.True(JsonNode.DeepEquals(report, Assert.Single(loadedAgain.Reports())));
            Assert.Equal(0, captures); Assert.Equal("Idle", restarted.Snapshot().State);
        }
        finally { if (Directory.Exists(original)) Directory.Delete(original, true); Directory.Delete(crashed, true); }
    }

    [Fact]
    public async Task Lost_report_ack_survives_restart_and_duplicate_ack_does_not_remove_a_later_report()
    {
        string directory = DirectoryName();
        try
        {
            JsonObject first;
            await using (var service = new NoiseService(_ => new Capture(), () => []))
            {
                Select(service); var transport = new NoiseTransport(service, directory); transport.Bind("class-A");
                transport.Execute(Command(service), () => {}); await Until(() => service.Snapshot().State == "Active");
                transport.Execute(Command(service, "STOP"), () => {}); await Until(() => transport.Reports().Count == 1);
                first = transport.Reports()[0]!.AsObject(); // Server acceptance with a lost response leaves the durable outbox unchanged.
            }
            await using var restarted = new NoiseService(_ => new Capture(), () => []);
            Select(restarted); var recovered = new NoiseTransport(restarted, directory); recovered.Bind("class-A");
            Assert.True(JsonNode.DeepEquals(first, Assert.Single(recovered.Reports())));
            recovered.Execute(Command(restarted), () => {}); await Until(() => restarted.Snapshot().State == "Active");
            recovered.Execute(Command(restarted, "STOP"), () => {}); await Until(() => recovered.Reports().Count == 2);
            var acknowledgement = new JsonObject { ["acceptedReports"] = new JsonArray(first["sessionId"]!.DeepClone()), ["acceptedReceipts"] = new JsonArray() };
            recovered.Acknowledge(acknowledgement); recovered.Acknowledge(acknowledgement);
            Assert.NotEqual(first["sessionId"]!.GetValue<string>(), Assert.Single(recovered.Reports())!["sessionId"]!.GetValue<string>());
            Assert.Single(new NoiseTransport(restarted, directory).Reports());
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task Crash_after_command_intent_persistence_keeps_unknown_receipt_without_replaying_start()
    {
        string directory = DirectoryName();
        try
        {
            await using var service = new NoiseService(_ => throw new InvalidOperationException("Capture must not start"), () => []);
            Select(service); var transport = new NoiseTransport(service, directory); transport.Bind("class-A");
            var command = Command(service); int checks = 0;
            Assert.Throws<NpepException>(() => transport.Execute(command, () => { if (++checks == 2) throw new NpepException("CONTROL_OFFLINE"); }));
            await using var restarted = new NoiseService(_ => throw new InvalidOperationException("Capture must not restart"), () => []);
            var recovered = new NoiseTransport(restarted, directory); recovered.Bind("class-A");
            recovered.Execute(command, () => throw new InvalidOperationException("Consumed intent must not execute"));
            Assert.Equal("UNKNOWN", Assert.Single(recovered.Receipts())!["outcome"]!.GetValue<string>());
            Assert.Equal("Idle", restarted.Snapshot().State);
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }
}

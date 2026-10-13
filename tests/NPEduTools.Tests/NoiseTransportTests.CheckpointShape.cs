using System.Text.Json.Nodes;
using NPEduTools.Contracts;
using NPEduTools.Host;
using NPEduTools.Integrations.Npep;

namespace NPEduTools.Tests;

public sealed partial class NoiseTransportTests
{
    [Theory]
    [InlineData("[]")]
    [InlineData("[{}]")]
    [InlineData("42")]
    [InlineData("true")]
    [InlineData("\"broken-checkpoint\"")]
    public async Task InvalidCheckpointShapeCannotBeReportedHealthyOrOverwritePendingReports(string checkpoint)
    {
        string directory = DirectoryName(); Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "noise-outbox.json");
        var capture = new Capture();
        var noise = new NoiseService(_ => capture, () => []);
        try
        {
            Select(noise);
            var oldState = new NoiseState(Guid.NewGuid(), 1, "Stopped", "synthetic", "mic", "fake", DateTimeOffset.UtcNow.AddSeconds(-60), null,
                "Good", new(60, 30, .5, -30, -20, 0, 300), [], SessionId: Guid.NewGuid());
            var pending = NoiseTransport.Report(oldState, "Stopped");
            NpepNoiseProtocol.Validate("report", pending);
            var store = new JsonObject { ["scope"] = "class-A", ["reports"] = new JsonArray(pending),
                ["commands"] = new JsonArray(), ["checkpoint"] = JsonNode.Parse(checkpoint) };
            string original = store.ToJsonString(); File.WriteAllText(path, original);
            var transport = new NoiseTransport(noise, directory);
            var observed = transport.Observe();
            int authorizations = 0;
            transport.Execute(Command(noise), () => authorizations++);
            transport.Bind("class-B");
            await Until(() => !noise.CaptureBusy);
            Assert.True(observed["uploadError"]?.GetValue<string>() == "NOISE_STORE_UNAVAILABLE" &&
                authorizations == 0 && original == File.ReadAllText(path),
                $"{checkpoint}: error={observed["uploadError"]?.GetValue<string>() ?? "none"}, authorization calls={authorizations}, original preserved={original == File.ReadAllText(path)}");
            Assert.False(transport.Available); Assert.False(observed["configured"]!.GetValue<bool>());
            Assert.Equal(0, authorizations); Assert.Equal(0, capture.Starts);
            Assert.Empty(transport.Reports()); Assert.Empty(transport.Receipts());
            Assert.Equal(original, File.ReadAllText(path));

            // A broken upload store does not make a healthy local capture unusable.
            var state = noise.Snapshot();
            Assert.Equal("Accepted", noise.Handle(new(1, Guid.NewGuid(), "noise.command",
                Noise: new("start", state.InstanceId, state.Revision, "mic"))).Outcome);
            await Until(() => noise.Snapshot().State == "Active");
            state = noise.Snapshot();
            Assert.Equal("Accepted", noise.Handle(new(1, Guid.NewGuid(), "noise.command",
                Noise: new("stop", state.InstanceId, state.Revision))).Outcome);
            await Until(() => !noise.CaptureBusy);
            Assert.True(capture.Disposed); Assert.Equal(original, File.ReadAllText(path));
        }
        finally { await noise.DisposeAsync(); Directory.Delete(directory, true); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MissingOrNullCheckpointKeepsLegacyPendingReportReadable(bool missing)
    {
        string directory = DirectoryName(); Directory.CreateDirectory(directory);
        var noise = new NoiseService(_ => throw new InvalidOperationException("No capture"), () => []);
        try
        {
            var state = new NoiseState(Guid.NewGuid(), 1, "Stopped", "synthetic", "mic", "fake", DateTimeOffset.UtcNow.AddSeconds(-60), null,
                "Good", new(60, 30, .5, -30, -20, 0, 300), [], SessionId: Guid.NewGuid());
            var pending = NoiseTransport.Report(state, "Stopped");
            var store = new JsonObject { ["scope"] = "class-A", ["reports"] = new JsonArray(pending.DeepClone()), ["commands"] = new JsonArray() };
            if (!missing) store["checkpoint"] = null;
            string path = Path.Combine(directory, "noise-outbox.json"), original = store.ToJsonString(); File.WriteAllText(path, original);
            var transport = new NoiseTransport(noise, directory);
            Assert.True(transport.Available); Assert.Null(transport.Observe()["uploadError"]);
            Assert.True(JsonNode.DeepEquals(pending, Assert.Single(transport.Reports())));
            Assert.Equal(original, File.ReadAllText(path));
        }
        finally { await noise.DisposeAsync(); Directory.Delete(directory, true); }
    }
}

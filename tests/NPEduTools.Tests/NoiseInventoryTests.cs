using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using NPEduTools.Contracts;
using NPEduTools.Core;
using NPEduTools.Host;

namespace NPEduTools.Tests;

public sealed class NoiseInventoryTests
{
    private static NoiseDevice[] LargeCatalog(bool escaped) => Enumerable.Range(0, 128)
        .Select(i => new NoiseDevice(new string('x', escaped ? 80 : 2000) + i,
            new string(escaped ? '麦' : 'm', 256))).ToArray();

    [Theory]
    [InlineData(false)] [InlineData(true)]
    [SupportedOSPlatform("windows")]
    public async Task Large_microphone_catalog_remains_readable_over_pipe_without_capture(bool escaped)
    {
        int captures = 0;
        var catalog = LargeCatalog(escaped);
        await using var noise = new NoiseService(_ => { captures++; throw new InvalidOperationException("No real capture"); }, () => catalog);
        string name = "NPEduTools.Test.Microphone.Inventory." + Guid.NewGuid().ToString("N");
        using var lifetime = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var server = new PipeServer(name, new ForbiddenReader(), _ => { }, noise: noise);
        var running = server.RunAsync(lifetime.Token);
        try
        {
            var response = await HostClient.RequestAsync(name, "noise.devices", lifetime.Token);
            Assert.Equal("Succeeded", response.Outcome);
            Assert.NotEmpty(response.NoiseDevices!);
            Assert.True(response.NoiseDevices!.Count < catalog.Length);
            Assert.Equal(catalog.Take(response.NoiseDevices.Count), response.NoiseDevices);
            Assert.Null(response.Noise!.SelectedDeviceId);
            Assert.Equal(0, captures);
            Assert.Equal("Succeeded", (await HostClient.RequestAsync(name, "host.ping", lifetime.Token)).Outcome);
        }
        finally { await lifetime.CancelAsync(); try { await running; } catch (OperationCanceledException) { } }
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task Combined_microphone_camera_inventory_fits_school_management_receipt(bool escaped)
    {
        string directory = Directory.CreateTempSubdirectory("NPEduTools-inventory-budget-").FullName;
        int captures = 0;
        try
        {
            var microphoneCatalog = LargeCatalog(escaped);
            var cameraCatalog = Enumerable.Range(0, 128)
                .Select(i => new StudyCameraDevice(new string('摄', 2000) + i, new string('像', 256))).ToArray();
            string micPath = Path.Combine(directory, "noise-microphone.json"), camPath = Path.Combine(directory, "study-camera.json");
            string micSaved = JsonSerializer.Serialize(microphoneCatalog[^1].Id), camSaved = JsonSerializer.Serialize(cameraCatalog[^1].Id);
            File.WriteAllText(micPath, micSaved); File.WriteAllText(camPath, camSaved);
            await using var noise = new NoiseService(_ => { captures++; throw new InvalidOperationException("No real capture"); },
                () => microphoneCatalog, directory: directory);
            var cameras = new StudyDeviceService(() => cameraCatalog, directory);
            var management = new SchoolManagementService(noise, cameras, directory);
            var body = management.ObserveSchool();
            body["requestId"] = Guid.NewGuid().ToString("D");
            body["context"] = new JsonObject { ["sessionId"] = Guid.NewGuid().ToString("D"),
                ["identity"] = new JsonObject { ["deviceId"] = Guid.NewGuid().ToString("D") } };
            body["sequence"] = 1;
            // Match NpepApi.SchoolManagementAsync's exact UTF-8 body size check.
            // No HTTP request or school credentials are used by this fixture.
            Assert.InRange(Encoding.UTF8.GetByteCount(body.ToJsonString()), 1, 65536);
            Assert.NotEmpty(body["microphones"]!.AsArray()); Assert.NotEmpty(body["cameras"]!.AsArray());
            Assert.Equal(microphoneCatalog[^1].Id, body["selectedMicrophoneId"]!.GetValue<string>());
            Assert.Equal(cameraCatalog[^1].Id, body["selectedCameraId"]!.GetValue<string>());
            Assert.Equal(0, noise.Snapshot().Revision);
            Assert.Equal(0, captures);
            Assert.Equal(micSaved, File.ReadAllText(micPath)); Assert.Equal(camSaved, File.ReadAllText(camPath));
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task Ordinary_microphone_catalog_preserves_ids_names_and_order()
    {
        NoiseDevice[] catalog = [new("first", "教室麦克风"), new("second", "External microphone")];
        await using var noise = new NoiseService(_ => throw new InvalidOperationException("No real capture"), () => catalog);
        var reply = noise.Handle(new(1, Guid.NewGuid(), "noise.devices"));
        Assert.Equal("Succeeded", reply.Outcome); Assert.Equal(catalog, reply.NoiseDevices);
    }

    private sealed class ForbiddenReader : ILessonStatusReader
    {
        public Task<StatusResult> ReadAsync(StatusQuery query, CancellationToken cancellationToken)
            => throw new InvalidOperationException("Device enumeration cannot request classroom data");
    }
}

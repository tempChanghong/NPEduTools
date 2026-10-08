using System.Runtime.Versioning;
using NPEduTools.Contracts;
using NPEduTools.Core;
using NPEduTools.Host;

namespace NPEduTools.Tests;

[SupportedOSPlatform("windows")]
public sealed class NoisePipeTests
{
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task Normal_exit_requires_guard_marker_before_accepting_shutdown(bool stored)
    {
        string name = "NPEduTools.Test.Guard.Stop." + Guid.NewGuid().ToString("N");
        using var lifetime = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        int prepared = 0, stopped = 0;
        var server = new PipeServer(name, new ForbiddenReader(), _ => { }, stop: () => { stopped++; lifetime.Cancel(); },
            prepareStop: () => { prepared++; return stored; });
        var running = server.RunAsync(lifetime.Token);
        try
        {
            Assert.Equal("Succeeded", (await HostClient.RequestAsync(name, "host.ping", lifetime.Token)).Outcome);
            Assert.Equal(0, prepared);
            var result = await HostClient.RequestAsync(name, "host.stop", lifetime.Token);
            Assert.Equal(stored ? "Succeeded" : "Rejected", result.Outcome);
            if (!stored) { Assert.Equal("GUARD_STOP_STORE_UNAVAILABLE", result.ErrorCode); Assert.Equal(0, stopped); }
            Assert.Equal(1, prepared);
        }
        finally { await lifetime.CancelAsync(); try { await running; } catch (OperationCanceledException) { } }
    }
    [Fact]
    public async Task Direct_pipe_cannot_bypass_schedule_protection_and_authorized_stop_is_verified_by_host()
    {
        string directory = Path.Combine(Path.GetTempPath(), "noise-pipe-protection-" + Guid.NewGuid());
        var store = new NoiseManagementStore(directory); Assert.Null(store.Configure(null, "local-teacher-2026", false));
        await using var noise = new NoiseService(_ => new Capture(), () => [], management: store);
        var initial = noise.Snapshot(); noise.Handle(new(1, Guid.NewGuid(), "noise.command", Noise: new("select", initial.InstanceId, initial.Revision, "mic")));
        noise.StartScheduled(Guid.NewGuid());
        string name = "NPEduTools.Test.Noise.Protected." + Guid.NewGuid().ToString("N");
        using var lifetime = new CancellationTokenSource(TimeSpan.FromSeconds(12)); int exits = 0, prepared = 0;
        var server = new PipeServer(name, new ForbiddenReader(), _ => { }, stop: () => exits++, noise: noise, prepareStop: () => { prepared++; return true; });
        var running = server.RunAsync(lifetime.Token);
        try
        {
            var status = await HostClient.RequestAsync(name, "noise.management.status", lifetime.Token);
            Assert.True(status.NoiseProtection!.Protected);
            Assert.Equal("MANAGEMENT_REQUIRED", (await HostClient.RequestAsync(name, "host.stop", lifetime.Token)).ErrorCode); Assert.Equal(0, exits);
            Assert.Equal(0, prepared); // Rejected maintenance must not disarm the guard.
            var s = (await HostClient.RequestAsync(name, "noise.status", lifetime.Token)).Noise!;
            var stop = new HostRequest(1, Guid.NewGuid(), "noise.command", Noise: new("stop", s.InstanceId, s.Revision));
            Assert.Equal("MANAGEMENT_REQUIRED", (await HostClient.RequestAsync(name, stop, lifetime.Token)).ErrorCode);
            var auth = new HostRequest(1, Guid.NewGuid(), "noise.management.command", NoiseManagement:
                new("authorize", "local-teacher-2026", Purpose: "stop", TargetRequestId: stop.RequestId, InstanceId: s.InstanceId, SessionId: s.SessionId));
            var grant = await HostClient.RequestAsync(name, auth, lifetime.Token);
            Assert.Equal("Succeeded", grant.Outcome);
            var stopped = await HostClient.RequestAsync(name, stop with { NoiseAuthorization = grant.NoiseProtection!.Ticket }, lifetime.Token);
            Assert.Equal("Accepted", stopped.Outcome); Assert.Equal(0, exits);
        }
        finally
        {
            await lifetime.CancelAsync(); try { await running; } catch (OperationCanceledException) { }
            Directory.Delete(directory, true);
        }
    }
    [Fact]
    public async Task Real_local_pipe_supports_read_start_stop_without_recording_or_lesson_probe()
    {
        int created = 0;
        var capture = new Capture();
        await using var noise = new NoiseService(_ => { created++; return capture; }, () => [new("mic", "Fixture")]);
        string name = "NPEduTools.Test.Noise." + Guid.NewGuid().ToString("N");
        using var lifetime = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        var server = new PipeServer(name, new ForbiddenReader(), _ => { }, noise: noise);
        var running = server.RunAsync(lifetime.Token);
        try
        {
            var state = (await HostClient.RequestAsync(name, "noise.devices", lifetime.Token)).Noise!;
            Assert.Equal(0, created);
            var start = new HostRequest(Protocol.Version, Guid.NewGuid(), "noise.command",
                Noise: new("start", state.InstanceId, state.Revision, "mic"));
            Assert.Equal("Accepted", (await HostClient.RequestAsync(name, start, lifetime.Token)).Outcome);
            Assert.Equal("NoiseStateChanged", (await HostClient.RequestAsync(name, start, lifetime.Token)).ErrorCode);
            do
            {
                await Task.Delay(10, lifetime.Token);
                state = (await HostClient.RequestAsync(name, "noise.status", lifetime.Token)).Noise!;
            } while (state.State != "Active");
            Assert.NotNull(state.Summary); Assert.NotNull(state.CurrentDbfs);
            var stop = new HostRequest(Protocol.Version, Guid.NewGuid(), "noise.command",
                Noise: new("stop", state.InstanceId, state.Revision));
            Assert.Equal("Accepted", (await HostClient.RequestAsync(name, stop, lifetime.Token)).Outcome);
            do
            {
                await Task.Delay(10, lifetime.Token);
                state = (await HostClient.RequestAsync(name, "noise.status", lifetime.Token)).Noise!;
            } while (state.State != "Stopped");
            Assert.True(capture.Disposed); Assert.Null(state.CurrentDbfs);
            Assert.Equal(1, state.Summary!.Frames);
        }
        finally
        {
            await lifetime.CancelAsync();
            try { await running; } catch (OperationCanceledException) { }
        }
    }
    [Fact]
    public async Task Oversized_saved_microphone_does_not_break_status_or_device_selection_over_pipe()
    {
        string directory = Directory.CreateTempSubdirectory("NPEduTools-noise-selection-pipe-").FullName;
        string path = Path.Combine(directory, "noise-microphone.json");
        string content = System.Text.Json.JsonSerializer.Serialize(new string('x', 70000));
        File.WriteAllText(path, content);
        int created = 0;
        await using var noise = new NoiseService(_ => { created++; return new Capture(); },
            () => [new("mic", "Fixture")], directory: directory);
        string name = "NPEduTools.Test.Noise.Selection." + Guid.NewGuid().ToString("N");
        using var lifetime = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var server = new PipeServer(name, new ForbiddenReader(), _ => { }, noise: noise);
        var running = server.RunAsync(lifetime.Token);
        try
        {
            var devices = await HostClient.RequestAsync(name, "noise.devices", lifetime.Token);
            Assert.Equal("Succeeded", devices.Outcome);
            Assert.Equal("mic", Assert.Single(devices.NoiseDevices!).Id);
            Assert.Null(devices.Noise!.SelectedDeviceId);
            Assert.Equal(content, File.ReadAllText(path));
            var selected = await HostClient.RequestAsync(name, new HostRequest(Protocol.Version,
                Guid.NewGuid(), "noise.command", Noise: new("select", devices.Noise.InstanceId, devices.Noise.Revision, "mic")), lifetime.Token);
            Assert.Equal("Succeeded", selected.Outcome);
            Assert.Equal("mic", (await HostClient.RequestAsync(name, "noise.status", lifetime.Token)).Noise!.SelectedDeviceId);
            Assert.Equal(0, created);
        }
        finally
        {
            await lifetime.CancelAsync();
            try { await running; } catch (OperationCanceledException) { }
            Directory.Delete(directory, true);
        }
    }

    private sealed class ForbiddenReader : ILessonStatusReader
    {
        public Task<StatusResult> ReadAsync(StatusQuery query, CancellationToken cancellationToken)
            => throw new InvalidOperationException("Noise monitoring cannot request ClassIsland or recording.");
    }
    private sealed class Capture : INoiseCapture
    {
        public string DeviceName => "Fixture";
        public bool Disposed;
        public event Action<NoiseFrame>? Frame;
        public event Action<string>? Failed { add { } remove { } }
        public void Start() => Frame?.Invoke(new(0.1, 0.04, 0.2, 0, false));
        public void Dispose() => Disposed = true;
    }
}

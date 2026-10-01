using System.Runtime.Versioning;
using NPEduTools.Contracts;
using NPEduTools.Core;
using NPEduTools.Host;

namespace NPEduTools.Tests;

[SupportedOSPlatform("windows")]
public sealed class NoisePipeTests
{
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

using NPEduTools.Contracts;
using NPEduTools.Core;
using NPEduTools.Host;
using NPEduTools.Integrations.Npep;

namespace NPEduTools.Tests;

public sealed class NpepDiagnosticPipeTests
{
    private sealed class ForbiddenReader : ILessonStatusReader
    { public Task<StatusResult> ReadAsync(StatusQuery query, CancellationToken token) => throw new InvalidOperationException("Diagnostic reads cannot query ClassIsland or sensors."); }
    [Fact, System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public async Task Diagnostic_history_round_trips_only_on_its_read_only_capability()
    {
        string directory = Path.Combine(Path.GetTempPath(), "NPEduTools.DiagnosticPipe." + Guid.NewGuid().ToString("N"));
        await using var runtime = new NpepRuntime(directory, "fixture", () => throw new InvalidOperationException("Unpaired device cannot report."));
        string pipe = "NPEduTools.Test.ConnectionHistory." + Guid.NewGuid().ToString("N");
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        var running = new PipeServer(pipe, new ForbiddenReader(), _ => { }, npep: runtime).RunAsync(stop.Token);
        try {
            var history = await HostClient.RequestAsync(pipe, "npep.diagnostics", stop.Token);
            Assert.Equal("Succeeded", history.Outcome); Assert.NotNull(history.ConnectionHistory); Assert.Empty(history.ConnectionHistory.Events);
            Assert.Null(history.Npep);
            var status = await HostClient.RequestAsync(pipe, "npep.status", stop.Token);
            Assert.Equal("UNPAIRED", status.Npep!.State); Assert.Null(status.ConnectionHistory);
            var refused = await HostClient.RequestAsync(pipe, new HostRequest(1, Guid.NewGuid(), "npep.diagnostics", Npep: new("resume", 0)), stop.Token);
            Assert.Equal("Rejected", refused.Outcome); Assert.Null(refused.ConnectionHistory); Assert.Equal("UNPAIRED", runtime.Snapshot().State);
        } finally { await stop.CancelAsync(); try { await running; } catch (OperationCanceledException) { } }
    }
}

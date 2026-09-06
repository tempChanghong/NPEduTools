using System.Diagnostics;
using System.Text.Json;
using NPEduTools.Contracts;
using NPEduTools.Core;

namespace NPEduTools.Host;

/// <summary>One shared, supervised read-only connection, independent of App connections.</summary>
public sealed class StatusMonitor(Func<ProcessStartInfo> startInfo, Action<string> log) : IAsyncDisposable
{
    private readonly CancellationTokenSource _lifetime = new();
    private readonly object _sync = new();
    private readonly Guid _streamId = Guid.NewGuid();
    private Task? _run;
    private long _sequence;
    private WatchSnapshot _current = new(Protocol.Version, Guid.Empty, Guid.Empty, 0, Guid.Empty,
        DateTimeOffset.UtcNow, "Connecting", null, "正在连接 ClassIsland…", null);

    public WatchSnapshot Snapshot(Guid requestId)
    {
        lock (_sync)
        {
            _run ??= Task.Run(() => RunAsync(_lifetime.Token));
            return _current with { RequestId = requestId, StreamId = _streamId };
        }
    }

    private void Publish(StatusResult result, Guid connection)
    {
        var s = result.Status;
        lock (_sync)
            _current = new(Protocol.Version, Guid.Empty, _streamId, ++_sequence, connection,
                DateTimeOffset.UtcNow, result.Outcome, result.ErrorCode, result.Message,
                s is null ? null : new(s.SampleStartedAt, s.SampleCompletedAt, s.State, s.Subject,
                    s.IsTimerRunning, s.IsClassPlanLoaded, s.IsClassPlanEnabled, s.CurrentSelectedIndex, s.ObservedEvents));
    }

    private async Task RunAsync(CancellationToken token)
    {
        int delaySeconds = 1;
        while (!token.IsCancellationRequested)
        {
            Guid connection = Guid.NewGuid();
            using var worker = new Process { StartInfo = startInfo() };
            using var connectionLifetime = CancellationTokenSource.CreateLinkedTokenSource(token);
            Task? lease = null, drain = null;
            bool started = false;
            try
            {
                started = worker.Start();
                if (!started) throw new InvalidOperationException("Worker did not start.");
                lease = SendLeaseAsync(worker.StandardInput.BaseStream, connectionLifetime.Token);
                drain = DrainAsync(worker.StandardError.BaseStream, connectionLifetime.Token);
                while (!token.IsCancellationRequested)
                {
                    using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
                    deadline.CancelAfter(TimeSpan.FromSeconds(8));
                    var result = await Protocol.ReadAsync<StatusResult>(worker.StandardOutput.BaseStream, deadline.Token);
                    Publish(result, connection);
                    if (result.Outcome != "Succeeded") break;
                    delaySeconds = 1;
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { break; }
            catch (OperationCanceledException)
            {
                Publish(new("TimedOut", "ClassIslandMonitorDeadlineExceeded",
                    "ClassIsland 未及时响应，正在自动重连。"), connection);
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or JsonException or InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                Publish(new("Unavailable", "ClassIslandMonitorDisconnected",
                    "ClassIsland 连接不可用，正在自动重连。"), connection);
            }
            finally
            {
                connectionLifetime.Cancel();
                if (started)
                {
                    try
                    {
                        if (!worker.HasExited) worker.Kill(entireProcessTree: true);
                        await worker.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(2));
                    }
                    catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or TimeoutException)
                    {
                        // Never start another worker if ownership cleanup could not be confirmed.
                        Publish(new("Failed", "WorkerCleanupFailed", "监听进程未能清理，请重启后台。"), connection);
                        _lifetime.Cancel();
                    }
                }
                if (lease is not null) await lease;
                if (drain is not null) await drain;
            }
            log(JsonSerializer.Serialize(new { timestamp = DateTimeOffset.UtcNow,
                component = "ClassIslandMonitor", retrySeconds = delaySeconds }, Protocol.Json));
            try { await Task.Delay(TimeSpan.FromSeconds(delaySeconds), token); }
            catch (OperationCanceledException) { break; }
            delaySeconds = Math.Min(delaySeconds * 2, 15);
        }
    }

    private static async Task SendLeaseAsync(Stream input, CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested)
            {
                await input.WriteAsync(new byte[] { 1 }, token);
                await input.FlushAsync(token);
                await Task.Delay(TimeSpan.FromSeconds(2), token);
            }
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException) { }
    }

    private static async Task DrainAsync(Stream stream, CancellationToken token)
    {
        try { var buffer = new byte[4096]; while (await stream.ReadAsync(buffer, token) != 0) { } }
        catch (Exception ex) when (ex is IOException or OperationCanceledException) { }
    }

    public async ValueTask DisposeAsync()
    {
        _lifetime.Cancel();
        if (_run is not null) await _run;
        _lifetime.Dispose();
    }

    public async Task StopAsync()
    {
        _lifetime.Cancel();
        if (_run is not null) await _run;
        Publish(new("Stopped", null, "后台已停止。重新打开窗口可再次启动。"), Guid.Empty);
    }
}

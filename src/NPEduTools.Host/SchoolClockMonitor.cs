using System.Diagnostics;
using System.Text.Json;
using NPEduTools.Contracts;
using NPEduTools.Core;

namespace NPEduTools.Host;

/// <summary>One shared, supervised read-only connection, independent of App connections.</summary>
public sealed class SchoolClockMonitor(Func<ProcessStartInfo> startInfo, Action<string> log) : IAsyncDisposable
{
    private readonly CancellationTokenSource _lifetime = new();
    private readonly object _sync = new();
    private Task? _run;
    private long _publishedAt = Stopwatch.GetTimestamp();
    private SchoolClockFrame _current = SchoolClockFrame.Unavailable("正在连接 ClassIsland 桥接插件…");

    public SchoolClockFrame Snapshot()
    {
        lock (_sync)
        {
            _run ??= Task.Run(() => RunAsync(_lifetime.Token));
            double age = _current.AgeMs + Stopwatch.GetElapsedTime(_publishedAt).TotalMilliseconds;
            return _current with { AgeMs = age,
                State = _current.SchoolNow is not null && age >= SchoolClockFrame.MaxAgeMs ? "Stale" : _current.State };
        }
    }

    private void Publish(SchoolClockFrame result, Guid connection)
    {
        lock (_sync)
        {
            _publishedAt = Stopwatch.GetTimestamp();
            _current = result with { ConnectionId = connection };
        }
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
                    var result = await Protocol.ReadAsync<SchoolClockFrame>(worker.StandardOutput.BaseStream, deadline.Token);
                    Publish(result, connection);
                    if (result.State == "Unavailable") break;
                    delaySeconds = 1;
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { break; }
            catch (OperationCanceledException)
            {
                Publish(SchoolClockFrame.Unavailable("学校时间读取超时，正在重连。"), connection);
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or JsonException or InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                Publish(SchoolClockFrame.Unavailable("学校时间连接中断，正在重连。"), connection);
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
                        Publish(SchoolClockFrame.Unavailable("监听进程未能清理，请重启后台。"), connection);
                        _lifetime.Cancel();
                    }
                }
                if (lease is not null) await lease;
                if (drain is not null) await drain;
            }
            log(JsonSerializer.Serialize(new { timestamp = DateTimeOffset.UtcNow,
                component = "SchoolClockMonitor", retrySeconds = delaySeconds }, Protocol.Json));
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
        Publish(SchoolClockFrame.Unavailable("后台已停止。"), Guid.Empty);
    }
}

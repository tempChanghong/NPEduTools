using System.Diagnostics;
using NPEduTools.Contracts;
using NPEduTools.Core;

namespace NPEduTools.Host;

/// <summary>A single read-only worker at a time; deadline includes worker startup and observation.</summary>
public sealed class IsolatedStatusReader(Func<StatusQuery, ProcessStartInfo> startInfo) : ILessonStatusReader, IDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _cleanupFailed;

    public async Task<StatusResult> ReadAsync(StatusQuery query, CancellationToken cancellationToken)
    {
        if (!await _gate.WaitAsync(0, cancellationToken))
            return new("Rejected", "ResourceBusy", "另一个 ClassIsland 查询正在执行，请稍后重试。");
        try
        {
            if (_cleanupFailed)
                return new("Failed", "WorkerCleanupFailed", "探测进程未能清理；请检查进程状态后重启 Host。");
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(query.Timeout);
            using var worker = new Process { StartInfo = startInfo(query) };
            Task? drain = null;
            StatusResult result;
            try
            {
                if (!worker.Start()) throw new InvalidOperationException("Worker did not start.");
                drain = DrainAsync(worker.StandardError.BaseStream, deadline.Token);
                result = await Protocol.ReadAsync<StatusResult>(worker.StandardOutput.BaseStream, deadline.Token);
                await worker.WaitForExitAsync(deadline.Token);
                if (worker.ExitCode != 0) result = new("Failed", "WorkerExited", "ClassIsland 探测进程异常退出。");
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                result = new("Cancelled", "QueryCancelled", "查询已取消。");
            }
            catch (OperationCanceledException)
            {
                result = new("TimedOut", "ClassIslandDeadlineExceeded",
                    "ClassIsland 未在期限内完成查询；可能未运行、接口不可用或响应过慢。");
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or System.Text.Json.JsonException or InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                result = new("Failed", "WorkerFailed", "无法启动或读取 ClassIsland 探测进程。");
            }
            finally
            {
                // Only terminate the worker that we just created, never the user's ClassIsland process.
                try
                {
                    if (worker.Id > 0 && !worker.HasExited)
                    {
                        worker.Kill(entireProcessTree: true);
                        using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                        await worker.WaitForExitAsync(cleanup.Token);
                    }
                }
                catch (InvalidOperationException) { /* Process never started or has already exited. */ }
                catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or OperationCanceledException)
                {
                    _cleanupFailed = true;
                }
                deadline.Cancel();
                if (drain is not null) await drain;
            }
            return _cleanupFailed
                ? new("Failed", "WorkerCleanupFailed", "探测进程未能清理；后续查询已停用，请检查进程状态。")
                : result;
        }
        finally { _gate.Release(); }
    }

    private static async Task DrainAsync(Stream stream, CancellationToken token)
    {
        var buffer = new byte[4096];
        try { while (await stream.ReadAsync(buffer, token) != 0) { } }
        catch (Exception ex) when (ex is IOException or OperationCanceledException) { }
    }

    public void Dispose() => _gate.Dispose();
}

using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;
using NPEduTools.Contracts;
using NPEduTools.Core;

namespace NPEduTools.Host;

[SupportedOSPlatform("windows")]
public sealed class PipeServer(string pipeName, ILessonStatusReader reader, Action<string> log)
{
    public async Task RunAsync(CancellationToken token)
    {
        // Four fixed accept loops bound active connections, parsing buffers and stalled clients.
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(token);
        async Task RunLoopAsync()
        {
            try { await AcceptLoopAsync(lifetime.Token); }
            catch { lifetime.Cancel(); throw; }
        }
        await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => RunLoopAsync()));
    }

    private async Task AcceptLoopAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            await using var pipe = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 4,
                PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            try
            {
                await pipe.WaitForConnectionAsync(token);
                if (!IsCurrentSession(pipe)) continue;
                using var readDeadline = CancellationTokenSource.CreateLinkedTokenSource(token);
                readDeadline.CancelAfter(TimeSpan.FromSeconds(2));
                var request = await Protocol.ReadAsync<HostRequest>(pipe, readDeadline.Token);
                var watch = Stopwatch.StartNew();
                HostResponse response;
                string? error = Protocol.Validate(request);
                if (error is not null)
                    response = new(Protocol.Version, request.RequestId, "Rejected", error, "请求无效或协议不兼容。");
                else if (request.Capability == "host.ping")
                    response = new(Protocol.Version, request.RequestId, "Succeeded", null, "Host 已就绪。");
                else
                {
                    // Client disconnection does not cancel an accepted read-only operation.
                    var result = await reader.ReadAsync(new(TimeSpan.FromMilliseconds(request.TimeoutMs),
                        TimeSpan.FromMilliseconds(request.ObserveMs)), token);
                    var status = result.Status;
                    response = new(Protocol.Version, request.RequestId, result.Outcome, result.ErrorCode, result.Message,
                        status is null ? null : new(status.SampleStartedAt, status.SampleCompletedAt, status.State,
                            status.Subject, status.IsTimerRunning, status.IsClassPlanLoaded, status.IsClassPlanEnabled,
                            status.CurrentSelectedIndex, status.ObservedEvents));
                }
                // Structured diagnostics contain identifiers and outcomes, never lesson contents.
                log(JsonSerializer.Serialize(new
                {
                    timestamp = DateTimeOffset.UtcNow, request.RequestId, response.Outcome, response.ErrorCode,
                    elapsedMs = watch.ElapsedMilliseconds
                }, Protocol.Json));
                using var writeDeadline = CancellationTokenSource.CreateLinkedTokenSource(token);
                writeDeadline.CancelAfter(TimeSpan.FromSeconds(2));
                await Protocol.WriteAsync(pipe, response, writeDeadline.Token);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) when (ex is IOException or InvalidDataException or JsonException or UnauthorizedAccessException)
            {
                log(JsonSerializer.Serialize(new { timestamp = DateTimeOffset.UtcNow, error = "ClientTransportError", type = ex.GetType().Name }));
            }
        }
    }

    private static bool IsCurrentSession(NamedPipeServerStream pipe)
    {
        if (!GetNamedPipeClientProcessId(pipe.SafePipeHandle, out uint pid)) return false;
        try
        {
            using var client = Process.GetProcessById(checked((int)pid));
            using var host = Process.GetCurrentProcess();
            return client.SessionId == host.SessionId;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        { return false; }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeClientProcessId(SafePipeHandle pipe, out uint clientProcessId);
}

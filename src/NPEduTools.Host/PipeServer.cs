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
public sealed class PipeServer(string pipeName, ILessonStatusReader reader, Action<string> log,
    StatusMonitor? monitor = null, Action? stop = null, LaunchService? launch = null, TouchAssistService? touch = null,
    SchoolClockMonitor? schoolClock = null, RecordingService? recording = null, ExamAwareService? examAware = null, ClassroomModeService? classroom = null)
{
    private readonly SemaphoreSlim _subscriptions = new(2, 2);
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
                if (error is null && request.Capability == "classisland.watch")
                {
                    await WatchAsync(pipe, request, token);
                    continue;
                }
                if (error is not null)
                    response = new(Protocol.Version, request.RequestId, "Rejected", error, "请求无效或协议不兼容。");
                else if (request.Capability == "host.ping")
                    response = new(Protocol.Version, request.RequestId, "Succeeded", null, "Host 已就绪。");
                else if (request.Capability == "host.cached-status")
                    response = new(Protocol.Version, request.RequestId, "Succeeded", null, "已有本地缓存",
                        SchoolClock: schoolClock?.PeekSnapshot(), Recording: recording?.State,
                        Automatic: recording?.Automatic, ExamAware: examAware?.Snapshot(), ClassroomMode: classroom?.Snapshot);
                else if (request.Capability == "host.stop" && classroom is not null && !classroom.BeginShutdown())
                    response = new(Protocol.Version, request.RequestId, "Rejected", "ClassroomBusy", "课堂模式正在切换，请等待完成或恢复提示后再停止后台。");
                else if (request.Capability.StartsWith("classroom.", StringComparison.Ordinal))
                    response = classroom?.Handle(request) ?? new(Protocol.Version, request.RequestId, "Rejected", "ClassroomUnavailable", "请更新并重启后台以使用课堂模式。");
                else if (request.Capability == "host.stop")
                {
                    if (stop is not null && recording is not null) await recording.StopAsync();
                    if (stop is not null && touch is not null) await touch.StopAsync();
                    if (stop is not null && launch is not null) await launch.StopAsync();
                    if (stop is not null && monitor is not null) await monitor.StopAsync();
                    if (stop is not null && schoolClock is not null) await schoolClock.StopAsync();
                    response = new(Protocol.Version, request.RequestId, stop is null ? "Rejected" : "Succeeded",
                        stop is null ? "StopUnavailable" : null, "停止后台请求已受理。");
                }
                else if (request.Capability == "classisland.school-clock")
                    response = new(Protocol.Version, request.RequestId, "Succeeded", null, "学校时间状态",
                        SchoolClock: schoolClock?.Snapshot() ?? SchoolClockFrame.Unavailable("此后台不支持学校时间，请更新后台。"));
                else if (request.Capability.StartsWith("examaware.", StringComparison.Ordinal))
                    response = examAware is not null ? await examAware.HandleAsync(request, token)
                        : new(Protocol.Version, request.RequestId, "Rejected", "ExamAwareUnavailable", "此后台不支持 ExamAware，请更新后台。");
                else if (request.Capability.StartsWith("recording.", StringComparison.Ordinal))
                    response = recording is not null ? await recording.HandleAsync(request)
                        : new(Protocol.Version, request.RequestId, "Rejected", "RecordingUnavailable", "此后台不支持录制管理。");
                else if (request.Capability.StartsWith("presentation.touch.", StringComparison.Ordinal))
                    response = touch is not null ? await touch.HandleAsync(request, token)
                        : new(Protocol.Version, request.RequestId, "Rejected", "TouchUnavailable", "此后台未启用触摸辅助。");
                else if (request.Capability is "classisland.config.get" or "classisland.config.set" or "classisland.start" or "classisland.verify" or "classisland.execution.get")
                    response = launch is not null ? await launch.HandleAsync(request, token)
                        : new(Protocol.Version, request.RequestId, "Rejected", "LaunchUnavailable", "此后台未启用启动功能。");
                else
                {
                    // Client disconnection does not cancel an accepted read-only operation.
                    var result = await reader.ReadAsync(new(TimeSpan.FromMilliseconds(request.TimeoutMs),
                        TimeSpan.FromMilliseconds(request.ObserveMs), request.Capability == "classisland.schedule", request.SchoolDate), token);
                    var status = result.Status;
                    response = new(Protocol.Version, request.RequestId, result.Outcome, result.ErrorCode, result.Message,
                        status is null ? null : new(status.SampleStartedAt, status.SampleCompletedAt, status.State,
                            status.Subject, status.IsTimerRunning, status.IsClassPlanLoaded, status.IsClassPlanEnabled,
                            status.CurrentSelectedIndex, status.ObservedEvents), Schedule: result.Schedule, Forecast: result.Forecast);
                }
                // Structured diagnostics contain identifiers and outcomes, never lesson contents.
                log(JsonSerializer.Serialize(new
                {
                    timestamp = DateTimeOffset.UtcNow, request.RequestId, response.Outcome, response.ErrorCode,
                    elapsedMs = watch.ElapsedMilliseconds
                }, Protocol.Json));
                using var writeDeadline = CancellationTokenSource.CreateLinkedTokenSource(token);
                writeDeadline.CancelAfter(TimeSpan.FromSeconds(2));
                if (error is null && request.Capability == "host.stop" && stop is not null && response.Outcome == "Succeeded")
                {
                    try { await Protocol.WriteAsync(pipe, response, writeDeadline.Token); }
                    finally
                    {
                        // An accepted shutdown survives client disconnection. Other windows get one heartbeat.
                        await Task.Delay(TimeSpan.FromMilliseconds(1500), token);
                        stop();
                    }
                }
                else await Protocol.WriteAsync(pipe, response, writeDeadline.Token);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) when (ex is IOException or InvalidDataException or JsonException or UnauthorizedAccessException)
            {
                log(JsonSerializer.Serialize(new { timestamp = DateTimeOffset.UtcNow, error = "ClientTransportError", type = ex.GetType().Name }));
            }
        }
    }

    private async Task WatchAsync(Stream pipe, HostRequest request, CancellationToken token)
    {
        bool accepted = monitor is not null && await _subscriptions.WaitAsync(0, token);
        try
        {
            do
            {
                var snapshot = accepted ? monitor!.Snapshot(request.RequestId) : new WatchSnapshot(
                    Protocol.Version, request.RequestId, Guid.Empty, 0, Guid.Empty, DateTimeOffset.UtcNow,
                    "Rejected", "SubscriptionLimit", "状态订阅已达上限，请关闭多余窗口后重试。", null);
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
                deadline.CancelAfter(TimeSpan.FromSeconds(2));
                await Protocol.WriteAsync(pipe, snapshot, deadline.Token);
                if (!accepted || snapshot.Outcome == "Stopped") return;
                await Task.Delay(TimeSpan.FromSeconds(1), token);
            } while (!token.IsCancellationRequested);
        }
        finally { if (accepted) _subscriptions.Release(); }
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

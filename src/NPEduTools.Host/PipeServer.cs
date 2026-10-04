using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;
using NPEduTools.Contracts;
using NPEduTools.Core;
using NPEduTools.Integrations.Npep;

namespace NPEduTools.Host;

[SupportedOSPlatform("windows")]
public sealed partial class PipeServer(string pipeName, ILessonStatusReader reader, Action<string> log,
    StatusMonitor? monitor = null, Action? stop = null, LaunchService? launch = null, TouchAssistService? touch = null,
    SchoolClockMonitor? schoolClock = null, RecordingService? recording = null, ExamAwareService? examAware = null, ClassroomModeService? classroom = null, NpepRuntime? npep = null,
    RuntimeOperationGate? runtimeGate = null, RemoteExamExecutor? remoteExam = null, NoiseService? noise = null, SecRandomService? secRandom = null, NoiseDisplayService? noiseDisplay = null,
    Func<bool>? prepareStop = null)
{
    private readonly SemaphoreSlim _subscriptions = new(2, 2);
    private readonly TaskCompletionSource _watchStopping = new(TaskCreationOptions.RunContinuationsAsynchronously);
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
                using var shutdownReservation = error is null && request.Capability == "host.stop"
                    ? runtimeGate?.TryEnterMutation() : null;
                if (error is null && request.Capability == "classisland.watch")
                {
                    await WatchAsync(pipe, request, token);
                    continue;
                }
                if (error is not null)
                    response = new(Protocol.Version, request.RequestId, "Rejected", error, "请求无效或协议不兼容。");
                else if (request.Capability == "host.ping")
                    response = new(Protocol.Version, request.RequestId, "Succeeded", null, "Host 已就绪。");
                else if (request.Capability.StartsWith("secrandom.", StringComparison.Ordinal))
                    response = secRandom?.Handle(request) ?? new(Protocol.Version, request.RequestId, "Rejected", "SecRandomUnavailable", "请重启新版后台以使用 SecRandom。");
                else if (request.Capability.StartsWith("noise.management.", StringComparison.Ordinal))
                    response = noise?.ManagementHandle(request) ?? new(Protocol.Version, request.RequestId, "Rejected", "NoiseUnavailable", "请重启新版后台。");
                else if (request.Capability.StartsWith("noise.display.", StringComparison.Ordinal))
                    response = noiseDisplay?.Handle(request) ?? new(Protocol.Version, request.RequestId, "Rejected", "DISPLAY_UNSUPPORTED", "请重启新版后台。");
                else if (request.Capability is not ("noise.command" or "host.stop") && Protocol.NoiseInterruption(request) &&
                    noise?.AuthorizeInterruption(request) is { } noiseDenied)
                    response = noiseDenied;
                else if (request.Capability.StartsWith("noise.", StringComparison.Ordinal))
                    response = (noise?.Handle(request) ?? new(Protocol.Version, request.RequestId, "Rejected", "NoiseUnavailable", "请重启新版后台以使用噪音监测。"))
                        with { NoiseDisplay = noiseDisplay?.Snapshot() };
                else if (request.Capability is "remoteexam.preflight" or "remoteexam.inspect")
                    response = await RemoteExamPreflightAsync(request, token);
                else if (request.Capability is "remoteexam.status" or "remoteexam.command")
                    response = await RemoteExamLocalAsync(request, token);
                else if (request.Capability.StartsWith("npep.", StringComparison.Ordinal))
                    response = npep?.Handle(request) ?? new(Protocol.Version, request.RequestId, "Rejected", "NpepUnavailable", "请更新并重启后台以使用学校互联。");
                else if (request.Capability == "host.cached-status")
                    response = new(Protocol.Version, request.RequestId, "Succeeded", null, "已有本地缓存",
                        SchoolClock: schoolClock?.PeekSnapshot(), Recording: recording?.State,
                        Automatic: recording?.Automatic, ExamAware: examAware?.Snapshot(), ClassroomMode: classroom?.Snapshot);
                else if (request.Capability == "host.stop" && runtimeGate is not null && shutdownReservation is null)
                    response = new(Protocol.Version, request.RequestId, "Rejected", "RuntimeOperationBusy", "正在切换运行环境，后台暂不能停止。");
                else if (request.Capability == "host.stop" && noise?.AuthorizeInterruption(request) is { } exitDenied)
                    response = exitDenied;
                else if (request.Capability == "host.stop" && classroom is not null && !classroom.BeginShutdown())
                {
                    noise?.AbortShutdown();
                    response = new(Protocol.Version, request.RequestId, "Rejected", "ClassroomBusy", "课堂模式正在切换，请等待完成或恢复提示后再停止后台。");
                }
                else if (request.Capability.StartsWith("classroom.", StringComparison.Ordinal))
                    response = classroom?.Handle(request) ?? new(Protocol.Version, request.RequestId, "Rejected", "ClassroomUnavailable", "请更新并重启后台以使用课堂模式。");
                else if (request.Capability == "host.stop")
                {
                    if (prepareStop is not null && !prepareStop())
                    {
                        noise?.AbortShutdown(); classroom?.AbortShutdown();
                        response = new(Protocol.Version, request.RequestId, "Rejected", "GUARD_STOP_STORE_UNAVAILABLE", "未能保存正常退出标记，后台未退出；请检查配置目录。");
                    }
                    else
                    {
                        if (stop is not null && recording is not null) await recording.StopAsync();
                        if (stop is not null && touch is not null) await touch.StopAsync();
                        if (stop is not null && launch is not null) await launch.StopAsync();
                        if (stop is not null && monitor is not null) await monitor.StopAsync();
                        if (stop is not null && schoolClock is not null) await schoolClock.StopAsync();
                        response = new(Protocol.Version, request.RequestId, stop is null ? "Rejected" : "Succeeded",
                            stop is null ? "StopUnavailable" : null, "停止后台请求已受理。");
                    }
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
                        // Accepted shutdown survives client disconnection. Wake subscriptions and
                        // wait for their final frame/disconnection, rather than
                        // assuming a heartbeat will be scheduled within a fixed sleep on a busy machine.
                        await DrainSubscriptionsAsync(token);
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

    private async Task<HostResponse> RemoteExamPreflightAsync(HostRequest request, CancellationToken token)
    {
        if (remoteExam is null) return new(Protocol.Version, request.RequestId, "Rejected", "RemoteExamUnavailable", "此后台不支持考试环境检查，请重启新版后台。");
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
            deadline.CancelAfter(TimeSpan.FromMilliseconds(request.TimeoutMs));
            var current = request.Capability == "remoteexam.inspect"
                ? await remoteExam.InspectLocallyAsync(deadline.Token) : await remoteExam.PreflightAsync(deadline.Token, switchMode: true);
            return new(Protocol.Version, request.RequestId, "Succeeded", null,
                "本机检查通过：后台具有管理员权限，录制器空闲，程序路径有效。" +
                (current.ExamAwareReady ? "ExamAware2 桥接已就绪。" : "ExamAware2 桥接尚未就绪，实际切换时需要准备并确认。") +
                (current.ClassIslandStopped ? "ClassIsland 当前已停止。" : "ClassIsland 当前实例已核实。") +
                "本次仅检查，没有切换软件或修改自启动。实际远程执行还需要本机许可和学校服务授权。", RemoteExam: RemoteExamSnapshot());
        }
        catch (RemoteExamException error)
        { return new(Protocol.Version, request.RequestId, "Rejected", error.Code, error.LocalMessage ?? RemoteExamPreflightMessage(error.Code)); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException or
            System.ComponentModel.Win32Exception or LaunchTargetException or OperationCanceledException)
        { return new(Protocol.Version, request.RequestId, "Rejected", "PREFLIGHT_UNAVAILABLE", "未能完成本机检查，请核对程序位置、桥接和后台状态。没有执行软件切换。"); }
    }

    private static string RemoteExamPreflightMessage(string code) => code switch
    {
        "HOST_NOT_ELEVATED" => "当前实际执行的后台没有管理员权限。请在结束录制后退出整个 NPEduTools，再以管理员身份启动；只提升前台不会替换已运行的普通权限后台。",
        "RECORDING_BUSY" => "录制器正在工作或状态未确认，本次不切换，也不会停止录制。",
        "OPERATION_BUSY" => "当前有通知窗口或软件管理操作，请处理完后重新检查。",
        "CLASSISLAND_TASK_REQUIRED" => "请先在 ClassIsland 设置中创建并核实当前程序的管理员自启动任务。远程操作不会新建或覆盖任务。",
        "STARTUP_NOT_READY" => "自启动设置尚未得到确认，请检查 ExamAware 桥接权限及 ClassIsland 管理员任务；如切换已开始，请在课堂模式中恢复切换前设置。",
        "MODE_CONTROL_UNAVAILABLE" => "后台尚不支持完整考试模式，请更新并重启后台。",
        "DAILY_MODE_REQUIRED" => "当前仍是本地考试模式。请先在课堂模式中返回日常，再核实并解除本次远程录课暂停。",
        "CLASSISLAND_CONFIGURATION_REQUIRED" => "请先保存 ClassIsland 程序位置并处理配置警告。",
        "EXAMAWARE_CONFIGURATION_REQUIRED" => "请先保存 ExamAware2 程序位置并处理桥接服务配置问题。",
        "CLASSISLAND_EXECUTABLE_INVALID" => "ClassIsland 程序文件无效或缺失，请在 ClassIsland 设置中重新保存位置。",
        "EXAMAWARE_EXECUTABLE_INVALID" => "ExamAware2 程序文件无效、缺失或版本不受支持，请在考试看板中重新保存位置。",
        "EXAMAWARE_EXECUTABLE_UNREADABLE" => "无法读取 ExamAware2 程序文件，请检查位置和文件访问权限。",
        "CLASSISLAND_IDENTITY_UNAVAILABLE" => "无法核实 ClassIsland 的路径、用户、会话或唯一实例，请先检查本地运行状态。",
        "EXAMAWARE_IDENTITY_UNAVAILABLE" => "无法核实 ExamAware2 的程序实例或所属用户，请检查其他位置、用户或会话中的进程。",
        "CONFIGURATION_DRIFT" => "检查期间程序配置发生变化，请重新检查。",
        "DESKTOP_UNAVAILABLE" => "当前交互桌面不可用，请解锁电脑后重新检查。",
        "RECOVERY_REQUIRED" => "存在未解决的切换记录，请先核实并处理原操作。",
        "STORAGE_UNAVAILABLE" => "远程考试状态文件不可用，原记录已保留，请先处理存储问题。",
        "STATE_CHANGED" => "本地状态已变化，请重新核实后再操作。",
        "POLICY_CHANGED" => "学校绑定或许可版本已变化，请刷新后重新确认。",
        "CONTROL_OFFLINE" => "请等待当前学校连接恢复后再开启许可；离线时仍可关闭已有许可。",
        "POLICY_STORE_UNAVAILABLE" => "本地许可文件不可用，控制已禁用，原文件已保留。",
        "RESOLUTION_NOT_REQUIRED" => "该操作没有持有 N3 暂停，无需结束。",
        "OPERATION_NOT_FOUND" => "找不到该操作记录，请刷新状态。",
        _ => "本机检查未通过：" + code
    };

    private async Task WatchAsync(Stream pipe, HostRequest request, CancellationToken token)
    {
        bool accepted = monitor is not null && !_watchStopping.Task.IsCompleted && await _subscriptions.WaitAsync(0, token);
        try
        {
            do
            {
                var snapshot = accepted || (_watchStopping.Task.IsCompleted && monitor is not null)
                    ? monitor!.Snapshot(request.RequestId) : new WatchSnapshot(
                    Protocol.Version, request.RequestId, Guid.Empty, 0, Guid.Empty, DateTimeOffset.UtcNow,
                    "Rejected", "SubscriptionLimit", "状态订阅已达上限，请关闭多余窗口后重试。", null);
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
                deadline.CancelAfter(TimeSpan.FromSeconds(2));
                await Protocol.WriteAsync(pipe, snapshot, deadline.Token);
                if (!accepted || snapshot.Outcome == "Stopped") return;
                await Task.WhenAny(Task.Delay(TimeSpan.FromSeconds(1), token), _watchStopping.Task);
            } while (!token.IsCancellationRequested);
        }
        finally { if (accepted) _subscriptions.Release(); }
    }

    private async Task DrainSubscriptionsAsync(CancellationToken token)
    {
        _watchStopping.TrySetResult();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(4));
        int acquired = 0;
        try
        {
            // Both slots become available only after the active writers have completed.
            // Each write is independently bounded, so an unread client cannot hold shutdown forever.
            for (; acquired < 2; acquired++) await _subscriptions.WaitAsync(deadline.Token);
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            log(JsonSerializer.Serialize(new { timestamp = DateTimeOffset.UtcNow, error = "SubscriptionShutdownDeadline" }));
        }
        finally { if (acquired > 0) _subscriptions.Release(acquired); }
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

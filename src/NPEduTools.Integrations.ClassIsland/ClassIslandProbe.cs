#nullable enable
using System.Collections.Concurrent;
using ClassIsland.Shared.IPC;
using ClassIsland.Shared.IPC.Abstractions.Services;
using dotnetCampus.Ipc.CompilerServices.GeneratedProxies;
using dotnetCampus.Ipc.Exceptions;
using NPEduTools.Core;
using System.Threading.Channels;

namespace NPEduTools.Integrations.ClassIsland;

/// <summary>Only run inside an isolated, disposable worker process.</summary>
public static class ClassIslandProbe
{
    public static string DefaultPipeName => IpcClient.PipeName;

    public static async Task<StatusResult> ReadAsync(string pipeName, int observeMs)
    {
        StatusResult? result = null;
        await RunAsync(pipeName, observeMs, value => { result = value; return Task.CompletedTask; }, false, CancellationToken.None);
        return result!;
    }

    public static Task MonitorAsync(string pipeName, Func<StatusResult, Task> publish, CancellationToken token) =>
        RunAsync(pipeName, 0, publish, true, token);

    private static async Task RunAsync(string pipeName, int observeMs, Func<StatusResult, Task> publish,
        bool continuous, CancellationToken token)
    {
        var client = new IpcClient();
        using var provider = client.Provider;
        var events = new ConcurrentDictionary<string, long>();
        int disconnected = 0;
        var changed = Channel.CreateBounded<bool>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite });
        foreach (string id in new[]
        {
            IpcRoutedNotifyIds.OnClassNotifyId,
            IpcRoutedNotifyIds.OnBreakingTimeNotifyId,
            IpcRoutedNotifyIds.OnAfterSchoolNotifyId,
            IpcRoutedNotifyIds.CurrentTimeStateChangedNotifyId
        })
        {
            events[id] = 0;
            client.JsonIpcProvider.AddNotifyHandler(id, () =>
            {
                events.AddOrUpdate(id, 1, (_, n) => n + 1);
                changed.Writer.TryWrite(true);
            });
        }

        try
        {
            // Mirror IpcClient.Connect(), allowing a private test pipe without touching the real app.
            provider.StartServer();
            client.JsonIpcProvider.StartServer();
            var peer = await provider.GetAndConnectToPeerAsync(pipeName);
            peer.PeerConnectionBroken += (_, _) =>
            {
                Interlocked.Exchange(ref disconnected, 1);
                changed.Writer.TryWrite(true);
            };
            var service = provider.CreateIpcProxy<IPublicLessonsService, StrictLessonsShape>(peer);
            if (observeMs > 0) await Task.Delay(observeMs);
            // The library can complete Connect inline on its receive loop. Its generated
            // getters block on Task.Result, so never execute them on that continuation.
            do
            {
                token.ThrowIfCancellationRequested();
                var result = await Task.Run(() =>
                {
                    var started = DateTimeOffset.UtcNow;
                    var state = service.CurrentState;
                    var subject = service.CurrentSubject?.Name;
                    bool timer = service.IsTimerRunning;
                    bool loaded = service.IsClassPlanLoaded;
                    bool enabled = service.IsClassPlanEnabled;
                    int index = service.CurrentSelectedIndex;
                    if (Volatile.Read(ref disconnected) != 0)
                        return new StatusResult("Unavailable", "ClassIslandDisconnected", "ClassIsland 在查询期间断开连接。");
                    if (!Enum.IsDefined(state))
                        return new StatusResult("Failed", "UnexpectedTimeState", "收到无法识别的课程状态，请核对 ClassIsland 版本。");
                    return new StatusResult("Succeeded", null, "已读取 ClassIsland 课程状态。", new(
                        started, DateTimeOffset.UtcNow, state.ToString(), subject, timer, loaded, enabled, index,
                        events.ToDictionary(pair => pair.Key, pair => pair.Value)));
                });
                await publish(result);
                if (!continuous || result.Outcome != "Succeeded") return;
                // Coalesce bursts, retain cumulative counters, and reconcile even without notifications.
                await Task.Delay(200, token);
                using var interval = CancellationTokenSource.CreateLinkedTokenSource(token);
                interval.CancelAfter(TimeSpan.FromSeconds(1));
                try { await changed.Reader.ReadAsync(interval.Token); }
                catch (OperationCanceledException) when (!token.IsCancellationRequested) { }
                while (changed.Reader.TryRead(out _)) { }
            } while (!token.IsCancellationRequested);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception) when (Volatile.Read(ref disconnected) != 0)
        {
            await publish(new("Unavailable", "ClassIslandDisconnected", "ClassIsland 在查询期间断开连接。"));
        }
        // alpha410 exposes this exception as internal; match its exact identity at this adapter boundary.
        catch (Exception ex) when (ex.GetBaseException().GetType().FullName == "dotnetCampus.Ipc.Exceptions.IpcInvokingTimeoutException")
        {
            await publish(new("TimedOut", "ClassIslandCallTimedOut", "ClassIsland 已连接，但课程接口未及时响应。"));
        }
        catch (Exception ex) when (ex.GetBaseException() is IpcPeerConnectionBrokenException)
        {
            await publish(new("Unavailable", "ClassIslandDisconnected", "ClassIsland 连接已断开。"));
        }
        catch (Exception ex) when (ex.GetBaseException() is UnauthorizedAccessException)
        {
            await publish(new("Unavailable", "ClassIslandAccessDenied", "ClassIsland IPC 拒绝访问，请核对用户会话和进程权限。"));
        }
        catch (Exception ex)
        {
            // Keep exception text (which may contain paths or lesson data) out of the protocol.
            Console.Error.WriteLine($"ClassIsland probe error: {ex.GetBaseException().GetType().Name}");
            await publish(new("Failed", "ClassIslandQueryFailed", "ClassIsland 查询失败，请核对接口兼容性。"));
        }
    }
}

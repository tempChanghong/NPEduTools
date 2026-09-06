#nullable enable
using System.Collections.Concurrent;
using ClassIsland.Shared.IPC;
using ClassIsland.Shared.IPC.Abstractions.Services;
using dotnetCampus.Ipc.CompilerServices.GeneratedProxies;
using dotnetCampus.Ipc.Exceptions;
using NPEduTools.Core;

namespace NPEduTools.Integrations.ClassIsland;

/// <summary>Only run inside an isolated, disposable worker process.</summary>
public static class ClassIslandProbe
{
    public static string DefaultPipeName => IpcClient.PipeName;

    public static async Task<StatusResult> ReadAsync(string pipeName, int observeMs)
    {
        var client = new IpcClient();
        using var provider = client.Provider;
        var events = new ConcurrentDictionary<string, long>();
        int disconnected = 0;
        foreach (string id in new[]
        {
            IpcRoutedNotifyIds.OnClassNotifyId,
            IpcRoutedNotifyIds.OnBreakingTimeNotifyId,
            IpcRoutedNotifyIds.OnAfterSchoolNotifyId,
            IpcRoutedNotifyIds.CurrentTimeStateChangedNotifyId
        })
        {
            events[id] = 0;
            client.JsonIpcProvider.AddNotifyHandler(id, () => events.AddOrUpdate(id, 1, (_, n) => n + 1));
        }

        try
        {
            // Mirror IpcClient.Connect(), allowing a private test pipe without touching the real app.
            provider.StartServer();
            client.JsonIpcProvider.StartServer();
            var peer = await provider.GetAndConnectToPeerAsync(pipeName);
            peer.PeerConnectionBroken += (_, _) => Interlocked.Exchange(ref disconnected, 1);
            var service = provider.CreateIpcProxy<IPublicLessonsService, StrictLessonsShape>(peer);
            if (observeMs > 0) await Task.Delay(observeMs);
            // The library can complete Connect inline on its receive loop. Its generated
            // getters block on Task.Result, so never execute them on that continuation.
            return await Task.Run(() =>
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
        }
        catch (Exception) when (Volatile.Read(ref disconnected) != 0)
        {
            return new("Unavailable", "ClassIslandDisconnected", "ClassIsland 在查询期间断开连接。");
        }
        // alpha410 exposes this exception as internal; match its exact identity at this adapter boundary.
        catch (Exception ex) when (ex.GetBaseException().GetType().FullName == "dotnetCampus.Ipc.Exceptions.IpcInvokingTimeoutException")
        {
            return new("TimedOut", "ClassIslandCallTimedOut", "ClassIsland 已连接，但课程接口未及时响应。");
        }
        catch (Exception ex) when (ex.GetBaseException() is IpcPeerConnectionBrokenException)
        {
            return new("Unavailable", "ClassIslandDisconnected", "ClassIsland 连接已断开。");
        }
        catch (Exception ex) when (ex.GetBaseException() is UnauthorizedAccessException)
        {
            return new("Unavailable", "ClassIslandAccessDenied", "ClassIsland IPC 拒绝访问，请核对用户会话和进程权限。");
        }
        catch (Exception ex)
        {
            // Keep exception text (which may contain paths or lesson data) out of the protocol.
            Console.Error.WriteLine($"ClassIsland probe error: {ex.GetBaseException().GetType().Name}");
            return new("Failed", "ClassIslandQueryFailed", "ClassIsland 查询失败，请核对接口兼容性。");
        }
    }
}

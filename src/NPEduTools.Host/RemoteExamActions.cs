using System.Diagnostics;
using NPEduTools.Contracts;

namespace NPEduTools.Host;

/// <summary>Real Host services, with only the OS boundary replaceable for isolated tests.</summary>
public sealed class RemoteExamActions(LaunchService launch, ExamAwareService examAware,
    RecordingService recording, ClassroomModeService classroom, IRemoteExamPlatform platform) : IRemoteExamActions
{
    public Task<IDisposable> ReserveIdleRecordingAsync(CancellationToken token) => recording.ReserveIdleForRuntimeAsync(token);
    public Task<IDisposable> FinishRecordingAndReserveAsync(CancellationToken token) => recording.FinishAndReserveForExamAsync(token);

    private async Task<RemoteExamConfiguration> ReadConfigurationAsync(CancellationToken token)
    {
        var ci = await launch.HandleAsync(new(Protocol.Version, Guid.NewGuid(), "classisland.config.get"), token);
        if (ci.Outcome != "Succeeded" || ci.Launch is not { StorageWarning: null, Settings.ExecutablePath: not null } data)
            throw new RemoteExamException("CLASSISLAND_CONFIGURATION_REQUIRED");
        var ea = examAware.Snapshot();
        if (string.IsNullOrWhiteSpace(ea.ExecutablePath) || ea.BridgeState == "Unavailable")
            throw new RemoteExamException("EXAMAWARE_CONFIGURATION_REQUIRED");
        var config = new RemoteExamConfiguration(data.Settings.ExecutablePath, data.Settings.Revision,
            ea.ExecutablePath, ea.Revision);
        platform.ValidateConfiguration(config);
        return config;
    }

    public Task<RemoteExamObservation> InspectAsync(CancellationToken token) => InspectCoreAsync(token, false);

    private async Task<RemoteExamObservation> InspectCoreAsync(CancellationToken token, bool ownsModeChange)
    {
        token.ThrowIfCancellationRequested();
        var config = await ReadConfigurationAsync(token);
        var state = examAware.Snapshot();
        // Inspect all instances, even when the bridge is disconnected. Do not launch over an
        // unverified executable or treat a fresh socket alone as proof of process identity.
        bool examRunning = platform.ExamAwareRunning(config.ExamAwarePath);
        bool ready = state.BridgeState == "Connected";
        if (ready) await examAware.VerifyConnectedProcessAsync(config.ExamAwarePath, config.ExamAwareRevision);
        var ci = platform.InspectClassIsland(config.ClassIslandPath);
        if (ci.State is not ("Stopped" or "Standard" or "Administrator"))
            throw new RemoteExamException("CLASSISLAND_IDENTITY_UNAVAILABLE");
        var mode = classroom.Snapshot;
        if (config != await ReadConfigurationAsync(token)) throw new RemoteExamException("CONFIGURATION_DRIFT");
        return new(config, mode.Revision, ready, ci.State == "Stopped",
            DesktopAvailable: platform.DesktopAvailable,
            RecoveryRequired: (!ownsModeChange && (mode.Recovery is not null || mode.Runtime is not null)) || mode.Phase == "Unavailable",
            PendingActions: classroom.Busy || state.Quit?.State is "Sending" or "AwaitingExit" || state.AutoStartChange?.State == "Sending" || state.PlanOperation?.State == "Sending",
            HostElevated: platform.HostElevated, ClassroomMode: mode.Mode,
            StartupReady: ready ? await classroom.ObserveExamStartupAsync(token) : null,
            ExamAwareStopped: !examRunning, ClassIslandReady: ci.State == "Administrator" && await classroom.ObserveClassIslandReadyAsync(token),
            DailyStartupReady: await classroom.ObserveDailyStartupAsync(token));
    }

    public Task ValidateModeAsync(CancellationToken token) => classroom.ValidateRemoteAsync(token);

    public Task<long> EnterDailyModeAsync(RemoteExamConfiguration expected, long revision, Func<Task> authorize,
        Action<string> progress, CancellationToken token) => classroom.EnterRemoteDailyAsync(expected, revision,
        async ownedRevision =>
        {
            await authorize();
            RemoteExamExecutor.Check(await InspectCoreAsync(token, true),
                new(Guid.NewGuid(), 0, ownedRevision, SwitchMode: true, Target: "Daily"), expected);
        }, async beforeDispatch =>
        {
            await RequireConfigurationAsync(expected, token);
            if (!platform.ExamAwareRunning(expected.ExamAwarePath)) return;
            await examAware.VerifyConnectedProcessAsync(expected.ExamAwarePath, expected.ExamAwareRevision);
            await examAware.QuitUnderRuntimeLeaseAsync(expected.ExamAwareRevision, beforeDispatch);
            var deadline = Stopwatch.StartNew();
            while (platform.ExamAwareRunning(expected.ExamAwarePath) && deadline.Elapsed < TimeSpan.FromSeconds(3))
                await Task.Delay(150, token);
            if (platform.ExamAwareRunning(expected.ExamAwarePath)) throw new RemoteExamException("EXAMAWARE_EXIT_FAILED");
        }, progress, token);

    public Task<long> EnterExamModeAsync(RemoteExamConfiguration expected, long revision, Func<Task> authorize,
        Action<string> progress, CancellationToken token) => classroom.EnterRemoteExamAsync(expected, revision,
        async ownedRevision =>
        {
            await authorize();
            var current = await InspectCoreAsync(token, true);
            RemoteExamExecutor.Check(current, new(Guid.NewGuid(), 0, ownedRevision), expected);
            if (!current.ExamAwareReady) throw new RemoteExamException("EXAMAWARE_NOT_READY");
        }, async beforeDispatch =>
        {
            await CloseClassIslandAsync(expected, beforeDispatch, token);
            await beforeDispatch();
            var current = await InspectCoreAsync(token, true);
            if (!current.ExamAwareReady || !current.ClassIslandStopped) throw new RemoteExamException("TARGET_NOT_READY");
        }, progress, token);

    public async Task PrepareExamAsync(RemoteExamConfiguration expected, Func<Task> beforeDispatch, CancellationToken token)
    {
        await RequireConfigurationAsync(expected, token);
        bool running = platform.ExamAwareRunning(expected.ExamAwarePath);
        if (!running) await examAware.StartForRemoteExamAsync(expected, beforeDispatch, token);
        // An existing unconnected instance may still be starting. Wait, without launching twice.
        var deadline = Stopwatch.StartNew();
        while (deadline.Elapsed < TimeSpan.FromSeconds(18))
        {
            token.ThrowIfCancellationRequested();
            await beforeDispatch();
            var current = await InspectAsync(token);
            if (current.Configuration != expected) throw new RemoteExamException("CONFIGURATION_DRIFT");
            if (current.ExamAwareReady) return;
            await Task.Delay(250, token);
        }
        throw new RemoteExamException("EXAMAWARE_NOT_READY");
    }

    public async Task CloseClassIslandAsync(RemoteExamConfiguration expected, Func<Task> beforeDispatch, CancellationToken token)
    {
        await RequireConfigurationAsync(expected, token);
        var instance = platform.InspectClassIsland(expected.ClassIslandPath);
        if (instance.State == "Stopped") return;
        if (instance.ProcessId is not > 0 || instance.Started is not > 0)
            throw new RemoteExamException("CLASSISLAND_IDENTITY_UNAVAILABLE");
        await platform.CloseClassIslandAsync(expected.ClassIslandPath, instance, async () =>
        {
            token.ThrowIfCancellationRequested();
            await beforeDispatch();
            await RequireConfigurationAsync(expected, token);
            await examAware.VerifyConnectedProcessAsync(expected.ExamAwarePath, expected.ExamAwareRevision);
        }, token);
    }

    private async Task RequireConfigurationAsync(RemoteExamConfiguration expected, CancellationToken token)
    {
        if (!platform.HostElevated) throw new RemoteExamException("HOST_NOT_ELEVATED");
        if (!platform.DesktopAvailable) throw new RemoteExamException("DESKTOP_UNAVAILABLE");
        if (expected != await ReadConfigurationAsync(token)) throw new RemoteExamException("CONFIGURATION_DRIFT");
    }
}

public sealed record RemoteExamProcess(string State, int? ProcessId = null, long? Started = null);

public interface IRemoteExamPlatform
{
    bool HostElevated { get; }
    bool DesktopAvailable { get; }
    void ValidateConfiguration(RemoteExamConfiguration config);
    bool ExamAwareRunning(string path);
    RemoteExamProcess InspectClassIsland(string path);
    Task CloseClassIslandAsync(string path, RemoteExamProcess instance, Func<Task> beforeDispatch, CancellationToken token);
}

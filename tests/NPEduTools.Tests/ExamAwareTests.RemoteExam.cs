using NPEduTools.Contracts;
using NPEduTools.Core;
using NPEduTools.Host;

namespace NPEduTools.Tests;

public sealed partial class ExamAwareTests
{
    private sealed class RemoteLaunchTarget : IClassIslandLaunchTarget
    {
        public string ValidateExecutable(string path) => path;
        public bool IsRunning(string path) => true;
        public int Start(string path) => throw new InvalidOperationException("N3 must not start ClassIsland.");
    }
    private sealed class RemoteReader : ILessonStatusReader
    {
        public Task<StatusResult> ReadAsync(StatusQuery query, CancellationToken cancellationToken) =>
            Task.FromResult(new StatusResult("Succeeded", null, "fixture"));
    }
    private sealed class ForbiddenStartupEffects : IClassroomModeEffects
    {
        public Task<ClassroomStartupSnapshot> ObserveAsync(bool connect) => throw new InvalidOperationException("Legacy mode invoked");
        public Task SetClassIslandAsync(ClassroomStartupSnapshot expected, bool enabled) => throw new InvalidOperationException("Startup changed");
        public Task SetExamAwareAsync(ClassroomStartupSnapshot expected, bool enabled) => throw new InvalidOperationException("Startup changed");
        public Task PauseRecordingAsync() => throw new InvalidOperationException("Old pause invoked");
    }
    private sealed class RemotePlatform : IRemoteExamPlatform
    {
        public bool HostElevated { get; set; } = true;
        public bool DesktopAvailable => true;
        public bool ExamRunning;
        public int Closes;
        public RemoteExamProcess Process = new("Administrator", 123, 456);
        public Func<Task>? OnClose;
        public void ValidateConfiguration(RemoteExamConfiguration config) { }
        public bool ExamAwareRunning(string path) => ExamRunning;
        public RemoteExamProcess InspectClassIsland(string path) => Process;
        public async Task CloseClassIslandAsync(string path, RemoteExamProcess instance, Func<Task> beforeDispatch, CancellationToken token)
        {
            Assert.Equal(Process, instance);
            if (OnClose is not null) await OnClose();
            await beforeDispatch();
            Closes++;
            Process = new("Stopped");
        }
    }
    private sealed class RemoteAuthorization : IRemoteExamAuthorization
    {
        public Func<bool> Revoked = () => false;
        public Task CheckAsync(bool firstEffect, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (Revoked()) throw new RemoteExamException("AUTH_REVOKED");
            return Task.CompletedTask;
        }
    }
    private sealed class RemoteFixture : IAsyncDisposable
    {
        public readonly RuntimeOperationGate Gate = new();
        public readonly Target Target = new();
        public readonly RemotePlatform Platform = new();
        public readonly RemoteAuthorization Authorization = new();
        public readonly LaunchService Launch;
        public readonly ExamAwareService Exam;
        public readonly RecordingService Recording;
        public readonly ClassroomModeService Classroom;
        public readonly ClassroomModeStore ModeStore;
        public readonly RemoteModeEffects ModeEffects = new();
        public readonly RemoteExamStore Store;
        public readonly RemoteExamExecutor Executor;
        public RemoteFixture(string directory, bool switchMode = false)
        {
            Launch = new(directory, new RemoteLaunchTarget(), new RemoteReader(), runtimeGate: Gate);
            Exam = new(directory, Target, Gate);
            Recording = new("NPEduTools.Test." + Guid.NewGuid(), directory, () => SchoolClockFrame.Unavailable("fixture"), runtimeGate: Gate);
            ModeStore = new(directory);
            Classroom = new(ModeStore, switchMode ? ModeEffects : new ForbiddenStartupEffects(), Gate);
            Store = new(directory);
            Executor = new(Store, Gate, new RemoteExamActions(Launch, Exam, Recording, Classroom, Platform));
        }
        public async Task ConfigureAsync()
        {
            Assert.Equal("Succeeded", (await Launch.HandleAsync(new(Protocol.Version, Guid.NewGuid(), "classisland.config.set",
                ExecutablePath: @"C:\Test\ClassIsland.exe", ExpectedRevision: 0), default)).Outcome);
            Assert.Equal("Succeeded", (await Configure(Exam)).Outcome);
        }
        public Task<RemoteExamEntry> RunAsync(bool switchMode = false) => Executor.RunAsync(
            new(Guid.NewGuid(), Store.State.Revision, Classroom.Snapshot.Revision, switchMode), Authorization);
        public async ValueTask DisposeAsync()
        {
            await Classroom.DisposeAsync(); await Recording.DisposeAsync();
            await Exam.DisposeAsync(); await Launch.DisposeAsync();
        }
    }

    [Fact]
    public async Task RuntimeAdapterUsesVerifiedBridgeWithoutAutostartCapabilityOrTask()
    {
        await using var f = new RemoteFixture(_directory);
        await f.ConfigureAsync();
        var pairing = await Pairing(f.Exam);
        var (client, hello) = await Connect(pairing);
        using (client)
        {
            await Send(client, hello, pairing, registered: null, canSetAutoStart: false);
            await Wait(() => f.Exam.Snapshot().BridgeState == "Connected");
            f.Platform.ExamRunning = true;
            var legacy = f.Classroom.Snapshot;
            var result = await f.RunAsync();
            Assert.Equal("SUCCEEDED", result.Outcome);
            Assert.Equal(1, f.Platform.Closes);
            Assert.Equal(0, f.Target.Starts);
            Assert.Equal(legacy, f.Classroom.Snapshot);
            Assert.Null(f.Exam.Snapshot().AutoStartChange);
            Assert.True(f.Store.State.AutomaticPaused);
        }
    }

    [Fact]
    public async Task RuntimeAdapterRechecksAuthorizationAtActualCloseDispatch()
    {
        await using var f = new RemoteFixture(_directory);
        await f.ConfigureAsync();
        var pairing = await Pairing(f.Exam);
        var (client, hello) = await Connect(pairing);
        using (client)
        {
            await Send(client, hello, pairing);
            await Wait(() => f.Exam.Snapshot().BridgeState == "Connected");
            f.Platform.ExamRunning = true;
            f.Platform.OnClose = () => { f.Authorization.Revoked = () => true; return Task.CompletedTask; };
            var result = await f.RunAsync();
            Assert.Equal("PARTIAL", result.Outcome);
            Assert.Equal("AUTH_REVOKED", result.Reason);
            Assert.Equal(0, f.Platform.Closes);
        }
    }

    [Fact]
    public async Task RuntimeAdapterLaunchesOnceAndDoesNotCloseSourceBeforeBridgeReadiness()
    {
        await using var f = new RemoteFixture(_directory);
        await f.ConfigureAsync();
        // Revoke immediately after the fake process launch, while waiting for the real bridge service.
        f.Authorization.Revoked = () => f.Target.Starts > 0;
        var result = await f.RunAsync();
        Assert.Equal("PARTIAL", result.Outcome);
        Assert.Equal("AUTH_REVOKED", result.Reason);
        Assert.Equal(1, f.Target.Starts);
        Assert.Equal(0, f.Platform.Closes);
        Assert.Null(f.Target.Link);
    }

    [Fact]
    public async Task RuntimeAdapterRejectsUnprivilegedHostBeforeAnyLaunch()
    {
        await using var f = new RemoteFixture(_directory);
        await f.ConfigureAsync();
        f.Platform.HostElevated = false;
        Assert.Equal("HOST_NOT_ELEVATED", (await f.RunAsync()).Reason);
        Assert.Equal(0, f.Target.Starts);
        Assert.Equal(0, f.Platform.Closes);
        Assert.False(f.Store.State.AutomaticPaused);
    }

    [Fact]
    public async Task RuntimeReservationBlocksRealLocalServicesButAllowsReads()
    {
        await using var f = new RemoteFixture(_directory);
        await f.ConfigureAsync();
        using var reservation = f.Gate.TryReserveSwitch(); Assert.NotNull(reservation);
        foreach (string capability in new[] { "examaware.start", "examaware.quit", "examaware.pairing.reset", "examaware.autostart.set" })
            Assert.Equal("RuntimeOperationBusy", (await f.Exam.HandleAsync(Request(capability))).ErrorCode);
        Assert.Equal("RuntimeOperationBusy", (await f.Launch.HandleAsync(Request("classisland.start"), default)).ErrorCode);
        Assert.Equal("RuntimeOperationBusy", f.Classroom.Handle(Request("classroom.refresh")).ErrorCode);
        Assert.Equal("RuntimeOperationBusy", (await f.Recording.HandleAsync(new(Protocol.Version, Guid.NewGuid(), "recording.automatic",
            Automatic: new("disable", Guid.NewGuid())))).ErrorCode);
        Assert.Equal("Succeeded", (await f.Exam.HandleAsync(Request("examaware.status"))).Outcome);
        Assert.Equal("Succeeded", (await f.Launch.HandleAsync(Request("classisland.config.get"), default)).Outcome);
        Assert.Equal("Succeeded", f.Classroom.Handle(Request("classroom.status")).Outcome);
        Assert.Equal(0, f.Target.Starts);
    }
}

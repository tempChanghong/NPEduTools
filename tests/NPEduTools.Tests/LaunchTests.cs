using System.Runtime.Versioning;
using NPEduTools.Contracts;
using NPEduTools.Core;
using NPEduTools.Host;

namespace NPEduTools.Tests;

[SupportedOSPlatform("windows")]
public sealed class LaunchTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "NPEduTools.LaunchTests", Guid.NewGuid().ToString("N"));
    private static HostRequest Request(string capability, Guid? id = null) => new(Protocol.Version, id ?? Guid.NewGuid(), capability);
    private static Task<HostResponse> ConfigureAsync(LaunchService service, long revision = 0, string path = @"C:\Test\ClassIsland.exe") =>
        service.HandleAsync(new(Protocol.Version, Guid.NewGuid(), "classisland.config.set", ExecutablePath: path, ExpectedRevision: revision), default);

    private static async Task<LaunchExecution> FinishedAsync(LaunchService service)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (true)
        {
            var response = await service.HandleAsync(Request("classisland.execution.get"), deadline.Token);
            if (response.Launch?.Execution is { Outcome: not "Running" } record) return record;
            await Task.Delay(20, deadline.Token);
        }
    }

    [Fact]
    public async Task VerificationNeverStartsAnExitedProcess()
    {
        var target = new FakeTarget();
        await using var service = new LaunchService(_directory, target, new FakeReader());
        await ConfigureAsync(service);
        var request = new HostRequest(Protocol.Version, Guid.NewGuid(), "classisland.verify", ExecutablePath: @"C:\Test\ClassIsland.exe", ExpectedRevision: 1);
        Assert.Null(Protocol.Validate(request));
        await service.HandleAsync(request, default);
        Assert.Equal("ClassIslandExited", (await FinishedAsync(service)).ErrorCode);
        Assert.Equal(0, target.Starts);
    }

    [Fact]
    public async Task VerificationPersistsReadinessWithoutStartingOrUsingChangedConfiguration()
    {
        var target = new FakeTarget { Running = true };
        await using var service = new LaunchService(_directory, target, new FakeReader());
        await ConfigureAsync(service);
        var request = new HostRequest(Protocol.Version, Guid.NewGuid(), "classisland.verify", ExecutablePath: @"C:\Test\ClassIsland.exe", ExpectedRevision: 1);
        Assert.Equal("ConfigurationConflict", (await service.HandleAsync(request with { ExpectedRevision = 0 }, default)).ErrorCode);
        Assert.Equal("ConfigurationConflict", (await service.HandleAsync(request with { ExecutablePath = @"C:\Other\ClassIsland.exe" }, default)).ErrorCode);
        await service.HandleAsync(request, default);
        Assert.Equal("Succeeded", (await FinishedAsync(service)).Outcome);
        Assert.Equal("Succeeded", (await service.HandleAsync(request, default)).Outcome);
        Assert.Equal(0, target.Starts);
    }

    [Fact]
    public async Task DuplicateRequestSurvivesHostRestartWithoutAnotherLaunch()
    {
        var target = new FakeTarget();
        Guid id = Guid.NewGuid();
        await using (var service = new LaunchService(_directory, target, new FakeReader()))
        {
            Assert.Equal("Succeeded", (await ConfigureAsync(service)).Outcome);
            Assert.Equal("Running", (await service.HandleAsync(Request("classisland.start", id), default)).Outcome);
            Assert.Equal("Succeeded", (await FinishedAsync(service)).Outcome);
            Assert.Equal("Succeeded", (await service.HandleAsync(Request("classisland.start", id), default)).Outcome);
        }
        await using (var reopened = new LaunchService(_directory, target, new FakeReader()))
        {
            Assert.Equal("Succeeded", (await reopened.HandleAsync(Request("classisland.start", id), default)).Outcome);
            var response = await reopened.HandleAsync(Request("classisland.config.get"), default);
            Assert.Equal(1, response.Launch!.Settings.Revision);
            Assert.Equal(id, response.Launch.Execution!.RequestId);
        }
        Assert.Equal(1, target.Starts);
    }

    [Fact]
    public async Task ConcurrentClicksAreRejectedAndWindowDisconnectionCannotCancelAcceptedWork()
    {
        var target = new FakeTarget();
        var reader = new FakeReader { Pause = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously) };
        await using var service = new LaunchService(_directory, target, reader);
        await ConfigureAsync(service);
        using var client = new CancellationTokenSource();
        var accepted = await service.HandleAsync(Request("classisland.start"), client.Token);
        client.Cancel();
        Assert.Equal("Running", accepted.Outcome);
        Assert.Equal("LaunchBusy", (await service.HandleAsync(Request("classisland.start"), default)).ErrorCode);
        Assert.Equal("LaunchBusy", (await ConfigureAsync(service, 1)).ErrorCode);
        reader.Pause.SetResult();
        Assert.Equal("Succeeded", (await FinishedAsync(service)).Outcome);
        Assert.Equal(1, target.Starts);
    }

    [Fact]
    public async Task ExistingProcessIsVerifiedWithoutLaunchingAndTimeoutDoesNotLaunchAgain()
    {
        var target = new FakeTarget { Running = true };
        var reader = new FakeReader { Healthy = false };
        await using var service = new LaunchService(_directory, target, reader, TimeSpan.FromMilliseconds(200));
        await ConfigureAsync(service);
        await service.HandleAsync(Request("classisland.start"), default);
        Assert.Equal("TimedOut", (await FinishedAsync(service)).Outcome);
        reader.Healthy = true;
        await service.HandleAsync(Request("classisland.start"), default);
        Assert.Equal("Succeeded", (await FinishedAsync(service)).Outcome);
        Assert.Equal(0, target.Starts);
    }

    [Fact]
    public async Task FailedStartAndExitBeforeReadinessRemainDistinctFromSuccess()
    {
        var target = new FakeTarget { StartFailure = true };
        await using var service = new LaunchService(_directory, target, new FakeReader { Healthy = false });
        await ConfigureAsync(service);
        await service.HandleAsync(Request("classisland.start"), default);
        Assert.Equal("ProcessStartFailed", (await FinishedAsync(service)).ErrorCode);
        target.StartFailure = false;
        target.ExitImmediately = true;
        await service.HandleAsync(Request("classisland.start"), default);
        Assert.Equal("ClassIslandExited", (await FinishedAsync(service)).ErrorCode);
    }

    [Fact]
    public async Task ConfigurationUsesRevisionAndKeepsLastGoodValue()
    {
        await using var service = new LaunchService(_directory, new FakeTarget(), new FakeReader());
        await ConfigureAsync(service);
        Assert.Equal("ConfigurationConflict", (await ConfigureAsync(service, 0, @"C:\Other\ClassIsland.exe")).ErrorCode);
        var data = (await service.HandleAsync(Request("classisland.config.get"), default)).Launch!;
        Assert.Equal(@"C:\Test\ClassIsland.exe", data.Settings.ExecutablePath);
        Assert.Equal(1, data.Settings.Revision);
    }

    [Fact]
    public async Task InterruptedPersistedIntentBecomesUnknownAndIsNeverAutomaticallyReplayed()
    {
        Guid id = Guid.NewGuid();
        var store = new LaunchStore(_directory);
        store.Save(new(1, new(1, @"C:\Test\ClassIsland.exe"), [new(id, @"C:\Test\ClassIsland.exe",
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, "Running", null, "启动中")]));
        store.Dispose();
        var target = new FakeTarget();
        await using var service = new LaunchService(_directory, target, new FakeReader());
        var response = await service.HandleAsync(Request("classisland.start", id), default);
        Assert.Equal("Unknown", response.Outcome);
        Assert.Equal("HostInterrupted", response.ErrorCode);
        Assert.Equal(0, target.Starts);
    }

    [Fact]
    public async Task CorruptPrimaryShowsBackupButCannotLaunchFromIncompleteHistory()
    {
        await using (var service = new LaunchService(_directory, new FakeTarget(), new FakeReader()))
        {
            await ConfigureAsync(service);
            await ConfigureAsync(service, 1, @"C:\Other\ClassIsland.exe");
        }
        File.WriteAllText(Path.Combine(_directory, "classisland.json"), "broken");
        var target = new FakeTarget();
        await using var recovered = new LaunchService(_directory, target, new FakeReader());
        var response = await recovered.HandleAsync(Request("classisland.config.get"), default);
        Assert.Equal(1, response.Launch!.Settings.Revision);
        Assert.NotNull(response.Launch.StorageWarning);
        Assert.Single(Directory.GetFiles(_directory, "*.corrupt-*"));
        Assert.Equal("StorageUnavailable", (await recovered.HandleAsync(Request("classisland.start"), default)).ErrorCode);
        Assert.Equal(0, target.Starts);
    }

    [Fact]
    public async Task IntentWriteFailurePreventsExternalStart()
    {
        var target = new FakeTarget();
        await using var service = new LaunchService(_directory, target, new FakeReader());
        await ConfigureAsync(service);
        using var locked = new FileStream(Path.Combine(_directory, "classisland.json"), FileMode.Open, FileAccess.Read, FileShare.Read);
        Assert.Equal("StorageUnavailable", (await service.HandleAsync(Request("classisland.start"), default)).ErrorCode);
        Assert.Equal(0, target.Starts);
    }

    [Fact]
    public async Task ResultWriteFailureDisablesFurtherLaunchesAndDoesNotReportSuccess()
    {
        FileStream? locked = null;
        var target = new FakeTarget { OnStart = () => locked = new FileStream(Path.Combine(_directory, "classisland.json"), FileMode.Open, FileAccess.Read, FileShare.Read) };
        await using var service = new LaunchService(_directory, target, new FakeReader());
        await ConfigureAsync(service);
        try
        {
            await service.HandleAsync(Request("classisland.start"), default);
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            HostResponse response;
            do
            {
                await Task.Delay(20, deadline.Token);
                response = await service.HandleAsync(Request("classisland.execution.get"), deadline.Token);
            } while (response.Launch!.StorageWarning is null);
            Assert.NotEqual("Succeeded", response.Launch.Execution!.Outcome);
            Assert.Equal("StorageUnavailable", (await service.HandleAsync(Request("classisland.start"), default)).ErrorCode);
            Assert.Equal(1, target.Starts);
        }
        finally { locked?.Dispose(); }
    }

    [Theory]
    [InlineData("ClassIsland.exe")]
    [InlineData(@"\\server\ClassIsland.exe")]
    [InlineData(@"C:\Windows\System32\cmd.exe")]
    public void ExecutableValidationRejectsRelativeNetworkAndUnrelatedPrograms(string path)
        => Assert.Throws<LaunchTargetException>(() => new ClassIslandLaunchTarget().ValidateExecutable(path));

    public void Dispose()
    {
        string testRoot = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "NPEduTools.LaunchTests")) + Path.DirectorySeparatorChar;
        string resolved = Path.GetFullPath(_directory);
        if (!resolved.StartsWith(testRoot, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Unsafe test cleanup path.");
        if (Directory.Exists(resolved)) Directory.Delete(resolved, recursive: true);
    }

    private sealed class FakeTarget : IClassIslandLaunchTarget
    {
        public int Starts;
        public bool Running, StartFailure, ExitImmediately;
        public Action? OnStart;
        public string ValidateExecutable(string path) => path;
        public bool IsRunning(string executablePath) => Running;
        public int Start(string executablePath)
        {
            if (StartFailure) throw new LaunchTargetException("ProcessStartFailed", "启动失败");
            Interlocked.Increment(ref Starts);
            Running = !ExitImmediately;
            OnStart?.Invoke();
            return 123;
        }
    }

    private sealed class FakeReader : ILessonStatusReader
    {
        public bool Healthy = true;
        public TaskCompletionSource? Pause;
        public async Task<StatusResult> ReadAsync(StatusQuery query, CancellationToken cancellationToken)
        {
            if (Pause is not null) await Pause.Task.WaitAsync(cancellationToken);
            return Healthy ? new("Succeeded", null, "已读取", new(DateTimeOffset.UtcNow, DateTimeOffset.UtcNow,
                "None", null, true, false, false, -1, new Dictionary<string, long>())) : new("Unavailable", "NotReady", "未就绪");
        }
    }
}

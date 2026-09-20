using NPEduTools.App;
using NPEduTools.Contracts;
using NPEduTools.Host;

namespace NPEduTools.Tests;

public sealed partial class ClassroomModeTests
{
    private sealed class RuntimeActions : IClassroomRuntimeActions
    {
        public List<string> Calls = [];
        public string? Fail;
        public bool CiRunning, EaRunning = true;
        private Task Act(string step)
        {
            Calls.Add(step);
            if (Fail == step) throw new InvalidOperationException(step + ": blocked by editor, permission or unavailable peer");
            return Task.CompletedTask;
        }
        public Task ValidateAsync(ClassroomStartupSnapshot expected) => Act("validate");
        public async Task PrepareExamAsync(ClassroomStartupSnapshot expected) { await Act("ready-ea"); EaRunning = true; }
        public async Task CloseClassIslandAsync(ClassroomStartupSnapshot expected)
        {
            Assert.True(EaRunning);
            await Act("close-ci"); CiRunning = false;
        }
        public async Task CloseExamAsync(ClassroomStartupSnapshot expected)
        {
            if (!EaRunning) { Calls.Add("ea-already-stopped"); return; }
            await Act("close-ea"); EaRunning = false;
        }
        public async Task StartClassIslandAsync(ClassroomStartupSnapshot expected)
        {
            Assert.False(EaRunning);
            if (CiRunning) { Calls.Add("ci-already-started"); return; }
            await Act("start-ci"); CiRunning = true;
        }
        public async Task VerifyAsync(ClassroomStartupSnapshot expected, string target)
        {
            await Act("verify");
            Assert.Equal(target == "Daily", CiRunning);
            Assert.Equal(target == "Exam", EaRunning);
        }
    }
    [Fact]
    public async Task RunningOptionDefaultsOffAndHasNoProcessSideEffects()
    {
        await using var f = new Fixture(); f.Send(); await f.Finish();
        Assert.Empty(f.Effects.RuntimeActions.Calls);
        Assert.Null(f.Store.State.Runtime);
        Assert.False(new ClassroomModeCommand("Exam").SwitchRunning);
    }
    [Theory]
    [InlineData("Exam", "ready-ea", "close-ci")]
    [InlineData("Daily", "close-ea", "start-ci")]
    public async Task RuntimeSwitchCompletesInRequiredOrder(string target, string first, string second)
    {
        await using var f = new Fixture();
        f.Send(target: target, running: true); await f.Finish();
        Assert.Equal(new[] { "validate", first, second, "verify" }, f.Effects.RuntimeActions.Calls);
        Assert.Equal("Idle", f.Store.State.Phase); Assert.Equal(target, f.Store.State.Mode);
        Assert.Equal(target == "Exam", f.Store.State.AutomaticPaused);
        Assert.Null(f.Store.State.Runtime); Assert.Null(f.Store.State.Recovery);
    }
    [Theory]
    [InlineData("Exam", "ready-ea", "close-ci")]
    [InlineData("Exam", "close-ci", "verify")]
    [InlineData("Daily", "close-ea", "start-ci")]
    public async Task FailureNeverContinuesToNextProcessAction(string target, string failure, string forbidden)
    {
        await using var f = new Fixture(); f.Effects.RuntimeActions.Fail = failure;
        f.Send(target: target, running: true); await f.Finish();
        Assert.DoesNotContain(forbidden, f.Effects.RuntimeActions.Calls);
        Assert.Equal("Incomplete", f.Store.State.Phase); Assert.True(f.Store.State.AutomaticPaused);
        Assert.Equal("Unconfigured", f.Store.State.Mode); Assert.Equal(target, f.Store.State.Runtime!.Target);
        Assert.NotNull(f.Store.State.Recovery);
    }
    [Fact]
    public async Task RetryAfterExamExitDoesNotReopenItOrReapplyStartup()
    {
        await using var f = new Fixture(); f.Effects.RuntimeActions.Fail = "start-ci";
        f.Send(target: "Daily", running: true); await f.Finish();
        Assert.False(f.Effects.RuntimeActions.EaRunning);
        Assert.Equal("StartClassIsland", f.Store.State.Runtime!.Step);
        f.Effects.RuntimeActions.Fail = null;
        f.Effects.RuntimeActions.Calls.Clear(); f.Effects.Calls.Clear();
        Assert.Equal("Accepted", f.Send("classroom.retry", "Daily", running: true).Outcome); await f.Finish();
        Assert.Equal(new[] { "validate", "ea-already-stopped", "start-ci", "verify" }, f.Effects.RuntimeActions.Calls);
        Assert.Equal(new[] { "pause" }, f.Effects.Calls);
        Assert.Equal("Daily", f.Store.State.Mode); Assert.False(f.Store.State.AutomaticPaused);
    }
    [Fact]
    public async Task RetryAfterReadinessFailureDoesNotDuplicateExistingProcess()
    {
        await using var f = new Fixture(); f.Effects.RuntimeActions.Fail = "verify";
        f.Send(target: "Daily", running: true); await f.Finish();
        Assert.True(f.Effects.RuntimeActions.CiRunning);
        f.Effects.RuntimeActions.Fail = null; f.Effects.RuntimeActions.Calls.Clear();
        f.Send("classroom.retry", "Daily", running: true); await f.Finish();
        Assert.Contains("ci-already-started", f.Effects.RuntimeActions.Calls);
        Assert.DoesNotContain("start-ci", f.Effects.RuntimeActions.Calls);
        Assert.Equal("Idle", f.Store.State.Phase);
    }
    [Fact]
    public async Task RuntimeRecoverySurvivesRestartAndNeverRunsAutomatically()
    {
        await using var f = new Fixture(); f.Effects.RuntimeActions.Fail = "start-ci";
        f.Send(target: "Daily", running: true); await f.Finish();
        f.Effects.RuntimeActions.Calls.Clear();
        var restarted = new ClassroomModeStore(f.DirectoryPath);
        Assert.Equal(f.Store.State.Runtime, restarted.State.Runtime);
        Assert.True(restarted.State.AutomaticPaused);
        await using var service = new ClassroomModeService(restarted, f.Effects);
        Assert.Empty(f.Effects.RuntimeActions.Calls);
        f.Effects.RuntimeActions.Fail = null;
        var result = service.Handle(new(1, Guid.NewGuid(), "classroom.retry", ExpectedRevision: restarted.State.Revision,
            ClassroomMode: new("Daily", true, true)));
        Assert.Equal("Accepted", result.Outcome);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (service.Busy) await Task.Delay(10, deadline.Token);
        Assert.Equal("Daily", restarted.State.Mode); Assert.False(restarted.State.AutomaticPaused);
        Assert.DoesNotContain("ready-ea", f.Effects.RuntimeActions.Calls);
    }
    [Fact]
    public async Task RestoreClearsRuntimeIntentWithoutClosingAnyMorePrograms()
    {
        await using var f = new Fixture(); f.Effects.RuntimeActions.Fail = "close-ci";
        f.Send(running: true); await f.Finish();
        f.Effects.RuntimeActions.Calls.Clear();
        f.Send("classroom.restore"); await f.Finish();
        Assert.Empty(f.Effects.RuntimeActions.Calls); Assert.Null(f.Store.State.Runtime);
        Assert.Null(f.Store.State.Recovery); Assert.False(f.Store.State.AutomaticPaused);
    }
    [Fact]
    public async Task RetryCannotChangeTargetOrUseStaleConfirmation()
    {
        await using var f = new Fixture(); f.Effects.RuntimeActions.Fail = "start-ci";
        f.Send(target: "Daily", running: true); await f.Finish();
        Assert.Equal("NoRuntimeRetry", f.Send("classroom.retry", "Exam", running: true).ErrorCode);
        Assert.Equal("RevisionConflict", f.Service.Handle(new(1, Guid.NewGuid(), "classroom.retry",
            ExpectedRevision: 0, ClassroomMode: new("Daily", true, true))).ErrorCode);
    }
    [Theory]
    [InlineData("classroom.set")]
    [InlineData("classroom.retry")]
    public void ClosingExamRequiresExplicitSavedWorkConfirmation(string capability)
    {
        var request = new HostRequest(1, Guid.NewGuid(), capability, ExpectedRevision: 0, ClassroomMode: new("Daily", true));
        Assert.Equal("InvalidRuntimeConfirmation", Protocol.Validate(request));
        Assert.Null(Protocol.Validate(request with { ClassroomMode = new("Daily", true, true) }));
        Assert.NotNull(Protocol.Validate(request with { ClassroomMode = new("Exam", true, true) }));
    }
    [Theory]
    [InlineData("Daily", false, "日常", false)]
    [InlineData("Exam", true, "考试", false)]
    [InlineData("Unconfigured", false, "未设", false)]
    public void ModePresentationExplainsRecordingEligibility(string mode, bool paused, string label, bool attention)
    {
        var view = ClassroomModePresentation.From(new(Mode: mode, AutomaticPaused: paused));
        Assert.Equal(label, view.RailLabel); Assert.Equal(attention, view.Attention);
        Assert.Contains(paused ? "暂停" : "原有启用状态", view.Detail);
    }
    [Fact]
    public void DisconnectionAndIncompleteStateNeverShowCompletedMode()
    {
        Assert.Equal("未知", ClassroomModePresentation.From(null).RailLabel);
        var failed = ClassroomModePresentation.From(new(Mode: "Daily", Phase: "Incomplete", AutomaticPaused: true));
        Assert.Contains("切换未完成", failed.Title); Assert.Equal("待处理", failed.RailLabel);
        Assert.True(failed.Attention);
        Assert.Equal("切换", ClassroomModePresentation.From(new(Phase: "Running", AutomaticPaused: true)).RailLabel);
    }
}


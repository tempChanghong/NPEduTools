using NPEduTools.App;
using NPEduTools.Contracts;

namespace NPEduTools.Tests;

public sealed class RemoteExamPresentationTests
{
    private static RemoteExamHistory Entry(string outcome = "PARTIAL", string step = "SetStartup",
        string? reason = "STARTUP_NOT_READY", string target = "Exam") =>
        new(Guid.NewGuid(), outcome, step, reason, DateTimeOffset.Parse("2026-10-07T10:00:00Z"), null, target);

    private static RemoteExamStatus State(bool paused = false, string? storageError = null) =>
        new(new(1, true, null, Guid.NewGuid(), Guid.NewGuid(), true, "学校已连接"), 2, paused,
            paused ? Guid.NewGuid() : null, storageError, []);

    private static HostResponse Reply(string outcome = "Succeeded", string? code = null, RemoteExamStatus? state = null) =>
        new(Protocol.Version, Guid.NewGuid(), outcome, code, "后台提供的完整诊断说明", RemoteExam: state);

    private static string Body(RemoteExamHistoryItem item) =>
        string.Join("\n", item.Title, item.Step, item.Message, item.NextAction, item.LocallyEnded);

    [Fact]
    public void PartialStartupExplainsCauseWithoutInventingCompletedSteps()
    {
        var entry = Entry();
        var item = RemoteExamPresentation.History(entry);
        Assert.Contains("切换未完成", item.Title);
        Assert.Equal("最后记录的步骤：设置登录自启动", item.Step);
        Assert.Contains("部分软件或自启动设置可能已改变", item.Message);
        Assert.Contains("尚未得到确认", item.Message);
        Assert.Contains("ExamAware2", item.NextAction);
        Assert.Contains("ClassIsland", item.NextAction);
        Assert.DoesNotContain("STARTUP_NOT_READY", Body(item));
        Assert.DoesNotContain(entry.OperationId.ToString(), Body(item));
        Assert.DoesNotContain("ExamAware2 已就绪", Body(item));
        Assert.Contains("STARTUP_NOT_READY", item.Details);
        Assert.Contains("SetStartup", item.Details);
        Assert.Contains(entry.OperationId.ToString(), item.Details);
    }

    [Theory]
    [InlineData("CLASSISLAND_EXECUTABLE_INVALID", "ClassIsland")]
    [InlineData("EXAMAWARE_EXECUTABLE_INVALID", "ExamAware2")]
    public void PathFailuresIdentifyTheSoftwareToFix(string code, string software)
    {
        var item = RemoteExamPresentation.History(Entry("REJECTED", "Check", code));
        Assert.Contains("请求未执行", item.Title);
        Assert.Contains(software, item.Message);
        Assert.Contains(software, item.NextAction);
        Assert.DoesNotContain(code, Body(item));
    }

    [Theory]
    [InlineData("RECEIVED")]
    [InlineData("CHECKING")]
    [InlineData("RUNNING")]
    public void PendingReceiptDoesNotClaimCompletion(string outcome)
    {
        var item = RemoteExamPresentation.History(Entry(outcome, "Check", null));
        Assert.DoesNotContain("切换成功", item.Title);
        Assert.Contains("尚未完成", item.Message);
        Assert.Contains("不会自动重发", item.NextAction);
    }

    [Fact]
    public void UnknownProtocolValuesRemainUnconfirmedAndAvailableInDetails()
    {
        var item = RemoteExamPresentation.History(Entry("NEW_RESULT", "NEW_STEP", "NEW_REASON", "NEW_TARGET"));
        Assert.Contains("目标模式待核实", item.Title);
        Assert.Contains("结果待核实", item.Title);
        Assert.Contains("步骤待核实", item.Step);
        Assert.Contains("不能确认切换成功", item.Message);
        Assert.DoesNotContain("进入考试", item.Title);
        foreach (string raw in new[] { "NEW_RESULT", "NEW_STEP", "NEW_REASON", "NEW_TARGET" })
        {
            Assert.Contains(raw, item.Details);
            Assert.DoesNotContain(raw, Body(item));
        }
    }

    [Fact]
    public void LocallyEndedPartialReceiptKeepsItsOriginalFailure()
    {
        var entry = Entry() with { LocallyEndedAt = DateTimeOffset.Parse("2026-10-07T10:10:00Z") };
        var item = RemoteExamPresentation.History(entry);
        Assert.Contains("切换未完成", item.Title);
        Assert.DoesNotContain("切换成功", item.Title);
        Assert.Contains("远程录课暂停", item.LocallyEnded);
        Assert.Contains("原切换结果仍保留", item.LocallyEnded);
        Assert.Equal("PARTIAL", entry.Outcome);
        Assert.Equal("STARTUP_NOT_READY", entry.Reason);
    }

    [Fact]
    public void SuccessIsHistoricalAndDailyTargetIsDistinct()
    {
        var entry = Entry("SUCCEEDED", "Verify", null, "Daily");
        var item = RemoteExamPresentation.History(entry);
        Assert.Contains("返回日常", item.Title);
        Assert.Contains("历史回执", item.Title);
        Assert.Contains("不代表软件此刻", item.Message);
        Assert.Contains(entry.UpdatedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"), item.Title);
        Assert.Contains("取决于原开关、计划及课堂模式", RemoteExamPresentation.Runtime(State()));
    }

    [Fact]
    public void PassedPreflightDoesNotClaimActualSwitch()
    {
        var item = RemoteExamPresentation.Feedback(Reply(state: State()), RemoteExamLocalAction.CheckEnvironment);
        Assert.Equal("本机检查通过", item.Title);
        Assert.Contains("没有执行切换", item.Message);
        Assert.Contains("不等于已进入考试模式", item.NextAction);
    }

    [Fact]
    public void PassedInspectionIsReadOnly()
    {
        var item = RemoteExamPresentation.Feedback(Reply(state: State(true)), RemoteExamLocalAction.InspectCurrent);
        Assert.Equal("当前考试状态已核实", item.Title);
        Assert.Contains("没有执行软件切换", item.Message);
    }

    [Fact]
    public void EndPauseSuccessDoesNotMeanReturningDaily()
    {
        var item = RemoteExamPresentation.Feedback(Reply(state: State()), RemoteExamLocalAction.EndPause);
        Assert.Equal("已解除远程录课暂停", item.Title);
        Assert.Contains("没有切换软件", item.Message);
        Assert.Contains("没有清除历史", item.Message);
        Assert.Contains("不等同于返回日常", item.NextAction);
    }

    [Fact]
    public void MissingUnreadableOrStillPausedStateCannotConfirmPauseCleared()
    {
        foreach (var state in new RemoteExamStatus?[] { null, State(true), State(false, "STORAGE_UNAVAILABLE") })
        {
            var item = RemoteExamPresentation.Feedback(Reply(state: state), RemoteExamLocalAction.EndPause);
            Assert.Equal("操作结果待核实", item.Title);
            Assert.Contains("不能认定操作成功", item.Message);
            Assert.Contains("不会自动重发", item.NextAction);
        }
    }

    [Fact]
    public void FailedLocalEndPreservesDiagnosticAndDoesNotEncourageBlindResend()
    {
        var reply = Reply("Rejected", "LOCAL_CONTROL_UNAVAILABLE");
        var item = RemoteExamPresentation.Feedback(reply, RemoteExamLocalAction.EndPause);
        Assert.Equal("未能确认暂停已解除", item.Title);
        Assert.Contains("不要直接重复", item.NextAction);
        Assert.Contains(reply.Message, item.Details);
        Assert.Contains(reply.RequestId.ToString(), item.Details);
        Assert.DoesNotContain(reply.ErrorCode!, item.Message);
    }

    [Fact]
    public void UnrecognizedHostOutcomeCannotConfirmSuccess()
    {
        var item = RemoteExamPresentation.Feedback(Reply("NEW_OUTCOME", state: State()), RemoteExamLocalAction.CheckEnvironment);
        Assert.Equal("操作结果待核实", item.Title);
        Assert.Contains("NEW_OUTCOME", item.Details);
    }

    [Fact]
    public void CurrentPauseRequestRemainsAvailableEvenIfAbsentFromRecentHistory()
    {
        var state = State(true);
        Assert.Empty(state.History);
        Assert.DoesNotContain(state.PauseOperationId!.Value.ToString(), RemoteExamPresentation.Runtime(state));
        Assert.Contains(state.PauseOperationId.Value.ToString(), RemoteExamPresentation.RuntimeDetails(state));
        Assert.Equal("", RemoteExamPresentation.RuntimeDetails(null));
    }

    [Fact]
    public void MissingRuntimeIsNotEquivalentToNoPause()
    {
        Assert.Contains("状态未知", RemoteExamPresentation.Runtime(null));
        Assert.Contains("保护仍保留", RemoteExamPresentation.Runtime(State(false, "STORAGE_UNAVAILABLE")));
        Assert.Contains("已暂停", RemoteExamPresentation.Runtime(State(true)));
    }
}

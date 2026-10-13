using NPEduTools.App;
using NPEduTools.Contracts;

namespace NPEduTools.Tests;

public sealed class HomeRecordingTests
{
    [Theory]
    [InlineData("Recording", "进行中", "管理")]
    [InlineData("Paused", "暂停", "管理")]
    [InlineData("Saving", "保存", "管理")]
    [InlineData("Saved", "已保存", "结果")]
    [InlineData("Failed", "未完成", "结果")]
    [InlineData("Idle", "没有录课", "立即")]
    [InlineData("Unknown", "待确认", "检查")]
    public void CurrentTaskActionsFollowConfirmedPhase(string phase, string title, string action)
    {
        var state = new RecordingState(phase, "fixture");
        Assert.Contains(title, HomeRecordingPresentation.Title(state, true));
        Assert.Contains(action, HomeRecordingPresentation.Action(state, true));
        Assert.Contains("待确认", HomeRecordingPresentation.Title(state, false));
        Assert.Contains("检查", HomeRecordingPresentation.Action(state, false));
    }

    [Fact]
    public void UpcomingUsesSchoolWallTimeAndWithdrawsUncertainOrInvalidData()
    {
        var start = new DateTimeOffset(2031, 4, 7, 19, 15, 0, TimeSpan.Zero);
        var state = new AutomaticRecordingState(true, Guid.NewGuid(), "fixture", null, [], Next: new("数学", start, start.AddMinutes(45)), NextState: "Ready");
        Assert.Contains("19:15", HomeRecordingPresentation.Next(state, true));
        Assert.Contains("ClassIsland 时间", HomeRecordingPresentation.Next(state, true));
        Assert.DoesNotContain("数学", HomeRecordingPresentation.Next(state, false));
        foreach (var invalid in new[] { state with { NextState = "ClockPending" }, state with { Next = state.Next! with { ClockSource = "Windows" } },
            state with { Next = state.Next! with { Start = start.ToOffset(TimeSpan.FromHours(8)) } }, state with { Next = state.Next! with { End = start } } })
            Assert.DoesNotContain("19:15", HomeRecordingPresentation.Next(invalid, true));
        Assert.Contains("未开启", HomeRecordingPresentation.Next(state with { Enabled = false }, true));
        Assert.Contains("暂停", HomeRecordingPresentation.Next(state with { SuspendedByMode = true }, true));
        Assert.Contains("未提供", HomeRecordingPresentation.Next(state with { NextState = "Unknown", Next = null }, true));
        Assert.Contains("2031-04-08", HomeRecordingPresentation.Next(state with { Next = state.Next! with { End = start.AddDays(1) } }, true));
    }

}

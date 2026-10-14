using NPEduTools.App;
using NPEduTools.Contracts;

namespace NPEduTools.Tests;

public sealed class StudyConnectionPresentationTests
{
    private static StudyHostState Study(bool offline = false) => new("fixture", "APPLIED", "INTEGRATED", "ACTIVE",
        "Active", offline ? "OFFLINE_CONTINUING" : "WINDOW_ACTIVE", true, null, null, null, null);
    private static NpepState Online => new(1, "ACTIVE", "ONLINE", "fixture", LastReceivedAt: "2026-10-14T12:00:00.000Z");

    [Fact]
    public void Online_receipt_is_not_an_upload_receipt_and_ages_with_local_snapshot()
    {
        var label = StudyConnectionPresentation.Describe(Study(), Online, true);
        Assert.Equal("学校互联正常", label.Text); Assert.Equal("Normal", label.Tone);
        Assert.Contains("不代表本次报告或照片已上传", label.Detail);
        Assert.Equal("后台状态待确认", StudyConnectionPresentation.Describe(Study(), Online, false).Text);
    }
    [Theory]
    [InlineData(null)]
    [InlineData("invalid")]
    public void Online_without_a_valid_receipt_is_not_green(string? received)
    {
        var label = StudyConnectionPresentation.Describe(Study(), Online with { LastReceivedAt = received }, true);
        Assert.Equal("学校互联待确认", label.Text); Assert.NotEqual("Normal", label.Tone);
    }
    [Fact]
    public void Receipt_clock_skew_does_not_replace_Host_monotonic_freshness()
    {
        var state = Online with { LastReceivedAt = "2099-01-01T00:00:00.000Z" };
        Assert.Equal("Normal", StudyConnectionPresentation.Describe(Study(), state, true).Tone);
        Assert.Equal("Muted", StudyConnectionPresentation.Describe(Study(), state, false).Tone);
    }
    [Theory]
    [InlineData("OFFLINE", "学校正在重连 · 本次继续")]
    [InlineData("STOPPED", "学校连接已停止 · 本次继续")]
    [InlineData("ONLINE", "安排待同步 · 本次继续")]
    public void Continuing_window_is_distinguished_from_connection_and_policy_recovery(string connection, string text)
    {
        var label = StudyConnectionPresentation.Describe(Study(true), Online with { Connection = connection }, true);
        Assert.Equal(text, label.Text); Assert.Equal("Warning", label.Tone);
        Assert.Contains("到点结束", label.Detail);
        Assert.DoesNotContain("本次继续", StudyConnectionPresentation.Describe(Study(), Online with { Connection = connection }, true).Text);
    }
    [Theory]
    [InlineData("Preparing", true)]
    [InlineData("Ended", false)]
    [InlineData("Outside", false)]
    [InlineData("Blocked", false)]
    public void An_old_offline_reason_cannot_claim_an_ended_or_blocked_window_continues(string phase, bool continuing)
    {
        var label = StudyConnectionPresentation.Describe(Study(true) with { Phase = phase }, Online with { Connection = "OFFLINE" }, true);
        Assert.Equal(continuing, label.Text.Contains("本次继续"));
        Assert.DoesNotContain("本次继续", StudyConnectionPresentation.Describe(Study(true) with { PolicyState = "UNAVAILABLE" }, Online, true).Text);
    }
    [Fact]
    public void Explicit_pause_suspension_and_unknown_state_never_claim_automatic_retry()
    {
        Assert.Equal("学校互联已暂停", StudyConnectionPresentation.Describe(Study(), Online with { ReportingPaused = true }, true).Text);
        Assert.Equal("学校连接已停用", StudyConnectionPresentation.Describe(Study(), Online with { State = "SUSPENDED" }, true).Text);
        Assert.Equal("学校互联待确认", StudyConnectionPresentation.Describe(Study(), Online with { Connection = "UNKNOWN" }, true).Text);
        Assert.Equal("学校互联处理中", StudyConnectionPresentation.Describe(Study(), Online with { Busy = true }, true).Text);
        Assert.Equal("学校互联待确认", StudyConnectionPresentation.Describe(Study(), null, true).Text);
    }
    [Fact]
    public void Unpaired_manual_study_is_explicitly_local()
    {
        var manual = Study() with { Manual = new(Guid.NewGuid(), 1, null, "Active", null, null, null, false, 600) };
        var unpaired = Online with { State = "UNPAIRED", Connection = "DISABLED", LastReceivedAt = null };
        Assert.Equal("仅本机自习", StudyConnectionPresentation.Describe(manual, unpaired, true).Text);
        Assert.Equal("学校尚未连接", StudyConnectionPresentation.Describe(Study(), unpaired, true).Text);
    }
}

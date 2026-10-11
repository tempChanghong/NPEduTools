using NPEduTools.App;
using NPEduTools.Contracts;

namespace NPEduTools.Tests;

public sealed class NpepConnectionPresentationTests
{
    private static NpepState State(string stage = "ACTIVE", string connection = "OFFLINE", string? error = null) =>
        new(1, stage, connection, "后台状态说明", Error: error);

    [Fact]
    public void TlsRetryRetainsValidationAndDoesNotClaimOnline()
    {
        var state = State(error: "TLS_VALIDATION_FAILED");
        Assert.Contains("自动重试", NpepConnectionPresentation.Title(state));
        Assert.DoesNotContain("当前在线", NpepConnectionPresentation.Title(state));
        Assert.Contains("证书验证失败", NpepConnectionPresentation.Cause(state.Error));
        Assert.Contains("持续自动重试", NpepConnectionPresentation.NextAction(state));
        Assert.Contains("每次仍验证证书", NpepConnectionPresentation.NextAction(state));
        Assert.DoesNotContain(state.Error!, NpepConnectionPresentation.Cause(state.Error));
    }

    [Theory]
    [InlineData("UNPAIRED")]
    [InlineData("CREATING")]
    [InlineData("CONFIRMING")]
    public void OrdinaryOfflineCommandDoesNotPromiseAutomaticRecovery(string stage)
    {
        var state = State(stage, error: "NETWORK_UNAVAILABLE");
        Assert.DoesNotContain("自动重试", NpepConnectionPresentation.Title(state));
        Assert.Contains("恢复原操作", NpepConnectionPresentation.NextAction(state));
        state = state with { Error = "TLS_VALIDATION_FAILED" };
        Assert.Contains("自动重试", NpepConnectionPresentation.Title(state));
    }

    [Fact]
    public void StoppedConnectionDoesNotInheritOfflineRetry()
    {
        var state = State(connection: "STOPPED");
        Assert.Contains("连接已停止", NpepConnectionPresentation.Title(state));
        Assert.Contains("重新连接", NpepConnectionPresentation.NextAction(state));
        Assert.Contains("不会按离线状态自动重试", NpepConnectionPresentation.NextAction(state));
        state = state with { Error = "SESSION_SUPERSEDED" };
        Assert.Contains("不要反复重连争抢", NpepConnectionPresentation.NextAction(state));
    }

    [Fact]
    public void PauseKeepsBindingAndRequiresExplicitResume()
    {
        var state = State(error: "TLS_VALIDATION_FAILED") with { ReportingPaused = true };
        Assert.Contains("互联已暂停", NpepConnectionPresentation.Title(state));
        Assert.DoesNotContain("自动重试", NpepConnectionPresentation.NextAction(state));
        Assert.Contains("恢复互联", NpepConnectionPresentation.NextAction(state));
        Assert.Contains("不会自行解除暂停", NpepConnectionPresentation.NextAction(state));
    }

    [Fact]
    public void SchoolSuspensionAndUnreadableStoreOverridePauseAndBusy()
    {
        var state = State("SUSPENDED") with { ReportingPaused = true, Busy = true };
        Assert.Equal("学校连接已停用", NpepConnectionPresentation.Title(state));
        Assert.Contains("学校管理员", NpepConnectionPresentation.NextAction(state));
        Assert.DoesNotContain("恢复互联", NpepConnectionPresentation.NextAction(state));
        state = state with { State = "STORE_UNAVAILABLE" };
        Assert.Contains("凭据暂不可用", NpepConnectionPresentation.Title(state));
        Assert.Contains("保留原凭据", NpepConnectionPresentation.NextAction(state));
    }

    [Fact]
    public void BusyOperationDoesNotClaimItsOldOnlineState()
    {
        var state = State(connection: "ONLINE") with { Busy = true };
        Assert.Contains("正在处理", NpepConnectionPresentation.Title(state));
        Assert.DoesNotContain("当前在线", NpepConnectionPresentation.Title(state));
        Assert.Contains("不要重复提交", NpepConnectionPresentation.NextAction(state));
    }

    [Fact]
    public void UnknownConnectionOrStageCannotClaimOnlineOrRetry()
    {
        foreach (var state in new[] { State(connection: "NEW_CONNECTION"), State("NEW_STAGE", "OFFLINE", "TLS_VALIDATION_FAILED") })
        {
            Assert.Contains("待核实", NpepConnectionPresentation.Title(state));
            Assert.DoesNotContain("当前在线", NpepConnectionPresentation.Title(state));
            Assert.DoesNotContain("持续自动重试", NpepConnectionPresentation.NextAction(state));
        }
        Assert.Contains("后台状态未确认", NpepConnectionPresentation.Title(null));
        Assert.Contains("不要重复配对", NpepConnectionPresentation.NextAction(null));
    }

    [Fact]
    public void InvalidResponseDirectsUserToBackendInsteadOfRepeatedPairing()
    {
        var state = State("UNPAIRED", "STOPPED", "INVALID_RESPONSE");
        Assert.Contains("互联响应", NpepConnectionPresentation.Cause(state.Error));
        Assert.Contains("后端地址", NpepConnectionPresentation.NextAction(state));
        Assert.DoesNotContain("重新配对", NpepConnectionPresentation.NextAction(state));
    }

    [Fact]
    public void UnknownErrorsHaveChineseFallbackAndRawEvidenceWithoutCredentialDump()
    {
        var id = Guid.NewGuid();
        var state = State(error: "NEW_ERROR") with { OperationId = id, Server = new() { ["testSecret"] = "do-not-render" } };
        Assert.Contains("尚未识别", NpepConnectionPresentation.Cause(state.Error));
        string details = NpepConnectionPresentation.Details(state, null, null, null);
        Assert.Contains("NEW_ERROR", details);
        Assert.Contains(id.ToString(), details);
        Assert.Contains(state.Message, details);
        Assert.DoesNotContain("do-not-render", details);
    }

    [Fact]
    public void CommandRejectionKeepsItsOwnCauseSeparateFromOldStatusError()
    {
        var state = State(error: "TLS_VALIDATION_FAILED");
        string advice = NpepConnectionPresentation.NextAction(state, "NpepStateChanged");
        Assert.Contains("刷新", advice);
        Assert.DoesNotContain("持续自动重试", advice);
        Assert.Contains("操作错误码：NpepStateChanged", NpepConnectionPresentation.Details(state, "NpepStateChanged", Guid.NewGuid(), "操作仍在进行"));
    }

    [Fact]
    public void ConnectingDoesNotClaimFirstReceiptAndPendingApprovalContinuesPolling()
    {
        var state = State(connection: "CONNECTING");
        Assert.Contains("正在连接", NpepConnectionPresentation.Title(state));
        Assert.Contains("新的回执", NpepConnectionPresentation.NextAction(state));
        state = State("PENDING", "OFFLINE", "NETWORK_UNAVAILABLE");
        Assert.Contains("自动重试", NpepConnectionPresentation.Title(state));
        Assert.Contains("无需重新配对", NpepConnectionPresentation.NextAction(state));
    }
}

using System.Text.Json.Nodes;
using NPEduTools.App;
using NPEduTools.Contracts;

namespace NPEduTools.Tests;

public sealed class NpepConnectionSessionTests
{
    [Fact]
    public async Task WebCodeRequiresInspectedProviderAndConsentThenUsesNormalizedCode()
    {
        var host = new Host { Snapshot = State() with { Server = Server() } };
        var session = new NpepConnectionSession(host.Send) { UserCode = "abcd-2345" };
        Assert.False(session.CanClaim);
        await session.RefreshAsync(); Assert.False(session.CanClaim);
        session.ServerConfirmed = true; Assert.True(session.CanClaim);
        Assert.True(await session.ClaimAsync());
        Assert.Equal("claim", host.Requests[^1].Npep!.Action);
        Assert.Equal("ABCD2345", host.Requests[^1].Npep!.UserCode);
        session.UserCode = "INVALID!"; Assert.False(session.CanClaim);
        host.Snapshot = State("PENDING") with { PairingSource = "SCREEN" };
        await session.RefreshAsync(); Assert.False(session.ShowCode);
        Assert.Contains("预授权", session.Title);
        host.Snapshot = State("APPROVED") with { PairingSource = "SCREEN", Approval = Approval() };
        await session.RefreshAsync(); Assert.False(session.CanConfirm);
        Assert.Equal("", session.UserCode);
    }
    private const string Origin = "http://127.0.0.1:3000";
    private const string ServerId = "bf85f73d-de6a-4f71-8457-dd897eb97b35";
    private const string Epoch = "a340c0f2-e8cb-4198-a805-1e0d783886ea";
    private const string ApprovalId = "48d7c7f0-7d27-4b51-bf86-b79257516d75";
    private static JsonObject Server() => new() { ["serverInstanceId"] = ServerId, ["deploymentEpoch"] = Epoch };
    private static JsonObject Approval(string school = "测试学校") => new()
    {
        ["approvalId"] = ApprovalId, ["schoolName"] = school,
        ["administrativeClassName"] = "高二1班", ["screenBindingName"] = "教室大屏"
    };
    private static NpepState State(string state = "UNPAIRED", long revision = 1) => new(revision, state, "STOPPED", "测试状态", Origin: Origin);
    private static HostResponse Response(HostRequest request, NpepState? state, string outcome = "Succeeded") =>
        new(Protocol.Version, request.RequestId, outcome, null, "测试回执", Npep: state);
    private sealed class Host
    {
        public NpepState Snapshot = State();
        public NpepState? AcceptedSnapshot;
        public List<HostRequest> Requests { get; } = [];
        public Task<HostResponse> Send(HostRequest request, CancellationToken token)
        {
            Requests.Add(request);
            if (request.Npep is { } command)
            {
                Assert.True(NpepContract.Valid(command));
                Assert.Equal(Snapshot.Revision, command.Revision);
            }
            return Task.FromResult(Response(request, request.Npep is null ? Snapshot : AcceptedSnapshot ?? Snapshot,
                request.Npep is null ? "Succeeded" : "Accepted"));
        }
    }

    [Fact]
    public async Task ReadingAndSharingSessionNeverCreatesOrConfirmsPairing()
    {
        var host = new Host(); var session = new NpepConnectionSession(host.Send);
        Assert.False(session.CanFinish); Assert.False(session.CanPair);
        await session.RefreshAsync();
        session.Origin = Origin; session.DeviceName = "测试电脑";
        Assert.True(session.CanInspect);
        await session.RefreshAsync();
        Assert.All(host.Requests, r => { Assert.Equal("npep.status", r.Capability); Assert.Null(r.Npep); });
        var settings = session; var onboarding = session;
        settings.DeviceName = "高二1班大屏";
        Assert.Equal("高二1班大屏", onboarding.DeviceName);
        Assert.False(await onboarding.PairAsync());
        Assert.False(await onboarding.ConfirmAsync());
        Assert.Equal(2, host.Requests.Count);
    }

    [Fact]
    public async Task PairingUsesInspectedIdentityExplicitConsentAndCurrentRevision()
    {
        var host = new Host(); var session = new NpepConnectionSession(host.Send);
        await session.RefreshAsync();
        Assert.True(await session.InspectAsync());
        Assert.Equal("inspect", host.Requests[^1].Npep!.Action);
        host.Snapshot = State(revision: 3) with { Server = Server() };
        await session.RefreshAsync();
        Assert.False(session.CanPair);
        session.ServerConfirmed = true;
        Assert.True(session.CanPair);
        Assert.True(await session.PairAsync());
        Assert.Equal(new NpepCommand("pair", 3, Origin, "教室大屏", ServerId, Epoch), host.Requests[^1].Npep);
        session.Origin = "http://127.0.0.1:3031";
        Assert.False(session.ServerConfirmed); Assert.False(session.CanPair);
    }

    [Fact]
    public async Task ChangedServerRequiresFreshConsentButOrdinaryRefreshDoesNot()
    {
        var host = new Host { Snapshot = State() with { Server = Server() } };
        var session = new NpepConnectionSession(host.Send); await session.RefreshAsync();
        session.ServerConfirmed = true;
        host.Snapshot = host.Snapshot with { Revision = 2, Server = Server() };
        await session.RefreshAsync(); Assert.True(session.CanPair);
        host.Snapshot.Server!["deploymentEpoch"] = Guid.NewGuid().ToString();
        await session.RefreshAsync(); Assert.False(session.ServerConfirmed); Assert.False(session.CanPair);
    }

    [Fact]
    public async Task PendingRequestResumesWithoutCreatingAnotherCode()
    {
        var host = new Host { Snapshot = State("PENDING") with { Pairing = new() { ["userCode"] = "TEST-1234", ["expiresAt"] = "2026-10-02T01:00:00Z" } } };
        var session = new NpepConnectionSession(host.Send); await session.RefreshAsync();
        Assert.True(session.ShowCode); Assert.False(session.ShowCreate); Assert.False(session.CanFinish);
        Assert.Equal("TEST-1234", session.PairingCode);
        Assert.False(await session.PairAsync());
        Assert.True(await session.RecoverAsync());
        Assert.Equal("poll", host.Requests[^1].Npep!.Action);
        Assert.DoesNotContain(host.Requests, r => r.Npep?.Action == "pair");
    }

    [Fact]
    public async Task ApprovalMustBeConfirmedLocallyAndChangedAssignmentClearsConsent()
    {
        var host = new Host { Snapshot = State("APPROVED") with { Approval = Approval() } };
        var session = new NpepConnectionSession(host.Send); await session.RefreshAsync();
        Assert.Contains("测试学校", session.Binding); Assert.False(session.ShowCode); Assert.False(await session.ConfirmAsync());
        session.BindingConfirmed = true; Assert.True(session.CanConfirm);
        host.Snapshot = host.Snapshot with { Revision = 2, Approval = Approval("另一学校") };
        await session.RefreshAsync(); Assert.False(session.BindingConfirmed);
        Assert.False(await session.ConfirmAsync());
        session.BindingConfirmed = true; Assert.True(await session.ConfirmAsync());
        Assert.Equal(new NpepCommand("confirm", 2, ApprovalId: ApprovalId), host.Requests[^1].Npep);
    }

    [Theory]
    [InlineData("ONLINE", false, "当前在线")]
    [InlineData("OFFLINE", false, "自动重试")]
    [InlineData("STOPPED", false, "连接已停止")]
    [InlineData("ONLINE", true, "互联已暂停")]
    public async Task ExistingPairingCanFinishWithoutNetworkOrReRegistration(string connection, bool paused, string title)
    {
        var host = new Host { Snapshot = State("ACTIVE") with { Connection = connection, ReportingPaused = paused, Approval = Approval() } };
        var session = new NpepConnectionSession(host.Send); await session.RefreshAsync();
        Assert.True(session.CanFinish); Assert.False(session.ShowCreate); Assert.False(session.ShowConfirm);
        Assert.Contains(title, session.Title); Assert.False(await session.PairAsync());
        Assert.Single(host.Requests);
    }

    [Fact]
    public async Task OfflineAndUnknownConnectionHaveDistinctRecoveryHints()
    {
        var host = new Host { Snapshot = State("ACTIVE") with { Connection = "OFFLINE", Error = "NETWORK_UNAVAILABLE" } };
        var session = new NpepConnectionSession(host.Send); await session.RefreshAsync();
        Assert.Contains("自动重试", session.Title);
        host.Snapshot = host.Snapshot with { Connection = "NEW_CONNECTION", Error = null };
        await session.RefreshAsync();
        Assert.Contains("待核实", session.Title);
        Assert.DoesNotContain(host.Requests, x => x.Npep is not null);
    }

    [Theory]
    [InlineData("CREATING", "resume-create")]
    [InlineData("CONFIRMING", "recover")]
    [InlineData("ACTIVE", "resume")]
    public async Task UnfinishedOperationUsesRecoveryInsteadOfNewRegistration(string state, string command)
    {
        var host = new Host { Snapshot = State(state) }; var session = new NpepConnectionSession(host.Send);
        await session.RefreshAsync(); Assert.True(await session.RecoverAsync());
        Assert.Equal(command, host.Requests[^1].Npep!.Action);
    }

    [Fact]
    public async Task ExpiredOrRevokedPairingCannotBeTreatedAsComplete()
    {
        var host = new Host { Snapshot = State("PENDING") with { Error = "PAIRING_EXPIRED" } };
        var session = new NpepConnectionSession(host.Send); await session.RefreshAsync();
        Assert.Contains("已过期", session.Title); Assert.False(session.CanFinish);
        host.Snapshot = State("SUSPENDED"); await session.RefreshAsync();
        Assert.False(session.CanFinish); Assert.False(session.CanConfirm); Assert.True(session.CanUnpair);
    }

    [Fact]
    public async Task CurrentFeedbackAndStatusErrorsClearOnSuccessfulRefreshWithoutSendingCommands()
    {
        var host = new Host { Snapshot = State("ACTIVE") with { Connection = "OFFLINE", Error = "TLS_VALIDATION_FAILED" } };
        bool disconnected = false;
        var session = new NpepConnectionSession((r, t) => disconnected ? throw new IOException("fixture disconnect") : host.Send(r, t));
        await session.RefreshAsync();
        Assert.Contains("证书", session.Error);
        Assert.DoesNotContain("TLS_VALIDATION_FAILED", session.Error);
        Assert.Contains("TLS_VALIDATION_FAILED", session.TechnicalDetails);
        Assert.Contains("自动重试", session.NextAction);
        disconnected = true; await session.RefreshAsync();
        Assert.Contains("未确认", session.Title); Assert.False(session.CanPause);
        Assert.DoesNotContain("TLS_VALIDATION_FAILED", session.TechnicalDetails);
        disconnected = false; host.Snapshot = host.Snapshot with { Connection = "ONLINE", Error = null };
        await session.RefreshAsync();
        Assert.Contains("当前在线", session.Title); Assert.Equal("", session.Error);
        Assert.DoesNotContain("HOST_UNAVAILABLE", session.TechnicalDetails);
        Assert.DoesNotContain(host.Requests, r => r.Npep is not null);
    }

    [Fact]
    public async Task HostBusyBlocksMutationAndAcceptedCommandPreventsDoubleClick()
    {
        var host = new Host { Snapshot = State() with { Server = Server() } };
        var session = new NpepConnectionSession(host.Send); await session.RefreshAsync(); session.ServerConfirmed = true;
        host.AcceptedSnapshot = host.Snapshot with { Revision = 2, Busy = true };
        Assert.True(await session.PairAsync());
        Assert.False(session.CanPair); Assert.False(await session.PairAsync());
        Assert.Equal(1, host.Requests.Count(r => r.Npep?.Action == "pair"));
        host.Snapshot = host.AcceptedSnapshot;
        await session.RefreshAsync(); Assert.False(session.CanEdit); Assert.False(session.CanInspect);
    }

    [Fact]
    public async Task ConcurrentViewsShareRequestGateAndPollingDoesNotDisableTyping()
    {
        var host = new Host(); var completion = new TaskCompletionSource<HostResponse>();
        bool waiting = false; HostRequest? pending = null;
        var session = new NpepConnectionSession((r, t) => { if (!waiting) return host.Send(r, t); pending = r; return completion.Task; });
        await session.RefreshAsync(); waiting = true;
        var read = session.RefreshAsync(); Assert.True(session.IsWorking); Assert.True(session.CanEdit);
        session.Origin = ""; Assert.False(await session.RefreshAsync()); Assert.False(await session.InspectAsync());
        completion.SetResult(Response(pending!, host.Snapshot)); await read;
        Assert.Equal("", session.Origin); Assert.True(session.CanRefresh);
    }

    [Fact]
    public async Task LostMutationResponseDisablesMutationsUntilFreshStatusAndConsent()
    {
        var host = new Host { Snapshot = State() with { Server = Server() } }; bool failing = false;
        var session = new NpepConnectionSession((r, t) => failing ? throw new IOException("lost") : host.Send(r, t));
        await session.RefreshAsync(); session.ServerConfirmed = true; failing = true;
        Assert.False(await session.PairAsync()); Assert.False(session.CanPair); Assert.False(session.CanFinish);
        Assert.Contains("可能已受理", session.Message); Assert.False(await session.PairAsync());
        failing = false; await session.RefreshAsync();
        Assert.False(session.ServerConfirmed); Assert.False(session.CanPair);
    }

    [Fact]
    public async Task UnpairRequiresSameSnapshotThatUserConfirmed()
    {
        var host = new Host { Snapshot = State("ACTIVE") }; var session = new NpepConnectionSession(host.Send);
        await session.RefreshAsync(); var revision = session.Revision!.Value;
        host.Snapshot = host.Snapshot with { Revision = 2 }; await session.RefreshAsync();
        Assert.False(await session.UnpairAsync(revision)); Assert.Contains("已变化", session.Message);
        Assert.True(await session.UnpairAsync(2)); Assert.Equal("unpair", host.Requests[^1].Npep!.Action);
    }

    [Theory]
    [InlineData(false, "pause")]
    [InlineData(true, "resume")]
    public async Task PauseAndResumeUseExistingBinding(bool paused, string action)
    {
        var host = new Host { Snapshot = State("ACTIVE") with { ReportingPaused = paused } };
        var session = new NpepConnectionSession(host.Send); await session.RefreshAsync();
        Assert.True(await session.PauseOrResumeAsync()); Assert.Equal(action, host.Requests[^1].Npep!.Action);
    }

    [Fact]
    public async Task ClosedApplicationDoesNotSendAnyRequests()
    {
        using var lifetime = new CancellationTokenSource(); lifetime.Cancel();
        var host = new Host(); var session = new NpepConnectionSession(host.Send, lifetime.Token);
        Assert.False(await session.RefreshAsync()); Assert.Empty(host.Requests);
    }

    [Fact]
    public async Task ProviderAddressCanBeEnteredBeforeHostConnectsButCannotSendCommands()
    {
        var host = new Host(); var session = new NpepConnectionSession(host.Send);
        Assert.True(session.ShowAddressEditor); Assert.True(session.CanEdit);
        session.Origin = "http://127.0.0.1:3031";
        Assert.False(session.CanInspect); Assert.False(await session.PairAsync()); Assert.Empty(host.Requests);
        await session.RefreshAsync();
        Assert.Equal("http://127.0.0.1:3031", session.Origin); Assert.True(session.CanInspect);
    }

    [Theory]
    [InlineData("PENDING")]
    [InlineData("APPROVED")]
    [InlineData("ACTIVE")]
    [InlineData("SUSPENDED")]
    public async Task ExistingProviderIsVisibleAndOnlyChangesAfterExplicitUnpair(string stage)
    {
        var host = new Host { Snapshot = State(stage) };
        var session = new NpepConnectionSession(host.Send); await session.RefreshAsync();
        Assert.True(session.ShowCurrentAddress); Assert.False(session.ShowAddressEditor); Assert.False(session.CanEdit);
        Assert.Equal(Origin, session.BoundOrigin); Assert.True(session.ShowChangeProvider);
        Assert.Contains("重新配对", session.ProviderHint);
        session.Origin = "https://other.example";
        Assert.False(await session.InspectAsync()); Assert.False(await session.PairAsync());
        Assert.Equal(Origin, session.BoundOrigin); Assert.Single(host.Requests);
        host.AcceptedSnapshot = State(revision: 2) with { Origin = null };
        Assert.True(await session.UnpairAsync(1));
        Assert.Equal("unpair", host.Requests[^1].Npep!.Action);
        Assert.True(session.ShowAddressEditor); Assert.False(session.ShowChangeProvider);
        Assert.Equal("https://other.example", session.Origin);
    }
}

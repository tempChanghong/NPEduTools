using System.Text.Json.Nodes;
using NPEduTools.App;
using NPEduTools.Contracts;

namespace NPEduTools.Tests;

public sealed class NpepConnectionSessionTests
{
    [Theory]
    [InlineData(null, "查找完成")]
    [InlineData("NETWORK_UNAVAILABLE", "班级查找失败，请重试或重新检查服务。")]
    [InlineData("RATE_LIMIT", "查找过于频繁，请稍后重试。")]
    public async Task AcceptedLookupWaitsForItsOwnHostCompletion(string? error, string message)
    {
        var state = State() with { Server = Server(), Catalogue = new JsonObject { ["supportsLookup"] = true, ["items"] = new JsonArray() } };
        var session = new NpepConnectionSession((request, _) =>
        {
            if (request.Npep is not { } command) return Task.FromResult(Response(request, state));
            var pending = LookupResult(command.SchoolQuery!, command.ClassQuery!, null);
            pending["items"] = new JsonArray();
            state = state with { Busy = true, OperationId = request.RequestId, Catalogue = pending };
            return Task.FromResult(Response(request, state, "Accepted"));
        });
        await session.RefreshAsync(); session.ServerConfirmed = true;
        session.SchoolQuery = "SCHOOL"; session.ClassQuery = "三班";
        Assert.True(await session.FindClassAsync());
        Assert.Empty(session.Classes);
        Assert.Contains("正在查找", session.ClassCatalogueHint);
        Assert.False(session.ShowRetryClassPage);
        await session.RefreshAsync(); // The Host is still busy.
        Assert.Contains("正在查找", session.ClassCatalogueHint);
        var completed = error is null ? LookupResult("SCHOOL", "三班", null) : (JsonObject)state.Catalogue!.DeepClone();
        state = state with { Busy = false, Catalogue = completed, Error = error, Message = message };
        await session.RefreshAsync();
        if (error is null) Assert.Equal("result-screen", Assert.Single(session.Classes).Id);
        else
        {
            Assert.Empty(session.Classes);
            Assert.Equal(message, session.ClassCatalogueHint);
            Assert.True(session.CanRetryClassPage);
        }
    }

    [Fact]
    public async Task ExactLookupRejectsTheWrongPageAndDoesNotRestoreFailedRowsFromPolling()
    {
        var state = State() with { Server = Server(), Catalogue = new JsonObject { ["supportsLookup"] = true, ["items"] = new JsonArray() } };
        string? afterReturned = "wrong-page";
        var commands = new List<NpepCommand>();
        var session = new NpepConnectionSession((request, _) =>
        {
            if (request.Npep is { } command)
            {
                commands.Add(command);
                state = state with { Catalogue = LookupResult(command.SchoolQuery!, command.ClassQuery!, afterReturned) };
            }
            return Task.FromResult(Response(request, state));
        });
        await session.RefreshAsync();
        session.SchoolQuery = "  SCHOOL  "; session.ClassQuery = " 高二（3）班 ";
        Assert.False(await session.FindClassAsync());
        session.ServerConfirmed = true;
        Assert.False(await session.FindClassAsync());
        Assert.True(session.CanRetryClassPage);
        var command = Assert.Single(commands);
        Assert.Equal("SCHOOL", command.SchoolQuery);
        Assert.Equal("高二（3）班", command.ClassQuery);
        Assert.Null(command.ManagementSecret);
        Assert.Empty(session.Classes);
        state = state with { Catalogue = LookupResult(command.SchoolQuery!, command.ClassQuery!, null) };
        await session.RefreshAsync();
        Assert.Empty(session.Classes);
        afterReturned = null;
        Assert.True(await session.RetryClassPageAsync());
        Assert.Equal("result-screen", Assert.Single(session.Classes).Id);
        Assert.Equal(command, commands.Last());
    }

    [Fact]
    public async Task ChangingServiceInvalidatesAnInFlightLookupAndCachedRows()
    {
        var state = State() with { Server = Server(), Catalogue = new JsonObject { ["supportsLookup"] = true, ["items"] = new JsonArray() } };
        var paused = new TaskCompletionSource<HostResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        HostRequest? sent = null;
        var session = new NpepConnectionSession((request, _) =>
        {
            if (request.Npep is null) return Task.FromResult(Response(request, state));
            sent = request;
            return paused.Task;
        });
        await session.RefreshAsync(); session.ServerConfirmed = true;
        session.SchoolQuery = "学校"; session.ClassQuery = "三班";
        var oldLookup = session.FindClassAsync();
        Assert.NotNull(sent);
        session.Origin = "https://other.example";
        state = state with { Catalogue = LookupResult("学校", "三班", null) };
        paused.SetResult(Response(sent!, state));
        Assert.False(await oldLookup);
        await session.RefreshAsync();
        Assert.Empty(session.Classes);
        Assert.False(session.ShowClassLookup);
        Assert.False(await session.ConnectClassAsync("result-screen", null));
        Assert.False(session.ServerConfirmed);
    }

    private static JsonObject LookupResult(string school, string classroom, string? after) => new()
    {
        ["supportsLookup"] = true, ["schoolQuery"] = school, ["classQuery"] = classroom, ["after"] = after,
        ["nextCursor"] = null, ["items"] = new JsonArray(new JsonObject { ["screenBindingId"] = "result-screen", ["className"] = "三班" })
    };

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
    private const string LastReceipt = "2026-10-14T12:00:00.000Z";
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

    [Theory]
    [InlineData("CLASS_PASSWORD", "BINDING_OCCUPIED", true)]
    [InlineData("CLASS_APPLY", "BINDING_OCCUPIED", true)]
    [InlineData("REPLACEMENT", "MANAGEMENT_SECRET_INVALID", true)]
    [InlineData("CLASS_PASSWORD", "NETWORK_UNAVAILABLE", false)]
    [InlineData("REPLACEMENT", "NETWORK_UNAVAILABLE", false)]
    [InlineData("REPLACEMENT", "REPLACEMENT_UNSUPPORTED", false)]
    [InlineData("SCREEN", "BINDING_OCCUPIED", false)]
    public async Task ReplacementUsesTheStoppedCandidateAndFreshPassword(string source, string error, bool offered)
    {
        var host = new Host { Snapshot = State("CREATING", 7) with { PairingSource = source, Error = error, Server = Server(),
            Catalogue = new() { ["replacementTarget"] = new JsonObject
            { ["screenBindingId"] = "screen-01", ["screenName"] = "一班前屏", ["deviceName"] = "新讲台机" } } } };
        var session = new NpepConnectionSession(host.Send) { Origin = "https://draft.example", DeviceName = "另一设备" };
        await session.RefreshAsync();
        Assert.Equal(offered, session.CanRequestReplacement);
        Assert.False(await session.RequestReplacementAsync("short"));
        Assert.Equal(offered, await session.RequestReplacementAsync("fixture-class-password"));
        var mutations = host.Requests.Where(x => x.Npep is not null).ToArray();
        if (!offered) { Assert.Empty(mutations); return; }
        Assert.Equal(new NpepCommand("replacement-class", 7, Origin, "新讲台机", ServerId, Epoch,
            ScreenBindingId: "screen-01", ManagementSecret: "fixture-class-password"), Assert.Single(mutations).Npep);
        host.Snapshot = host.Snapshot with { Catalogue = null };
        await session.RefreshAsync();
        Assert.False(session.ShowReplacementRequest);
        Assert.False(await session.RequestReplacementAsync("fixture-class-password"));
    }

    [Theory]
    [InlineData("PAIRING_CANCELLED", "已取消")]
    [InlineData("PAIRING_EXPIRED", "已过期")]
    public async Task ClosedReplacementShowsCleanupWithoutReplayingOrDisplayingPairingCode(string error, string title)
    {
        var host = new Host { Snapshot = State("PENDING", 8) with { PairingSource = "REPLACEMENT", Error = error,
            Pairing = new JsonObject { ["userCode"] = "TEST2345", ["expiresAt"] = "2026-11-22T01:15:00Z" } } };
        var session = new NpepConnectionSession(host.Send);
        await session.RefreshAsync();
        Assert.Contains(title, session.Title);
        Assert.True(session.CanUnpair);
        Assert.False(session.CanFinish);
        Assert.False(session.ShowCode);
        Assert.False(session.ShowRecover);
        Assert.False(await session.RecoverAsync());
        Assert.All(host.Requests, request => Assert.Null(request.Npep));
    }

    [Fact]
    public async Task UnsupportedReplacementRequiresSchoolUpgradeAndAllowsOriginalRequestRecovery()
    {
        var host = new Host { Snapshot = State("CREATING", 8) with { PairingSource = "REPLACEMENT", Error = "REPLACEMENT_UNSUPPORTED" } };
        var session = new NpepConnectionSession(host.Send);
        await session.RefreshAsync();
        Assert.Contains("升级 KV", session.NextAction);
        Assert.False(session.ShowReplacementRequest);
        Assert.True(await session.RecoverAsync());
        Assert.Equal("resume-create", host.Requests.Last().Npep?.Action);
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
        var host = new Host { Snapshot = State("ACTIVE") with
            {
                Connection = connection, ReportingPaused = paused, Approval = Approval(), LastReceivedAt = LastReceipt,
                Error = connection == "OFFLINE" ? "NETWORK_UNAVAILABLE" : null
            } };
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
        disconnected = false; host.Snapshot = host.Snapshot with { Connection = "ONLINE", Error = null, LastReceivedAt = LastReceipt };
        await session.RefreshAsync();
        Assert.Contains("当前在线", session.Title); Assert.Equal("", session.Error);
        Assert.DoesNotContain("HOST_UNAVAILABLE", session.TechnicalDetails);
        Assert.DoesNotContain(host.Requests, r => r.Npep is not null);
    }

    [Theory]
    [InlineData("ACTIVE", true)]
    [InlineData("SUSPENDED", true)]
    [InlineData("UNPAIRING", true)]
    [InlineData("UNPAIRED", false)]
    [InlineData("PENDING", false)]
    [InlineData("APPROVED", false)]
    public async Task Only_binding_states_use_the_summary_and_hide_routine_poll_messages(string state, bool hasSummary)
    {
        var host = new Host { Snapshot = State(state) };
        var session = new NpepConnectionSession(host.Send);
        Assert.True(await session.RefreshAsync());
        Assert.Equal(hasSummary, session.ShowBindingManagement);
        Assert.Equal(hasSummary ? "" : host.Snapshot.Message, session.Message);
        Assert.Equal(hasSummary ? "最近有效回执：暂无" : "", session.Receipt);
        Assert.Contains(host.Snapshot.Message, session.TechnicalDetails);
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData("invalid", false)]
    [InlineData("12:00", false)]
    [InlineData("2026-10-14T12:00:00", false)]
    [InlineData("2026-10-14T12:00:00Z", true)]
    [InlineData(LastReceipt, true)]
    public async Task Online_title_requires_a_valid_receipt_time(string? receipt, bool confirmed)
    {
        var host = new Host { Snapshot = State("ACTIVE") with { Connection = "ONLINE", LastReceivedAt = receipt } };
        var session = new NpepConnectionSession(host.Send);
        Assert.True(await session.RefreshAsync());
        if (confirmed)
        {
            Assert.Contains("当前在线", session.Title);
            Assert.StartsWith("最近有效回执：", session.Receipt);
            Assert.EndsWith("（本机时间）", session.Receipt);
            Assert.StartsWith("无需操作", session.NextAction);
        }
        else
        {
            Assert.Contains("等待学校回执", session.Title);
            Assert.Equal("最近有效回执：暂无", session.Receipt);
            Assert.Contains("收到新的回执后会自动更新", session.NextAction);
        }
    }

    [Fact]
    public async Task Rejected_command_message_does_not_hide_the_current_TLS_cause_or_advice()
    {
        var host = new Host { Snapshot = State("ACTIVE") with { Connection = "OFFLINE", Error = "TLS_VALIDATION_FAILED" } };
        const string feedback = "状态已变化，请刷新后再试。";
        var session = new NpepConnectionSession((request, token) => request.Npep is null ? host.Send(request, token) :
            Task.FromResult(Response(request, host.Snapshot, "Rejected") with { ErrorCode = "NpepStateChanged", Message = feedback }));
        Assert.True(await session.RefreshAsync());
        Assert.False(await session.PauseOrResumeAsync());
        Assert.Equal(feedback, session.Message);
        Assert.Contains("HTTPS 证书验证失败", session.Error);
        Assert.Contains("学校地址、本机系统时间和服务器证书", session.NextAction);
        Assert.Contains("持续自动重试", session.NextAction);
        Assert.Contains("操作错误码：NpepStateChanged", session.TechnicalDetails);
        Assert.True(await session.RefreshAsync());
        Assert.Equal("", session.Message);
        Assert.DoesNotContain("NpepStateChanged", session.TechnicalDetails);
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

    [Theory]
    [InlineData("refresh")]
    [InlineData("pair")]
    [InlineData("confirm")]
    [InlineData("unpair-authorization")]
    public async Task AccessDenialInvalidatesSharedStateAndAllowsReadOnlyRecovery(string operation)
    {
        var host = new Host { Snapshot = State(operation == "pair" ? "UNPAIRED" : operation == "confirm" ? "APPROVED" : "ACTIVE")
            with { Server = Server(), Approval = Approval(), Connection = "ONLINE" } };
        bool denied = false;
        var requests = new List<HostRequest>();
        int authorizations = 0;
        var session = new NpepConnectionSession((request, token) =>
        {
            requests.Add(request);
            if (denied) throw new UnauthorizedAccessException("isolated Host access denied");
            return host.Send(request, token);
        }, authorize: request =>
        {
            authorizations++;
            if (denied) throw new UnauthorizedAccessException("isolated management access denied");
            return Task.FromResult<HostRequest?>(request);
        });
        Assert.True(await session.RefreshAsync());
        session.ServerConfirmed = session.BindingConfirmed = true;
        session.DeviceName = "保留设备草稿"; session.UserCode = "ABCD-2345";
        string origin = session.Origin;
        Task<bool> Act() => operation switch
        {
            "pair" => session.PairAsync(), "confirm" => session.ConfirmAsync(),
            "unpair-authorization" => session.UnpairAsync(1), _ => session.RefreshAsync()
        };
        denied = true;
        Assert.False(await Act());
        Assert.Null(session.Revision);
        Assert.False(session.IsWorking); Assert.True(session.CanRefresh);
        Assert.False(session.HasPairing); Assert.False(session.CanFinish);
        Assert.False(session.CanPair); Assert.False(session.CanConfirm);
        Assert.False(session.CanPause); Assert.False(session.CanUnpair);
        Assert.False(session.ServerConfirmed); Assert.False(session.BindingConfirmed);
        Assert.Equal("", session.BoundOrigin); Assert.Equal("", session.Receipt);
        Assert.Contains(operation == "refresh" ? "重新检查" : "操作可能已受理", session.Message);
        Assert.Equal(origin, session.Origin); Assert.Equal("保留设备草稿", session.DeviceName);
        Assert.Equal("ABCD-2345", session.UserCode);
        Assert.Equal(operation == "unpair-authorization" ? 1 : 2, requests.Count);
        Assert.Equal(operation == "unpair-authorization" ? 1 : 0, authorizations);

        int beforeRecovery = requests.Count;
        denied = false;
        host.Snapshot = host.Snapshot with { Revision = 2 };
        Assert.True(await session.RefreshAsync());
        Assert.Equal(2, session.Revision); Assert.False(session.IsWorking);
        Assert.Equal(beforeRecovery + 1, requests.Count);
        Assert.Equal("npep.status", requests[^1].Capability); Assert.Null(requests[^1].Npep);
        Assert.Equal(operation == "unpair-authorization" ? 1 : 0, authorizations);
        Assert.False(session.ServerConfirmed); Assert.False(session.BindingConfirmed);
        if (operation == "pair") Assert.False(session.CanPair);
        else if (operation == "confirm") Assert.False(session.CanConfirm);
        else Assert.True(session.CanFinish);
        Assert.DoesNotContain("后台暂不可用", session.Message);
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

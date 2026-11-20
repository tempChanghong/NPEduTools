using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using NPEduTools.Contracts;
using NPEduTools.Integrations.Npep;

namespace NPEduTools.Npep.Tests;

public sealed class ManagedUpdateSessionTests
{
    private sealed class UpdateHost : ISchoolUpdateHost, IPreparedSchoolUpdate
    {
        public string RunningVersion => "1.1.10.0";
        internal string? Busy;
        internal int Prepared, Applied;
        internal Func<CancellationToken, Task<IPreparedSchoolUpdate>>? OnPrepare;
        public string? Deferral() => Busy;
        internal ManagedUpdateReceipt? LocalResult;
        public ManagedUpdateReceipt? Result(Guid id) => LocalResult;
        public Task<IPreparedSchoolUpdate> PrepareAsync(ManagedUpdatePlan plan, CancellationToken token)
        { Prepared++; return OnPrepare?.Invoke(token) ?? Task.FromResult<IPreparedSchoolUpdate>(this); }
        public async Task<bool> HandoffAsync(IPreparedSchoolUpdate worker, Func<Task<bool>> authorize, CancellationToken token)
        {
            if (!await authorize()) return false;
            await worker.ApplyAsync(token);
            return true;
        }
        public Task ApplyAsync(CancellationToken token) { Applied++; return Task.CompletedTask; }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
    [Fact]
    public void RecoveryFieldsAreOnlySentOnTheOptInStatusReply()
    {
        var status = new SoftwareUpdateStatus("MANAGED", Guid.NewGuid(), true, "1.1.10.0",
            State: "FAILED", Reason: "ROLLED_BACK", PreviousVersion: "1.1.10.0", RollbackConfirmed: true);
        foreach (var capability in new[] { "software.update.status", "software.update.consent" })
        {
            var legacy = JsonSerializer.SerializeToElement(status.ForStatusReply(capability), Protocol.Json);
            Assert.False(legacy.TryGetProperty("previousVersion", out _));
            Assert.False(legacy.TryGetProperty("rollbackConfirmed", out _));
            Assert.Equal("ROLLED_BACK", legacy.GetProperty("reason").GetString());
        }
        const string recovery = "software.update.status.recovery";
        var read = new HostRequest(Protocol.Version, Guid.NewGuid(), recovery);
        Assert.Null(Protocol.Validate(read));
        Assert.NotNull(Protocol.Validate(read with { SoftwareUpdateConsent = new(Guid.NewGuid(), true) }));
        var extended = JsonSerializer.SerializeToElement(status.ForStatusReply(recovery), Protocol.Json);
        Assert.True(extended.GetProperty("rollbackConfirmed").GetBoolean());
        Assert.Equal("1.1.10.0", extended.GetProperty("previousVersion").GetString());
    }

    [Fact]
    public async Task LocalRecoveryRelaysOnlyThisFailedPlansRollbackAndNeverStartsAnotherInstall()
    {
        using var f = new Scenario();
        await f.Consent();
        f.Phase = "FAILED";
        var reason = "APP_START_FAILED";
        string? confirmedAt = null;
        bool allow = false;
        async Task<JsonObject> Exchange(Guid? consent, ManagedUpdateReceipt? receipt)
        {
            var reply = await f.Exchange(consent, receipt);
            var plan = reply["plan"]!.AsObject();
            plan["reason"] = reason;
            if (allow) plan["canReportRollback"] = true;
            plan["rollbackConfirmedAt"] = confirmedAt;
            return reply;
        }
        Task Cycle() => f.Session.CycleAsync("current-scope", Exchange, default);
        f.Host.LocalResult = new(f.PlanId, "FAILED", "ROLLED_BACK");
        await Cycle();
        Assert.Empty(f.Receipts);
        Assert.False(f.Session.Status.RollbackConfirmed);
        Assert.Equal("1.1.10.0", f.Session.Status.PreviousVersion);
        allow = true;
        f.Host.LocalResult = new(Guid.NewGuid(), "FAILED", "ROLLED_BACK");
        await Cycle();
        Assert.Empty(f.Receipts);
        f.Host.LocalResult = new(f.PlanId, "FAILED", "ROLLED_BACK");
        await Cycle();
        Assert.Equal(f.Host.LocalResult, Assert.Single(f.Receipts));
        reason = "ROLLED_BACK";
        allow = false;
        await Cycle();
        Assert.False(f.Session.Status.RollbackConfirmed);
        confirmedAt = "2026-11-20T09:00:00.000Z";
        await Cycle();
        Assert.True(f.Session.Status.RollbackConfirmed);
        Assert.Single(f.Receipts);
        Assert.Equal(0, f.Host.Prepared);
        Assert.Equal(0, f.Host.Applied);
    }
    private sealed class Scenario : IDisposable
    {
        internal readonly UpdateHost Host = new();
        internal readonly string DirectoryPath = Directory.CreateTempSubdirectory("np-maintenance-").FullName;
        internal readonly Guid Invitation = Guid.NewGuid(), PlanId = Guid.NewGuid();
        internal readonly List<ManagedUpdateReceipt> Receipts = [];
        internal ManagedUpdateSession Session;
        internal string? Phase;
        internal bool LoseGrantReply;
        internal Scenario()
        {
            Session = new(Host, DirectoryPath);
            Session.Bind("current-scope", "school", "class", "screen");
        }
        internal Task<JsonObject> Exchange(Guid? approval, ManagedUpdateReceipt? receipt)
        {
            if (receipt is not null)
            {
                Receipts.Add(receipt);
                Phase = receipt.State;
                if (LoseGrantReply && receipt.State == "INSTALLING") throw new HttpRequestException("synthetic transport loss");
            }
            var response = new JsonObject { ["enrollment"] = approval == Invitation ? "MANAGED" : "INVITED",
                ["enrollmentId"] = Invitation.ToString("D"), ["managed"] = approval == Invitation, ["revision"] = 1 };
            ManagedRelease Release(string version) => new(version, $"https://school.example/{version}/NPEduTools.zip", new string('a', 64), 1234);
            if (Phase is not null) response["plan"] = JsonSerializer.SerializeToNode(new ManagedUpdatePlan(PlanId,
                "1.1.11.0", Phase, Release("1.1.11.0"), Release("1.1.10.0")), Protocol.Json);
            return Task.FromResult(response);
        }
        internal Task Cycle() => Session.CycleAsync("current-scope", Exchange, default);
        internal async Task Consent()
        { await Cycle(); Assert.False(Session.Accept(Guid.NewGuid())); Assert.True(Session.Accept(Invitation)); Phase = "APPROVED"; }
        public void Dispose() => Directory.Delete(DirectoryPath, true);
    }
    [Fact]
    public async Task OnlyTheCurrentLocalInvitationCanPrepareAndAfterSchoolInstallWaitsForIdle()
    {
        using var f = new Scenario();
        f.Phase = "APPROVED"; await f.Cycle();
        Assert.Equal(0, f.Host.Prepared);
        await f.Consent();
        f.Host.Busy = "RECORDING_ACTIVE";
        await f.Cycle();
        Assert.Equal(new(f.PlanId, "DEFERRED", "RECORDING_ACTIVE"), Assert.Single(f.Receipts));
        Assert.Equal(0, f.Host.Prepared);
        f.Host.Busy = null;
        await f.Cycle();
        Assert.Equal("INSTALLING", f.Phase); Assert.Equal(1, f.Host.Prepared); Assert.Equal(1, f.Host.Applied);
        await f.Cycle();
        Assert.Equal(1, f.Host.Applied);
    }
    [Fact]
    public async Task LostGrantReplyNeverRunsWorkerAndRecreatedHostResolvesUnsentHandoff()
    {
        using var f = new Scenario();
        await f.Consent(); f.LoseGrantReply = true;
        await Assert.ThrowsAsync<HttpRequestException>(f.Cycle);
        Assert.Equal("INSTALLING", f.Phase); Assert.Equal(0, f.Host.Applied);
        f.Session = new(f.Host, f.DirectoryPath); f.Session.Bind("current-scope", "school", "class", "device");
        f.LoseGrantReply = false;
        await f.Cycle();
        Assert.Equal(new(f.PlanId, "FAILED", "HANDOFF_NOT_STARTED"), f.Receipts.Last());
        Assert.Equal(0, f.Host.Applied);
    }
    [Fact]
    public async Task ACancelledSchoolPlanCancelsTheInProgressPreparationWithoutSendingApply()
    {
        using var f = new Scenario(); await f.Consent();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        bool preparationCancelled = false;
        f.Host.OnPrepare = async token =>
        {
            started.SetResult();
            try { await Task.Delay(Timeout.InfiniteTimeSpan, token); }
            finally { preparationCancelled = token.IsCancellationRequested; }
            return f.Host;
        };
        var cycle = f.Cycle();
        await started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        f.Phase = "CANCELLED";
        await cycle.WaitAsync(TimeSpan.FromSeconds(15));
        Assert.True(preparationCancelled);
        Assert.Empty(f.Receipts);
        Assert.Equal(0, f.Host.Applied);
    }
    [Fact]
    public async Task ChangedInvitationClearsOnlyTheExpiredConsentAndRequiresANewLocalClick()
    {
        using var f = new Scenario(); await f.Consent();
        var newInvitation = Guid.NewGuid(); int attempts = 0;
        await f.Session.CycleAsync("current-scope", (approval, _) =>
        {
            attempts++;
            if (approval.HasValue) throw new NpepException("UPDATE_ENROLLMENT_CHANGED", 409);
            return Task.FromResult(new JsonObject { ["enrollment"] = "INVITED", ["managed"] = false,
                ["enrollmentId"] = newInvitation.ToString("D"), ["plan"] = null });
        }, default);
        Assert.Equal(2, attempts); Assert.False(f.Session.Status.LocalConsent);
        Assert.False(f.Session.Accept(f.Invitation)); Assert.True(f.Session.Accept(newInvitation));
    }

    [Fact]
    public async Task AnOpenConsentPageCannotCarryApprovalAcrossWithdrawalAndReinvitation()
    {
        using var f = new Scenario();
        await f.Cycle();
        var oldPageId = f.Session.Status.EnrollmentId!.Value;
        var withdrawal = new TaskCompletionSource<JsonObject>(TaskCreationOptions.RunContinuationsAsynchronously);
        var poll = f.Session.CycleAsync("current-scope", (consent, receipt) =>
        {
            Assert.Null(consent);
            Assert.Null(receipt);
            return withdrawal.Task;
        }, default);

        // The school has withdrawn the invitation, but the open page still shows its previous reply.
        f.Session.Accept(oldPageId);
        withdrawal.SetResult(new JsonObject { ["enrollment"] = "UNMANAGED", ["enrollmentId"] = null, ["managed"] = false });
        await poll;
        Assert.Equal("UNMANAGED", f.Session.Status.Enrollment);
        Assert.Null(f.Session.Status.EnrollmentId);
        Assert.False(f.Session.Status.LocalConsent);
        Assert.False(f.Session.Accept(oldPageId));

        var newPageId = Guid.NewGuid();
        f.Session = new(f.Host, f.DirectoryPath);
        f.Session.Bind("current-scope", "school", "class", "screen");
        await f.Session.CycleAsync("current-scope", (consent, receipt) =>
        {
            Assert.Null(consent);
            Assert.Null(receipt);
            return Task.FromResult(new JsonObject { ["enrollment"] = "INVITED",
                ["enrollmentId"] = newPageId.ToString("D"), ["managed"] = false });
        }, default);
        Assert.Equal("INVITED", f.Session.Status.Enrollment);
        Assert.Equal(newPageId, f.Session.Status.EnrollmentId);
        Assert.False(f.Session.Accept(oldPageId));
        Assert.False(f.Session.Status.LocalConsent);
        Assert.Equal(0, f.Host.Prepared);
        Assert.True(f.Session.Accept(newPageId));
    }

    [Fact]
    public async Task ConsentGivenDuringAPollIsKeptWhenTheInvitationIsStillCurrent()
    {
        using var f = new Scenario();
        await f.Cycle();
        await f.Session.CycleAsync("current-scope", (consent, receipt) =>
        {
            Assert.Null(consent);
            Assert.True(f.Session.Accept(f.Invitation));
            return f.Exchange(consent, receipt);
        }, default);

        Assert.Equal("INVITED", f.Session.Status.Enrollment);
        Assert.True(f.Session.Status.LocalConsent);
        await f.Cycle();
        Assert.Equal("MANAGED", f.Session.Status.Enrollment);
    }

    [Fact]
    public async Task DisconnectRejectsAnOpenInvitationAndReturningAfterRebindingNeedsNewConsent()
    {
        using var f = new Scenario();
        await f.Cycle(); f.Session.Disconnect("SOFTWARE_UPDATE_UNSUPPORTED");
        Assert.Equal("SOFTWARE_UPDATE_UNSUPPORTED", f.Session.Status.Reason);
        Assert.False(f.Session.Accept(f.Invitation));
        await f.Consent();
        f.Session.Bind("other-address-and-binding", "other school", "class", "screen");
        f.Session.Bind("current-scope", "school", "class", "screen");
        await f.Cycle();
        Assert.False(f.Session.Status.LocalConsent);
        Assert.Equal(0, f.Host.Prepared);
    }
    private sealed class OldServer : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
            Assert.Equal("/api/v2/npep/device/software-update/exchange", request.RequestUri!.AbsolutePath);
            Assert.Equal("0.18", Assert.Single(request.Headers.GetValues("X-NPEP-Version")));
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }
    [Fact]
    public async Task OldSchoolServerIsReportedAsUnsupportedWithoutDowngradingTheEndpoint()
    {
        using var api = new NpepApi("https://school.example", new OldServer());
        var e = await Assert.ThrowsAsync<NpepException>(() => api.SoftwareUpdateAsync("test-device", NpepProtocol.Body(), default));
        Assert.Equal("SOFTWARE_UPDATE_UNSUPPORTED", e.Code);
    }
}

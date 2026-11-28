using System.Net;
using System.Text.Json.Nodes;
using NPEduTools.Integrations.Npep;

namespace NPEduTools.Npep.Tests;

public sealed partial class RuntimeTests
{
    private sealed class SchoolUpdateProbe : ISchoolUpdateHost
    {
        public string RunningVersion => "1.1.10.0";
        public string? Deferral() => "STUDY_ACTIVE";
        public ManagedUpdateReceipt? Result(Guid id) => null;
        public Task<IPreparedSchoolUpdate> PrepareAsync(ManagedUpdatePlan p, CancellationToken t) => throw new InvalidOperationException();
        public Task<bool> HandoffAsync(IPreparedSchoolUpdate w, Func<Task<bool>> a, CancellationToken t) => throw new InvalidOperationException();
    }

    [Fact]
    public async Task PairedRuntimeExchangesActualHostVersionAndOnlyTheLocallyAcceptedInvitation()
    {
        using var directory = new TestDirectory();
        var school = new FakeServer();
        var invitation = Guid.NewGuid();
        var accepted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var running = new SchoolUpdateProbe();
        async Task<HttpResponseMessage> Send(HttpRequestMessage request)
        {
            if (!request.RequestUri!.AbsolutePath.EndsWith("/device/software-update/exchange", StringComparison.Ordinal))
                return await school.Send(request);
            Assert.True(school.Active);
            Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
            Assert.Equal("0.18", Assert.Single(request.Headers.GetValues("X-NPEP-Version")));
            var input = JsonNode.Parse(await request.Content!.ReadAsStringAsync())!.AsObject();
            Assert.Equal(running.RunningVersion, input.Text("runningVersion"));
            Assert.Equal(school.Registration().Text("deviceId"), input["context"]!["identity"]!["deviceId"]!.GetValue<string>());
            Assert.False(input["context"]!.AsObject().ContainsKey("controlEpoch"));
            Assert.Null(input["receipt"]);
            bool consent = input["localConsent"]!.GetValue<bool>();
            Assert.Equal(consent ? invitation.ToString("D") : null, input["consentEnrollmentId"]?.GetValue<string>());
            var data = new JsonObject { ["enrollment"] = consent ? "MANAGED" : "INVITED", ["managed"] = consent,
                ["revision"] = 1, ["enrollmentId"] = invitation.ToString("D"), ["plan"] = null };
            var response = Fixtures.Reply(request, data);
            var envelope = JsonNode.Parse(await response.Content.ReadAsStringAsync())!.AsObject();
            envelope["protocolVersion"] = "0.18";
            response.Content = new StringContent(envelope.ToJsonString());
            if (consent) accepted.TrySetResult();
            return response;
        }
        await using var runtime = new NpepRuntime(() => new(directory.Path, origin => new(origin, new Handler(Send))),
            "v1.1.10 · EVA-02", Sample, updates: running);
        Assert.False(runtime.AcceptUpdateInvitation(invitation));
        await Command(runtime, "inspect"); await Command(runtime, "pair", school);
        await Command(runtime, "poll"); await Command(runtime, "confirm", school);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        while (runtime.UpdateStatus()?.Enrollment != "INVITED") await Task.Delay(30, timeout.Token);
        Assert.True(runtime.AcceptUpdateInvitation(invitation));
        await accepted.Task.WaitAsync(timeout.Token);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task OnlyDenialFromTheCurrentConnectionClearsTheMaintenanceReceipt(bool reconnect)
    {
        using var directory = new TestDirectory();
        var school = new FakeServer();
        var invitation = Guid.NewGuid();
        var plan = Guid.NewGuid();
        var oldReadStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseOldRead = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int managedReads = 0;
        async Task<HttpResponseMessage> Send(HttpRequestMessage request)
        {
            if (!request.RequestUri!.AbsolutePath.EndsWith("/device/software-update/exchange", StringComparison.Ordinal))
                return await school.Send(request);
            var input = NpepProtocol.Parse(await request.Content!.ReadAsByteArrayAsync());
            Assert.Null(input["receipt"]);
            bool managed = input["localConsent"]!.GetValue<bool>();
            bool late = managed && Interlocked.Increment(ref managedReads) == 2;
            if (late)
            {
                oldReadStarted.TrySetResult();
                await releaseOldRead.Task;
            }
            var data = new JsonObject
            {
                ["enrollment"] = managed ? "MANAGED" : "INVITED", ["enrollmentId"] = invitation.ToString("D"),
                ["managed"] = managed, ["revision"] = 2
            };
            ManagedRelease Release(string version) => new(version, $"https://school.example/{version}.zip", new string('a', 64), 123);
            if (managed)
                data["plan"] = System.Text.Json.JsonSerializer.SerializeToNode(new ManagedUpdatePlan(plan, "1.1.11.0", "FAILED",
                    Release("1.1.11.0"), Release("1.1.10.0")), NPEduTools.Contracts.Protocol.Json);
            var response = Fixtures.Reply(request, data);
            var envelope = NpepProtocol.Parse(await response.Content.ReadAsByteArrayAsync());
            envelope["protocolVersion"] = "0.18";
            response.Content = new StringContent(envelope.ToJsonString());
            if (late) response.StatusCode = HttpStatusCode.Forbidden;
            return response;
        }

        using (var paired = new NpepDevice(directory.Path, school.Api)) await school.ActivateAsync(paired);
        await using var runtime = new NpepRuntime(() => new(directory.Path, origin => new(origin, new Handler(Send))),
            "update-reconnect", Sample, updates: new SchoolUpdateProbe());
        try
        {
            await Until(() => runtime.UpdateStatus()?.Enrollment == "INVITED");
            Assert.True(runtime.AcceptUpdateInvitation(invitation));
            await Until(() => runtime.UpdateStatus()?.MaintenanceReceipt?.PlanId == plan);
            await oldReadStarted.Task.WaitAsync(TimeSpan.FromSeconds(27));
            if (reconnect)
            {
                Assert.True((await Command(runtime, "pause")).ReportingPaused);
                Assert.False((await Command(runtime, "resume")).ReportingPaused);
                await Until(() => runtime.Snapshot().Connection == "ONLINE");
            }
            Assert.True(runtime.UpdateStatus()?.LocalConsent);
            releaseOldRead.TrySetResult();
            await Until(() => runtime.UpdateStatus()?.LocalConsent == false);
            var receipt = runtime.UpdateStatus()?.MaintenanceReceipt;
            if (reconnect)
            {
                Assert.Equal(plan, receipt?.PlanId);
                Assert.True(receipt?.IsHistorical);
            }
            else
                Assert.Null(receipt);
            Assert.Equal(2, Volatile.Read(ref managedReads));
        }
        finally { releaseOldRead.TrySetResult(); }
    }
}

using System.Text.Json.Nodes;
using NPEduTools.App;
using NPEduTools.Contracts;

namespace NPEduTools.Tests;

public sealed class NpepDiagnosticsTests
{
    [Theory]
    [InlineData("v1.1.5 · EVA-02（二号机）", "1.1.5")]
    [InlineData("v1.1.5 · EVA-02（二号机）+abcdef1234567890", "1.1.5+abcdef1234567890")]
    [InlineData("v1.1.5 · PRIVATE-CREDENTIAL", null)]
    [InlineData("v1.1.5 · EVA-02（二号机）\nsecret", null)]
    public void CurrentDesktopVersionIsExportedWithoutArbitraryMetadata(string value, string? expected)
    {
        var report = NpepDiagnostics.Create(value, At, null, null, null, null);
        Assert.Equal(expected, report["appVersion"]?.GetValue<string>());
    }
    private static readonly DateTimeOffset At = DateTimeOffset.Parse("2026-10-04T18:00:00+08:00");
    private static readonly Guid Binding = Guid.Parse("fd6298d3-a8ad-4dac-847d-b4d831410092");
    private static HostResponse Reply(HostRequest request, NpepState? state = null) => new(Protocol.Version, request.RequestId, "Succeeded", null, "not exported", Npep: state);
    [Theory]
    [InlineData("SAMPLE_TOO_OLD")]
    [InlineData("RATE_LIMITED")]
    [InlineData("PROTOCOL_UNSUPPORTED")]
    public void Known_connection_failure_codes_survive_diagnostic_export(string code)
    {
        var report = NpepDiagnostics.Create("1.1.5", At, Reply(new(1, Guid.NewGuid(), "npep.status"), new(1, "ACTIVE", "OFFLINE", "", Error: code)), At, null, null);
        Assert.Equal(code, report["connection"]!["error"]!.GetValue<string>());
    }
    [Fact]
    public void History_export_is_bounded_and_sanitized_independently_of_the_Host()
    {
        var entries = Enumerable.Range(0, 80).Select(_ => new NpepConnectionEvent(Guid.NewGuid(), At, "SECRET", "https://secret.invalid", "SECRET", -1, Guid.Empty, long.MaxValue, true)).ToArray();
        var history = new HostResponse(1, Guid.NewGuid(), "Succeeded", null, "SECRET", ConnectionHistory: new("SECRET", entries));
        var report = NpepDiagnostics.Create("1.1.5", At, null, null, null, null, history);
        Assert.Equal(48, report["connectionHistory"]!["events"]!.AsArray().Count); Assert.DoesNotContain("SECRET", report.ToJsonString());
        Assert.Null(report["connectionHistory"]!["events"]![0]!["requestId"]);
        Assert.Null(report["connectionHistory"]!["events"]![0]!["httpStatus"]);
        Assert.False(NpepDiagnostics.Create("1.1.5", At, null, null, null, null, history with { Outcome = "Rejected" })["connectionHistory"]!["available"]!.GetValue<bool>());
    }
    [Fact]
    public async Task Old_Host_without_diagnostic_capability_still_exports_current_status_and_cache_read_only()
    {
        var capabilities = new List<string>();
        var session = new NpepConnectionSession((request, _) => {
            capabilities.Add(request.Capability); Assert.Null(request.Npep);
            return Task.FromResult(request.Capability == "npep.diagnostics" ? Reply(request) with { Outcome = "Rejected", ErrorCode = "UnknownCapability" } : Reply(request, new(1, "ACTIVE", "ONLINE", "")));
        });
        var report = await session.CollectDiagnosticsAsync("1.1.5");
        Assert.Equal(new[] { "npep.status", "npep.diagnostics", "host.cached-status" }, capabilities);
        Assert.True(report!["connection"]!["available"]!.GetValue<bool>()); Assert.False(report["connectionHistory"]!["available"]!.GetValue<bool>());
        Assert.Null(Protocol.Validate(new(1, Guid.NewGuid(), "npep.diagnostics")));
        Assert.Equal("InvalidNpepCommand", Protocol.Validate(new(1, Guid.NewGuid(), "npep.diagnostics", Npep: new("resume", 1))));
    }

    [Fact]
    public void ExportUsesAllowListEvenWhenSourceContainsSecretsAndArbitraryNestedFields()
    {
        const string secret = "DO-NOT-EXPORT-ABCD2345";
        var state = new NpepState(4, "ACTIVE", "ONLINE", secret, Origin: "https://api.example.test:8443/ignored?key=" + secret,
            Pairing: new() { ["userCode"] = secret }, Approval: new() { ["schoolId"] = Binding.ToString(),
                ["screenBindingId"] = Binding.ToString(), ["schoolName"] = secret, ["credentialId"] = Binding.ToString(), ["secret"] = secret },
            Error: "TLS_VALIDATION_FAILED", LastReceivedAt: "2026-10-04T10:00:00Z");
        var request = new HostRequest(1, Guid.NewGuid(), "npep.status");
        var cache = Reply(request) with { ClassroomMode = new(Mode: "Exam", Actual: new(secret, 0, false, secret, 0, true)),
            Recording = new("Idle", secret, OutputFile: secret), ExamAwarePairing = new(1, "localhost", 1234, secret),
            ExamAware = new(secret, 1, "Connected", secret, "1.5.2", true, false, 1234) };
        var report = NpepDiagnostics.Create("InDev 20261002", At, Reply(request, state), At, cache, At);
        string json = report.ToJsonString();
        Assert.DoesNotContain(secret, json);
        Assert.DoesNotContain("credentialId", json);
        Assert.Equal("https://api.example.test:8443", report["connection"]!["origin"]!.GetValue<string>());
        Assert.Equal(Binding.ToString(), report["connection"]!["binding"]!["screenBindingId"]!.GetValue<string>());
        Assert.Equal("TLS_VALIDATION_FAILED", report["connection"]!["error"]!.GetValue<string>());
        Assert.Equal("Connected", report["localCache"]!["examAwareBridge"]!.GetValue<string>());
        Assert.Equal("2026-10-04T10:00:00.0000000+00:00", report["generatedAt"]!.GetValue<string>());
    }

    [Fact]
    public void UnknownValuesAndRejectedResponsesCannotMasqueradeAsObservedStatus()
    {
        var request = new HostRequest(1, Guid.NewGuid(), "npep.status");
        var state = new NpepState(-1, "SECRET", "SECRET", "SECRET", Origin: "https://user:secret@example.test",
            LastReceivedAt: "secret", Error: "SECRET", Approval: new() { ["schoolId"] = "secret" });
        var report = NpepDiagnostics.Create("secret", At, Reply(request, state), At, null, null);
        Assert.Null(report["appVersion"]);
        Assert.Null(report["connection"]!["origin"]);
        Assert.Null(report["connection"]!["revision"]);
        Assert.Null(report["connection"]!["lastReceivedAt"]);
        Assert.DoesNotContain("SECRET", report.ToJsonString());
        report = NpepDiagnostics.Create("1.2.3", At, Reply(request, state) with { Outcome = "Rejected" }, At, null, null);
        Assert.False(report["connection"]!["available"]!.GetValue<bool>());
        Assert.Null(report["connection"]!["observedAt"]);
        Assert.False(report["localCache"]!["available"]!.GetValue<bool>());
        Assert.Equal("Rejected", report["connection"]!["readOutcome"]!.GetValue<string>());
    }

    [Fact]
    public async Task CollectionReadsOnlyKnownSnapshotsAndPreservesMissingSection()
    {
        var capabilities = new List<string>();
        var session = new NpepConnectionSession((request, _) =>
        {
            capabilities.Add(request.Capability);
            Assert.Null(request.Npep); Assert.Null(request.ClassroomMode); Assert.Null(request.Noise);
            if (request.Capability == "npep.status") throw new IOException("secret credential and path");
            return Task.FromResult(Reply(request) with { ClassroomMode = new(Mode: "Exam") });
        });
        var report = await session.CollectDiagnosticsAsync("InDev 20261002");
        Assert.Equal(["npep.status", "host.cached-status"], capabilities);
        Assert.False(report!["connection"]!["available"]!.GetValue<bool>());
        Assert.True(report["localCache"]!["available"]!.GetValue<bool>());
        Assert.Equal("Exam", report["localCache"]!["mode"]!.GetValue<string>());
        Assert.DoesNotContain("secret", report.ToJsonString());
        Assert.True(session.CanRefresh);
    }

    [Fact]
    public async Task CollectingFreshFailureCannotReusePreviouslyHealthyConnection()
    {
        bool failed = false;
        var session = new NpepConnectionSession((request, _) => Task.FromResult(failed
            ? Reply(request, new(3, "ACTIVE", "ONLINE", "private")) with { Outcome = "Rejected", ErrorCode = "INVALID_RESPONSE" }
            : Reply(request, new(2, "ACTIVE", "ONLINE", "private"))));
        Assert.True(await session.RefreshAsync());
        Assert.True(session.HasPairing);
        failed = true;
        var report = await session.CollectDiagnosticsAsync("1.2.3");
        Assert.False(report!["connection"]!["available"]!.GetValue<bool>());
        Assert.False(report["localCache"]!["available"]!.GetValue<bool>());
        Assert.Null(report["connection"]!["observedAt"]);
        Assert.Equal("UNKNOWN", report["connection"]!["state"]!.GetValue<string>());
        Assert.True(session.HasPairing); // Export does not mutate the user's existing UI session.
    }

    [Theory]
    [InlineData("npep.status", "connection", "localCache")]
    [InlineData("host.cached-status", "localCache", "connection")]
    public async Task DeniedHostAccessPreservesOtherDiagnosticSectionAndAllowsReadOnlyRecovery(
        string deniedCapability, string missingSection, string availableSection)
    {
        string pipeName = "NPEduTools.Test.diagnostics-denied." + Guid.NewGuid().ToString("N");
        using var deniedPipe = new System.IO.Pipes.NamedPipeServerStream(pipeName,
            System.IO.Pipes.PipeDirection.Out, 1, System.IO.Pipes.PipeTransmissionMode.Byte,
            System.IO.Pipes.PipeOptions.Asynchronous | System.IO.Pipes.PipeOptions.CurrentUserOnly);
        bool deny = false;
        var capabilities = new List<string>();
        var session = new NpepConnectionSession((request, token) =>
        {
            capabilities.Add(request.Capability);
            if (deny && request.Capability == deniedCapability)
                return HostClient.RequestAsync(pipeName, request, token);
            return Task.FromResult(Reply(request, new(2, "ACTIVE", "ONLINE", "private"))
                with { ClassroomMode = new(Mode: "Daily") });
        });
        Assert.True(await session.RefreshAsync());
        session.DeviceName = "保留设备草稿";
        deny = true;
        capabilities.Clear();
        var report = await session.CollectDiagnosticsAsync("1.1.5");
        Assert.Equal(deniedCapability == "npep.status" ? ["npep.status", "host.cached-status"] : new[] { "npep.status", "npep.diagnostics", "host.cached-status" }, capabilities);
        Assert.False(report![missingSection]!["available"]!.GetValue<bool>());
        Assert.Null(report[missingSection]!["observedAt"]);
        Assert.True(report[availableSection]!["available"]!.GetValue<bool>());
        Assert.True(session.HasPairing); // Diagnostic collection does not rewrite the UI connection.
        Assert.Equal("保留设备草稿", session.DeviceName);
        Assert.True(session.CanRefresh);
        deny = false;
        var recovered = await session.CollectDiagnosticsAsync("1.1.5");
        Assert.True(recovered!["connection"]!["available"]!.GetValue<bool>());
        Assert.True(recovered["localCache"]!["available"]!.GetValue<bool>());
        Assert.All(capabilities, capability => Assert.Contains(capability, new[] { "npep.status", "npep.diagnostics", "host.cached-status" }));
    }

    [Fact]
    public async Task BusyCollectionBlocksCompetingRequestsAndClosingCancelsFurtherReads()
    {
        var pending = new TaskCompletionSource<HostResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var lifetime = new CancellationTokenSource();
        int calls = 0;
        var session = new NpepConnectionSession((request, _) => { calls++; return pending.Task; }, lifetime.Token);
        var collecting = session.CollectDiagnosticsAsync("1.2.3");
        Assert.False(session.CanRefresh);
        Assert.False(await session.RefreshAsync());
        Assert.Null(await session.CollectDiagnosticsAsync("1.2.3"));
        lifetime.Cancel();
        pending.SetResult(new(1, Guid.NewGuid(), "Succeeded", null, ""));
        Assert.Null(await collecting);
        Assert.Equal(1, calls);
        Assert.True(session.CanRefresh);
    }
}

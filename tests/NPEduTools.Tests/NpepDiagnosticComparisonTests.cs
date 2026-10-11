using System.Text;
using System.Text.Json.Nodes;
using NPEduTools.App;
using NPEduTools.Contracts;

namespace NPEduTools.Tests;

public sealed class NpepDiagnosticComparisonTests
{
    private static readonly DateTimeOffset At = DateTimeOffset.Parse("2026-10-04T12:00:00Z");
    private static string Id(char value) => new string(value, 8) + "-" + new string(value, 4) + "-4" + new string(value, 3) + "-8" + new string(value, 3) + "-" + new string(value, 12);
    private static JsonObject School() => NpepDiagnosticComparison.ParseSchoolSummary(
        File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures/npep-school-diagnostic.json")));
    private static JsonObject Desktop() => NpepDiagnostics.Create("InDev 20261002", At,
        new(1, Guid.NewGuid(), "Succeeded", null, "private", Npep: new(1, "ACTIVE", "ONLINE", "private", Origin: "https://api.example.test",
            Approval: new() { ["schoolId"] = Id('1'), ["administrativeClassId"] = Id('2'), ["screenBindingId"] = Id('3') })), At,
        new(1, Guid.NewGuid(), "Succeeded", null, "private", ClassroomMode: new(Mode: "Daily", Phase: "Idle", CheckedAt: At), Recording: new("Saved", "private")), At);
    private static bool Has(NpepDiagnosticComparisonResult result, string code) => result.Findings.Any(item => item.Code == code);

    [Fact]
    public void ActualWebExporterFixtureMatchesTheActualDesktopExporterWithoutClaimingLiveState()
    {
        var school = School(); var desktop = Desktop();
        var result = NpepDiagnosticComparison.Compare(desktop, school, At);
        Assert.True(Has(result, "BINDING_MATCHED")); Assert.True(Has(result, "MODE_OBSERVATIONS_AGREE"));
        Assert.False(Has(result, "RECORDING_OBSERVATIONS_DIFFER"));
        Assert.True(Has(result, "OBSERVATION_ONLY"));
        Assert.DoesNotContain("SYNTHETIC_PRIVATE_NAME", school.ToJsonString());
        Assert.DoesNotContain(Id('3'), result.DisplayText);
    }

    [Theory]
    [InlineData("https://api.example.test:443/", true)]
    [InlineData("https://API.EXAMPLE.TEST", true)]
    [InlineData("http://api.example.test", false)]
    [InlineData("https://api.example.test:8443", false)]
    [InlineData("https://user:secret@api.example.test", false)]
    [InlineData("https://api.example.test/school", false)]
    public void OriginsNormalizeDefaultPortsAndCaseButDoNotIgnoreProtocolPortOrCredentials(string origin, bool matches)
    {
        var desktop = Desktop(); desktop["connection"]!["origin"] = origin;
        var result = NpepDiagnosticComparison.Compare(desktop, School(), At);
        Assert.Equal(matches, Has(result, "BINDING_MATCHED"));
        if (!matches) Assert.DoesNotContain(result.Findings, item => item.Code == "MODE_OBSERVATIONS_AGREE");
        Assert.DoesNotContain("secret", result.DisplayText);
    }

    [Fact]
    public void MissingOrForeignBindingsNeverConfirmPairingAndMissingPageDoesNotMeanUnpaired()
    {
        var missing = Desktop(); missing["connection"]!["binding"]!["screenBindingId"] = "";
        Assert.True(Has(NpepDiagnosticComparison.Compare(missing, School(), At), "BINDING_UNAVAILABLE"));
        var other = School(); other["schoolId"] = Id('9');
        Assert.True(Has(NpepDiagnosticComparison.Compare(Desktop(), other, At), "SCHOOL_MISMATCH"));
        var partial = School(); partial["items"] = new JsonArray(); partial["exportedCount"] = 0; partial["loadedCount"] = 0;
        partial["hasMore"] = true; partial["filter"] = "ONLINE";
        var result = NpepDiagnosticComparison.Compare(Desktop(), partial, At);
        Assert.True(Has(result, "RECORD_NOT_INCLUDED")); Assert.True(Has(result, "PARTIAL_SCHOOL_EXPORT"));
        Assert.False(Has(result, "BINDING_MATCHED")); Assert.Contains("不能证明未配对", result.DisplayText);
        var changed = School(); changed["items"]![0]!["administrativeClassId"] = Id('9');
        Assert.True(Has(NpepDiagnosticComparison.Compare(Desktop(), changed, At), "CLASS_MISMATCH"));
    }

    [Fact]
    public void HistoricalDuplicatesAreAmbiguousEvenIfOnlyOneIsActive()
    {
        var school = School(); var historical = school["items"]![0]!.DeepClone(); historical["state"] = "REVOKED";
        ((JsonArray)school["items"]!).Add(historical); school["exportedCount"] = 2; school["loadedCount"] = 2; school["matchedTotal"] = 2;
        var result = NpepDiagnosticComparison.Compare(Desktop(), school, At);
        Assert.True(Has(result, "AMBIGUOUS_REGISTRATION")); Assert.False(Has(result, "MODE_OBSERVATIONS_AGREE"));
    }

    [Theory]
    [InlineData("REVOKED")]
    [InlineData("EXPIRED")]
    [InlineData("INVALIDATED")]
    public void InactiveSchoolRegistrationIsNotReportedAsHealthyRuntime(string state)
    {
        var school = School(); school["items"]![0]!["state"] = state;
        var result = NpepDiagnosticComparison.Compare(Desktop(), school, At);
        Assert.True(Has(result, "REGISTRATION_INACTIVE")); Assert.False(Has(result, "MODE_OBSERVATIONS_AGREE"));
    }

    [Fact]
    public void StaleFutureAndMissingTimestampsCannotProduceAnUnqualifiedAgreement()
    {
        var school = School(); school["stale"] = true;
        Assert.True(Has(NpepDiagnosticComparison.Compare(Desktop(), school, At), "OBSERVATION_STALE"));
        school = School(); school["generatedAt"] = At.AddHours(1).ToString("O");
        Assert.True(Has(NpepDiagnosticComparison.Compare(Desktop(), school, At), "TIME_REFERENCE_UNCERTAIN"));
        school = School(); school["asOf"] = "missing";
        Assert.True(Has(NpepDiagnosticComparison.Compare(Desktop(), school, At), "TIME_REFERENCE_UNCERTAIN"));
        var desktop = Desktop(); desktop["localCache"]!["modeCheckedAt"] = At.AddDays(-1).ToString("O");
        Assert.True(Has(NpepDiagnosticComparison.Compare(desktop, School(), At), "LOCAL_MODE_CACHE_OLD"));
    }

    [Fact]
    public void PausedTlsAndDifferentRuntimeObservationsHaveSpecificAdviceWithoutRunningCommands()
    {
        var desktop = Desktop(); desktop["connection"]!["reportingPaused"] = true;
        desktop["connection"]!["error"] = "TLS_VALIDATION_FAILED"; desktop["connection"]!["transport"] = "OFFLINE";
        desktop["localCache"]!["mode"] = "Exam"; desktop["localCache"]!["recordingPhase"] = "Recording";
        var result = NpepDiagnosticComparison.Compare(desktop, School(), At);
        foreach (var code in new[] { "LOCAL_REPORTING_PAUSED", "TLS_VALIDATION_FAILED", "LOCAL_TRANSPORT_UNAVAILABLE", "MODE_OBSERVATIONS_DIFFER", "RECORDING_OBSERVATIONS_DIFFER" }) Assert.True(Has(result, code));
        Assert.Contains("不能据此判定切换失败", result.DisplayText);
    }

    [Fact]
    public void UnknownFreeTextIsNotEchoedAndUnavailableSectionsAreNotHealthy()
    {
        const string secret = "PRIVATE_NAME_PASSWORD_PATH_COMMAND";
        var school = School(); school["private"] = secret; school["items"]![0]!["status"]!["mode"] = secret;
        var desktop = Desktop(); desktop["connection"]!["error"] = secret;
        var result = NpepDiagnosticComparison.Compare(desktop, school, At);
        Assert.True(Has(result, "MODE_UNKNOWN")); Assert.DoesNotContain(secret, result.DisplayText);
        desktop["localCache"]!["available"] = false;
        Assert.True(Has(NpepDiagnosticComparison.Compare(desktop, School(), At), "RUNTIME_DATA_UNAVAILABLE"));
        desktop["connection"]!["available"] = false;
        Assert.True(Has(NpepDiagnosticComparison.Compare(desktop, School(), At), "LOCAL_CONNECTION_UNAVAILABLE"));
    }

    [Theory]
    [InlineData("DAILY", "READY", "DISCONNECTED", false, false)]
    [InlineData("EXAM", "DISCONNECTED", "READY", false, false)]
    [InlineData("DAILY", "DISCONNECTED", "READY", true, false)]
    [InlineData("EXAM", "READY", "DISCONNECTED", false, true)]
    public void BridgeAdviceDependsOnTheModeObservedByTheSchool(string mode, string ci, string ea, bool ciIssue, bool eaIssue)
    {
        var school = School(); var status = school["items"]![0]!["status"]!;
        status["mode"] = mode; status["classIsland"]!["connection"] = ci; status["examAware"]!["connection"] = ea;
        var result = NpepDiagnosticComparison.Compare(Desktop(), school, At);
        Assert.Equal(ciIssue, Has(result, "CLASSISLAND_NEEDS_REVIEW")); Assert.Equal(eaIssue, Has(result, "EXAMAWARE_NEEDS_REVIEW"));
    }

    [Theory]
    [InlineData("http://[::1]:3000", "http://[::1]:3000/")]
    [InlineData("https://例子.测试", "https://xn--fsqu00a.xn--0zwm56d")]
    public void LocalIpv6AndIdnOriginsRemainComparable(string local, string remote)
    {
        var desktop = Desktop(); desktop["connection"]!["origin"] = local;
        var school = School(); school["backendOrigin"] = remote;
        Assert.True(Has(NpepDiagnosticComparison.Compare(desktop, school, At), "BINDING_MATCHED"));
    }

    [Fact]
    public async Task FileImportSupportsUtf8BomAndBoundsTheActualStreamWithoutTrustingMetadata()
    {
        byte[] content = Encoding.UTF8.GetBytes(School().ToJsonString());
        using var valid = new MemoryStream(new byte[] { 0xef, 0xbb, 0xbf }.Concat(content).ToArray());
        var root = await NpepDiagnosticComparison.ReadSchoolSummaryAsync(valid);
        Assert.Equal("npep-school-overview-diagnostics", root["kind"]!.GetValue<string>());
        using var oversized = new MemoryStream(new byte[NpepDiagnosticComparison.MaximumInputBytes + 100]);
        await Assert.ThrowsAsync<InvalidDataException>(() => NpepDiagnosticComparison.ReadSchoolSummaryAsync(oversized));
        Assert.Equal(NpepDiagnosticComparison.MaximumInputBytes + 1, oversized.Position);
        using var cancelled = new MemoryStream(content); using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => NpepDiagnosticComparison.ReadSchoolSummaryAsync(cancelled, cancellation.Token));
    }

    [Fact]
    public void MalformedUnsupportedDuplicateDeepAndOversizedInputsAreRejected()
    {
        var bad = School(); bad["schemaVersion"] = 2;
        var tooMany = School(); var items = new JsonArray(); for (int i = 0; i < 201; i++) items.Add(new JsonObject()); tooMany["items"] = items;
        var inconsistent = School(); inconsistent["exportedCount"] = 0;
        foreach (var text in new[] { "not json", "[]", bad.ToJsonString(), tooMany.ToJsonString(), inconsistent.ToJsonString(),
            School().ToJsonString().Replace("\"schemaVersion\":1", "\"schemaVersion\":1,\"schemaVersion\":1"),
            new string('[', 33) + "0" + new string(']', 33) })
            Assert.Throws<InvalidDataException>(() => NpepDiagnosticComparison.ParseSchoolSummary(Encoding.UTF8.GetBytes(text)));
        Assert.Throws<InvalidDataException>(() => NpepDiagnosticComparison.ParseSchoolSummary(new byte[NpepDiagnosticComparison.MaximumInputBytes + 1]));
    }

    [Fact]
    public async Task CallerCancellationReleasesDiagnosticGateAndStopsBeforeSecondHostRead()
    {
        using var cancel = new CancellationTokenSource(); int calls = 0;
        var pending = new TaskCompletionSource<HostResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        var session = new NpepConnectionSession((_, token) => { calls++; return pending.Task.WaitAsync(token); });
        var collecting = session.CollectDiagnosticsAsync("1.2.3", cancel.Token);
        cancel.Cancel(); Assert.Null(await collecting);
        Assert.Equal(1, calls); Assert.True(session.CanRefresh);
    }

    [Fact]
    public void FreshCrossRepositoryExportsAreComparedWhenProvided()
    {
        var path = Environment.GetEnvironmentVariable("NPEP_DIAGNOSTIC_COMPARISON_CASES");
        if (string.IsNullOrEmpty(path))
        {
            // Normal solution runs still execute a comparison of the committed real-export fixture.
            Assert.True(Has(NpepDiagnosticComparison.Compare(Desktop(), School(), At), "BINDING_MATCHED"));
            return;
        }
        var cases = JsonNode.Parse(File.ReadAllText(path))!.AsArray();
        string[] names = cases.Select(item => item!["name"]!.GetValue<string>()).ToArray();
        Assert.Equal(new[] { "unknown", "daily", "exam", "incomplete" }, names);
        foreach (var item in cases)
        {
            var desktop = item!["desktop"]!.AsObject();
            var school = NpepDiagnosticComparison.ParseSchoolSummary(Encoding.UTF8.GetBytes(item["school"]!.ToJsonString()));
            var now = DateTimeOffset.Parse(desktop["generatedAt"]!.GetValue<string>());
            var result = NpepDiagnosticComparison.Compare(desktop, school, now);
            Assert.True(Has(result, "BINDING_MATCHED"));
            if (item["name"]!.GetValue<string>() == "unknown") Assert.True(Has(result, "RUNTIME_DATA_UNAVAILABLE"));
            else Assert.True(Has(result, "MODE_OBSERVATIONS_AGREE"));
            if (item["name"]!.GetValue<string>() == "incomplete")
            {
                Assert.True(Has(result, "MODE_NEEDS_REVIEW")); Assert.True(Has(result, "EXAMAWARE_NEEDS_REVIEW"));
            }
            Assert.DoesNotContain("SYNTHETIC_SECRET", result.DisplayText);
            Assert.DoesNotContain("SYNTHETIC_PRIVATE_NAME", result.DisplayText);
        }
        var output = Environment.GetEnvironmentVariable("NPEP_DIAGNOSTIC_COMPARISON_RESULT");
        if (!string.IsNullOrEmpty(output)) File.WriteAllText(output, System.Text.Json.JsonSerializer.Serialize(new
        { status = "PASSED", source = "fresh-cross-repository-exports", cases = names }));
    }
}

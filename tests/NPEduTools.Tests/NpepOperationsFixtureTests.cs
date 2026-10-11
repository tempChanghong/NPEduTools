using System.Text.Json;
using System.Text.Json.Nodes;
using NPEduTools.App;
using NPEduTools.Contracts;
using NPEduTools.Integrations.Npep;

namespace NPEduTools.Tests;

public sealed class NpepOperationsFixtureTests
{
    [Fact]
    public void ActualDesktopStatusAndDiagnosticsProduceSharedSyntheticCases()
    {
        var at = DateTimeOffset.Parse("2026-10-04T10:00:00Z");
        var id = Guid.Parse("12345678-1234-4234-8234-123456789abc");
        const string secret = "DO_NOT_EXPORT_PAIRING_OR_PRIVATE_CONTENT";
        var connection = new NpepState(1, "ACTIVE", "ONLINE", secret, Origin: "https://api.example.test",
            Pairing: new() { ["userCode"] = secret }, Approval: new() { ["schoolId"] = id.ToString(),
                ["administrativeClassId"] = id.ToString(), ["screenBindingId"] = id.ToString(), ["schoolName"] = secret });
        HostResponse Cache(string mode, string phase, string clock, string recording, string ea) => new(1, id, "Succeeded", null, secret,
            ClassroomMode: new(Revision: 3, Mode: mode, Phase: phase, CheckedAt: at),
            SchoolClock: new(id, id, 1, 1, at, 10, clock, secret),
            Recording: new(recording, secret, OutputFile: secret),
            Automatic: new(true, id, secret, null, []),
            ExamAware: new(secret, 1, ea, secret, "1.5.2", true, false, null));
        var cases = new[]
        {
            new { Name = "unknown", Cache = (HostResponse?)null, Mode = "UNKNOWN", Recording = "UNKNOWN", Issues = new[] { "MODE_NOT_READY", "RECORDING_UNKNOWN" } },
            new { Name = "daily", Cache = (HostResponse?)Cache("Daily", "Idle", "Advancing", "Idle", "Disconnected"), Mode = "DAILY", Recording = "IDLE", Issues = Array.Empty<string>() },
            new { Name = "exam", Cache = (HostResponse?)Cache("Exam", "Idle", "Stale", "Recording", "Connected"), Mode = "EXAM", Recording = "RECORDING", Issues = Array.Empty<string>() },
            new { Name = "incomplete", Cache = (HostResponse?)Cache("Exam", "Incomplete", "Stale", "Saving", "Disconnected"), Mode = "EXAM", Recording = "FINALIZING", Issues = new[] { "MODE_INCOMPLETE", "EXAMAWARE_NOT_CONNECTED" } }
        };
        var output = new JsonArray();
        foreach (var item in cases)
        {
            var status = NpepStatusReader.Map(item.Cache, "InDev 20261002");
            Assert.Equal(item.Mode, status["mode"]!.GetValue<string>());
            Assert.Equal(item.Recording, status["recording"]!.GetValue<string>());
            var diagnostic = NpepDiagnostics.Create("InDev 20261002", at,
                new(1, id, "Succeeded", null, secret, Npep: connection), at, item.Cache, item.Cache is null ? null : at);
            Assert.DoesNotContain(secret, diagnostic.ToJsonString());
            output.Add(new JsonObject { ["name"] = item.Name, ["status"] = status, ["desktop"] = diagnostic,
                ["expectedIssues"] = JsonSerializer.SerializeToNode(item.Issues) });
        }
        // Optional output contains only synthetic data; normal solution tests still perform all assertions.
        if (Environment.GetEnvironmentVariable("NPEP_OPERATIONS_FIXTURES") is { Length: > 0 } directory)
        {
            Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, "desktop-cases.json"), output.ToJsonString(new() { WriteIndented = true }));
        }
    }
}

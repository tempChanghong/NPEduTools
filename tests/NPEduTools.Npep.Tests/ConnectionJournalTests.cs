using System.Text.Json;
using NPEduTools.Contracts;
using NPEduTools.Integrations.Npep;

namespace NPEduTools.Npep.Tests;

public sealed class ConnectionJournalTests
{
    [Fact]
    public async Task History_is_bounded_redacted_atomic_and_survives_recreation()
    {
        using var directory = new TestDirectory();
        await using (var history = new NpepConnectionJournal(directory.Path)) {
            for (int i = 0; i < 100; i++) history.Add("Failure", "Status", "SAMPLE_TOO_OLD", retrying: true);
            history.Add("PRIVATE", "https://private.invalid/?secret=PRIVATE", "PRIVATE", -1, Guid.Empty, long.MaxValue);
            Assert.Equal(48, history.Snapshot().Events.Count);
        }
        string file = Path.Combine(directory.Path, "connection-history.json"), json = await File.ReadAllTextAsync(file);
        Assert.DoesNotContain("PRIVATE", json); Assert.True(new FileInfo(file).Length <= 32768);
        await using var reopened = new NpepConnectionJournal(directory.Path);
        Assert.Equal("Stored", reopened.Snapshot().Persistence); Assert.Equal(48, reopened.Snapshot().Events.Count);
        Assert.Contains(reopened.Snapshot().Events, e => e.Code == "SAMPLE_TOO_OLD" && e.Retrying == true);
        var last = reopened.Snapshot().Events.Last(); Assert.Equal("UNKNOWN", last.Code); Assert.Equal("Unknown", last.Endpoint);
        Assert.Null(last.HttpStatus); Assert.Null(last.ElapsedMs); Assert.Null(last.RequestId);
        var response = new HostResponse(1, Guid.NewGuid(), "Succeeded", null, "fixture", ConnectionHistory: reopened.Snapshot());
        Assert.True(JsonSerializer.SerializeToUtf8Bytes(response, Protocol.Json).Length < Protocol.MaxFrameBytes);
        Assert.DoesNotContain("connectionHistory", JsonSerializer.Serialize(response with { ConnectionHistory = null }, Protocol.Json));
    }
    [Fact]
    public async Task Corrupted_or_unwritable_history_never_throws_or_becomes_a_credential_failure()
    {
        using var directory = new TestDirectory();
        Directory.CreateDirectory(directory.Path);
        await File.WriteAllTextAsync(Path.Combine(directory.Path, "connection-history.json"), "{PRIVATE broken json");
        await using (var history = new NpepConnectionJournal(directory.Path)) {
            Assert.Equal("Unavailable", history.Snapshot().Persistence); Assert.Empty(history.Snapshot().Events);
            history.Add("Recovered", "Status");
        }
        Assert.DoesNotContain("PRIVATE", await File.ReadAllTextAsync(Path.Combine(directory.Path, "connection-history.json")));
        string impossibleDirectory = Path.Combine(directory.Path, "not-a-directory"); await File.WriteAllTextAsync(impossibleDirectory, "owned fixture");
        await using var blocked = new NpepConnectionJournal(impossibleDirectory);
        blocked.Add("Failure", "Status", "RATE_LIMITED", 429, retrying: true);
        Assert.Single(blocked.Snapshot().Events);
        Assert.Equal("owned fixture", await File.ReadAllTextAsync(impossibleDirectory));
    }
    [Fact]
    public async Task Diagnostic_sink_failure_does_not_replace_http_result()
    {
        using var api = new NpepApi("https://isolated.invalid", new Handler(request => Task.FromResult(Fixtures.Reply(request, new(), "TEMPORARILY_UNAVAILABLE", 503))));
        api.Observation = _ => throw new IOException("private diagnostic sink failure");
        var error = await Assert.ThrowsAsync<NpepException>(() => api.SendAsync("info", "infoResponse"));
        Assert.Equal("TEMPORARILY_UNAVAILABLE", error.Code); Assert.Equal(503, error.Status);
    }
}

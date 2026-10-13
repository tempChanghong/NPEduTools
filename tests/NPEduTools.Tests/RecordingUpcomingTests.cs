using System.Diagnostics;
using System.Reflection;
using NPEduTools.Contracts;
using NPEduTools.Core;
using NPEduTools.Host;

namespace NPEduTools.Tests;

public sealed class RecordingUpcomingTests
{
    [Fact]
    public async Task SkipUpcomingPersistsNextOccurrenceWithoutStoppingActiveAutomaticSession()
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        string pipe = "NPEduTools.Test.upcoming." + Guid.NewGuid().ToString("N");
        string directory = Path.Combine(Path.GetTempPath(), pipe);
        var service = new RecordingService(pipe, directory, () => SchoolClockFrame.Unavailable("isolated fixture"));
        T Field<T>(string name) => (T)typeof(RecordingService).GetField(name, flags)!.GetValue(service)!;
        void Set(string name, object value) => typeof(RecordingService).GetField(name, flags)!.SetValue(service, value);
        string path = Field<string>("_bookPath");
        // Deterministic command-only fixture: no worker is created and the background loop is stopped.
        Field<CancellationTokenSource>("_lifetime").Cancel();
        await Field<Task>("_loop");
        try
        {
            var date = new DateOnly(2031, 4, 7);
            var book = RecordingPlanBook.Create() with { Recurring = [], Dated =
                [new(Guid.NewGuid(), "当前课", date, new(12, 0), new(12, 30)), new(Guid.NewGuid(), "下一课", date, new(12, 35), new(13, 0))] };
            new RecordingPlanBookStore(path).Save(book);
            var plans = CalendarRecordingPlanner.Build(book, date, null);
            var ledger = Field<RecordingExecutionLedger>("_ledger");
            var current = ledger.Add(Guid.Empty, date, plans[0], "Recording", "synthetic active session");
            var state = new RecordingState("Recording", "synthetic active session", Control: new("Automatic", current.SessionId, current.Key, 0, 0));
            Set("_last", state);
            Guid connection = Guid.NewGuid(), bridge = Guid.NewGuid();
            var at = Stopwatch.GetElapsedTime(0);
            var now = new DateTimeOffset(date.ToDateTime(new(12, 15)), TimeSpan.Zero);
            var clock = Field<SchoolClockTracker>("_clock");
            for (int i = 0; i < 3; i++) clock.Accept(new(connection, bridge, i + 1, 0, now.AddSeconds(i * .5), 0, "Advancing", "fixture"), at - TimeSpan.FromSeconds(1 - i * .5), 0);
            Assert.True(clock.Read(Stopwatch.GetElapsedTime(0)).CanStart);
            var request = new HostRequest(Protocol.Version, Guid.NewGuid(), "recording.automatic", Automatic: new("skip-upcoming", Guid.NewGuid()));
            Assert.Null(Protocol.Validate(request));
            Assert.NotNull(Protocol.Validate(request with { Automatic = request.Automatic! with { Options = new("fixture", directory) } }));
            var response = await service.HandleAsync(request);
            Assert.Equal("Succeeded", response.Outcome);
            Assert.Equal(state, service.State);
            Assert.Equal("Recording", ledger.Document.Entries.Single(e => e.Subject == "当前课").Phase);
            Assert.Equal("Skipped", ledger.Document.Entries.Single(e => e.Subject == "下一课").Phase);
            var reopened = new RecordingExecutionLedger(Path.Combine(directory, "recording-executions.json")); reopened.Load();
            Assert.Equal("Skipped", reopened.Document.Entries.Single(e => e.Subject == "下一课").Phase);
        }
        finally
        {
            Set("_last", new RecordingState("Idle", "fixture finished"));
            await service.DisposeAsync();
            if (File.Exists(path)) File.Delete(path);
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }
}

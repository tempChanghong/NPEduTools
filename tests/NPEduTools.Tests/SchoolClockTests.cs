using NPEduTools.Contracts;
using NPEduTools.Core;
using NPEduTools.ClassIsland.Bridge.Contracts;
using NPEduTools.Integrations.ClassIsland;

namespace NPEduTools.Tests;

public sealed class SchoolClockTests
{
    private static readonly DateTimeOffset School = new(2031, 4, 7, 10, 0, 0, TimeSpan.Zero);
    private static readonly Guid Instance = Guid.NewGuid(), Connection = Guid.NewGuid();
    private static SchoolClockFrame Frame(int seq, double seconds = 0, string state = "Advancing", double age = 0) =>
        new(Connection, Instance, seq, 0, School.AddSeconds(seconds), age, state, "学校时间");
    private static TimeSpan M(double seconds) => TimeSpan.FromSeconds(seconds);
    private static SchoolClockTracker Healthy()
    {
        var tracker = new SchoolClockTracker();
        for (int i = 0; i < 3; i++) tracker.Accept(Frame(i + 1, i * .5), M(i * .5), 0);
        Assert.True(tracker.Read(M(1)).CanStart); return tracker;
    }
    [Fact] public void UsesSchoolCalendarAndRequiresThreeAdvancingSamples()
    {
        var tracker = new SchoolClockTracker(); tracker.Accept(Frame(1), M(0), 0);
        Assert.False(tracker.Read(M(0)).CanStart);
        tracker.Accept(Frame(2, .5), M(.5), 0); Assert.False(tracker.Read(M(.5)).CanStart);
        tracker.Accept(Frame(3, 1), M(1), 0);
        Assert.True(tracker.Read(M(1)).CanStart); Assert.Equal(School.AddSeconds(1), tracker.Read(M(1)).Now);
        Assert.Equal(School.AddSeconds(1), tracker.Read(M(2)).Now); // No extrapolation toward admission.
    }
    [Fact] public void RepeatedCachedResponseCannotRenewAge()
    {
        var tracker = Healthy();
        tracker.Accept(Frame(3, 1), M(3), 0); tracker.Accept(Frame(3, 1), M(4.1), 0);
        Assert.False(tracker.Read(M(4.1)).Fresh);
    }
    [Fact] public void FrozenTimeWithIncreasingSequencesNeverWarmsUp()
    {
        var tracker = new SchoolClockTracker();
        for (int i = 0; i < 10; i++) { tracker.Accept(Frame(i + 1), M(i * .5), 0); Assert.False(tracker.Read(M(i * .5)).CanStart); }
    }
    [Fact] public void DelayedResponseAndClientCacheAgeAreAccumulated()
    {
        var tracker = Healthy(); tracker.Accept(Frame(4, 1.5, age: 1000), M(1.5), 1500);
        Assert.False(tracker.Read(M(2.1)).Fresh);
    }
    [Fact] public void RecoveryAfterStaleRequiresFreshWarmup()
    {
        var tracker = Healthy(); Assert.False(tracker.Read(M(5)).CanStart);
        tracker.Accept(Frame(4, 5), M(5), 0); Assert.False(tracker.Read(M(5)).CanStart);
        tracker.Accept(Frame(5, 5.5), M(5.5), 0); tracker.Accept(Frame(6, 6), M(6), 0);
        Assert.True(tracker.Read(M(6)).CanStart);
    }
    [Theory] [InlineData(-120)] [InlineData(120)]
    public void BothClockJumpDirectionsRevalidate(double jump)
    {
        var tracker = Healthy(); tracker.Accept(Frame(4, 1.5 + jump) with { Epoch = 1 }, M(1.5), 0);
        Assert.False(tracker.Read(M(1.5)).CanStart);
        tracker.Accept(Frame(5, 2 + jump) with { Epoch = 1 }, M(2), 0);
        tracker.Accept(Frame(6, 2.5 + jump) with { Epoch = 1 }, M(2.5), 0);
        Assert.True(tracker.Read(M(2.5)).CanStart);
    }
    [Fact] public void NewConnectionMustWarmUpEvenWithSamePluginInstance()
    {
        var tracker = Healthy(); tracker.Accept(Frame(4, 1.5) with { ConnectionId = Guid.NewGuid() }, M(1.5), 0);
        Assert.False(tracker.Read(M(1.5)).CanStart);
    }
    [Fact] public void DateJumpRequiresReviewButNaturalMidnightDoesNot()
    {
        var tracker = Healthy();
        tracker.Accept(Frame(4, 86401.5) with { Epoch = 1 }, M(1.5), 0);
        tracker.Accept(Frame(5, 86402) with { Epoch = 1 }, M(2), 0);
        tracker.Accept(Frame(6, 86402.5) with { Epoch = 1 }, M(2.5), 0);
        Assert.True(tracker.Read(M(2.5)).DateNeedsReview); Assert.False(tracker.Read(M(2.5)).CanStart);
        tracker.ConfirmDate(); Assert.True(tracker.Read(M(2.5)).CanStart);
        tracker = new();
        var midnight = School.Date.AddDays(1);
        for (int i = 0; i < 5; i++) tracker.Accept(Frame(i + 1) with { SchoolNow = new DateTimeOffset(midnight, TimeSpan.Zero).AddSeconds(-1 + i * .5) }, M(i * .5), 0);
        Assert.True(tracker.Read(M(2)).CanStart); Assert.False(tracker.Read(M(2)).DateNeedsReview);
    }
    [Fact] public void UnknownTimezoneOrNonfiniteAgeCannotStart()
    {
        var tracker = Healthy(); tracker.Accept(Frame(4, 1.5, age: double.NaN), M(1.5), 0);
        Assert.False(tracker.Read(M(1.5)).Fresh);
        tracker.Accept(Frame(5, 2) with { SchoolNow = School.ToOffset(TimeSpan.FromHours(8)) }, M(2), 0);
        Assert.False(tracker.Read(M(2)).Fresh);
    }
    [Fact] public void SchoolParserNeverAddsWindowsOffset()
    {
        var parsed = ClassIslandSchoolClock.ParseSchoolTime("2031-04-07T10:00:00.0000000");
        Assert.Equal(School, parsed);
        Assert.Throws<InvalidDataException>(() => ClassIslandSchoolClock.ParseSchoolTime("2031-04-07T10:00:00.0000000Z"));
    }
    [Fact] public void HandshakeMissingCapabilityIsRejected()
    {
        Assert.Throws<InvalidDataException>(() => ClassIslandSchoolClock.ValidateHello(new(1, "npedutools.recordingbridge.p0", Instance, "0.1", "2.1", "Ready", ["current-day"])));
    }
    [Fact] public void UntestedHostVersionIsRejected()
    {
        Assert.Throws<InvalidDataException>(() => ClassIslandSchoolClock.ValidateHello(new(1, "npedutools.recordingbridge.p0", Instance,
            "0.1", "9.0.0.0", "Ready", ["effective-local-clock", "current-day", "sample-age", "read-only"])));
    }
    [Fact] public void SystemTimeRecordsMigrateRulesOnlyAndKeepOriginalBackup()
    {
        string directory = Path.Combine(Path.GetTempPath(), "NPEduTools-school-" + Guid.NewGuid()); Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "state.json");
        try
        {
            var store = new RecordingPreviewStore(path);
            store.Save(new(1, new(AfterMinutes: 3), [], [new(Instance, School, School.AddHours(1), "old")], new(2031, 4, 7)));
            string original = File.ReadAllText(path); var state = store.Read();
            Assert.Equal(2, state.Version); Assert.Equal(3, state.Rules.AfterMinutes); Assert.Empty(state.Marks); Assert.Null(state.SkipDate);
            Assert.Equal(original, File.ReadAllText(path + ".v1.bak")); Assert.True(store.Migrated);
            Assert.Equal(2, store.Read().Version); // Crash between backup and replacement is safe to retry.
        }
        finally { Directory.Delete(directory, true); }
    }
}

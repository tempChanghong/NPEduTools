using NPEduTools.Contracts;
using NPEduTools.Host;
using Xunit;

namespace NPEduTools.Tests;

public sealed class EdgeWebsitePolicyTests
{
    private const string Room = "binding-a";
    private const string NextRoom = "binding-b";
    private const string A = "douyin.com";
    private const string B = "new.example";
    private static readonly string First = new('a', 64), Second = new('b', 64);

    [Fact]
    public void RestartsOfflineAndSeparatesWritingFromBrowserEvidence()
    {
        var store = new MemoryStore();
        var initial = Start(store, Room);
        initial.Apply(Room, First, "GRADE", [A, B]);
        Assert.Equal("WRITTEN_PENDING", initial.Status);
        var first = initial.ReadDesktop()!;
        Assert.True(initial.Observe(new(first.ObservationId, A)));
        Assert.Equal("WRITTEN_PENDING", initial.Status);
        Assert.False(initial.Observe(new(first.ObservationId, "www.douyin.com")));
        Assert.True(initial.Observe(new(first.ObservationId, B)));
        Assert.Equal("EDGE_CONFIRMED", initial.Status);

        var restarted = Start(store, Room);
        Assert.Equal("WRITTEN_PENDING", restarted.Status);
        Assert.Equal(new[] {A, B}, restarted.ReadDesktop()!.BlockedHosts);
        Assert.False(restarted.Observe(new(first.ObservationId, A)));
        restarted.Bind(NextRoom);
        Assert.Null(restarted.ReadDesktop());
        Assert.Empty(store.Slots);
        restarted.Apply(Room, First, "GRADE", [A]);
        Assert.Empty(store.Slots);
    }

    [Fact]
    public void ConflictingAdditionRetainsExistingDesiredEntryAndOtherAuthorityIsPreserved()
    {
        var store = new MemoryStore();
        store.Slots[41] = ".external.example";
        var policy = Start(store, Room);
        policy.Apply(Room, First, "GRADE", [A]);
        var slot = store.Slots.Single(entry => entry.Value == "." + A).Key;
        var previous = policy.ReadDesktop()!;
        store.Allows.Add(B);
        policy.Apply(Room, Second, "CLASS", [A, B]);
        Assert.Equal("CONFLICT", policy.Status);
        Assert.Equal("." + A, store.Slots[slot]);
        Assert.Equal(new[] {A}, policy.ReadDesktop()!.BlockedHosts);
        Assert.Equal("CLASS", policy.ReadDesktop()!.Source);
        Assert.False(policy.Observe(new(previous.ObservationId, A)));
        Assert.DoesNotContain("." + B, store.Slots.Values);

        store.SeparateAuthority = true;
        policy.Apply(Room, Second, "CLASS", [A, B]);
        Assert.Equal("CONFLICT", policy.Status);
        Assert.Null(policy.ReadDesktop());
        Assert.Equal(new Dictionary<int, string?> { [41] = ".external.example" }, store.Slots);
        policy.Apply(Room, First, "CLASS", []);
        Assert.Equal("CLEARED_PENDING", policy.Status);
        policy.Bind(null);
        Assert.Null(store.Journal);
        Assert.Equal(".external.example", store.Slots[41]);
    }

    [Fact]
    public void OwnershipDoesNotTouchAnExternalReplacementOrTreatReadFailureAsCleared()
    {
        var store = new MemoryStore();
        var policy = Start(store, Room);
        policy.Apply(Room, First, "GRADE", [A]);
        int slot = store.Slots.Single().Key;
        store.Slots[slot] = ".someone-else.example";
        policy.Apply(Room, First, "GRADE", [A]);
        Assert.Equal("CONFLICT", policy.Status);
        policy.Bind(null);
        Assert.Equal(".someone-else.example", store.Slots[slot]);
        Assert.Null(store.Journal);

        var denied = new EdgeWebsitePolicy(Path.GetTempPath(), () => throw new UnauthorizedAccessException());
        denied.Bind(Room);
        denied.Apply(Room, First, "GRADE", [A]);
        Assert.Equal("FAILED", denied.Status);
    }

    [Fact]
    public void OfflineFallbackPersistsAndRejectsOlderOrDowngradedLists()
    {
        var store = new MemoryStore();
        var policy = Start(store, Room);
        var schedule = new EdgeWebsiteTiming(2, [new(A, "LESSON"), new(B, "ALWAYS")], "ALLOW");
        Assert.True(policy.ApplyTiming(Room, Second, "GRADE", schedule));
        Assert.Equal(new[] {B}, policy.ReadDesktop()!.BlockedHosts);
        Assert.Equal("UNAVAILABLE", policy.SchedulePhase);
        Assert.False(policy.ApplyTiming(Room, First, "GRADE", schedule with {Revision = 1, UnavailableBehavior = "BLOCK"}));
        Assert.False(policy.Apply(Room, First, "GRADE", [A]));
        var offline = Start(store, Room);
        Assert.Equal(new[] {B}, offline.ReadDesktop()!.BlockedHosts);
        Assert.True(offline.ApplyTiming(Room, First, "CLASS", schedule with {Revision = 3, UnavailableBehavior = "BLOCK"}));
        Assert.Equal(new[] {A, B}, offline.ReadDesktop()!.BlockedHosts.Order(StringComparer.Ordinal));
        offline.Bind(NextRoom);
        Assert.Empty(store.Slots);
    }

    [Fact]
    public void SamePublicationUpdatesOnlyTheActiveSetAndInvalidatesAnEarlierObservation()
    {
        var store = new MemoryStore();
        WebsitePhaseObservation phase = new("LESSON");
        var policy = new EdgeWebsitePolicy(Path.Combine(Path.GetTempPath(), "edge-test"), () => store, () => phase);
        policy.Bind(Room);
        Assert.True(policy.ApplyTiming(Room, First, "GRADE", new(1, [new(A, "LESSON|AFTER_SCHOOL"), new(B, "ALWAYS")], "ALLOW")));
        var lesson = policy.ReadDesktop()!;
        Assert.Equal(new[] {A, B}, lesson.BlockedHosts);
        int alwaysSlot = store.Slots.Single(row => row.Value == "." + B).Key;
        phase = default;
        policy.RefreshTiming();
        Assert.True(policy.SchedulePhaseHeld);
        Assert.Equal(lesson.ObservationId, policy.ReadDesktop()!.ObservationId);
        Assert.Equal(new[] {A, B}, policy.ReadDesktop()!.BlockedHosts);
        phase = new("BREAK");
        policy.RefreshTiming();
        Assert.False(policy.SchedulePhaseHeld);
        Assert.Equal(new[] {B}, policy.ReadDesktop()!.BlockedHosts);
        Assert.False(policy.Observe(new(lesson.ObservationId, A)));
        Assert.Equal("." + B, store.Slots[alwaysSlot]);
        phase = new("AFTER_SCHOOL");
        policy.RefreshTiming();
        Assert.Equal(new[] {A, B}, policy.ReadDesktop()!.BlockedHosts.Order(StringComparer.Ordinal));
        phase = new(null, true);
        policy.RefreshTiming();
        Assert.Equal(new[] {B}, policy.ReadDesktop()!.BlockedHosts);
    }

    private static EdgeWebsitePolicy Start(MemoryStore store, string bind)
    {
        var result = new EdgeWebsitePolicy(Path.Combine(Path.GetTempPath(), "edge-test"), () => store);
        result.Bind(bind);
        return result;
    }

    private sealed class MemoryStore : IEdgeWebsitePolicyStore
    {
        public EdgeWebsiteJournal? Journal { get; private set; }
        public SortedDictionary<int, string?> Slots { get; } = new();
        public HashSet<string> Allows { get; } = [];
        public bool SeparateAuthority { get; set; }
        public EdgeWebsiteJournal? ReadJournal() => Journal is { } value ? value with { Rules = value.Rules.ToArray() } : null;
        public void SaveJournal(EdgeWebsiteJournal? journal) => Journal = journal is null ? null : journal with { Rules = journal.Rules.ToArray() };
        public IReadOnlyDictionary<int, string?> ReadBlocks() => new Dictionary<int, string?>(Slots);
        public bool HasSeparatePolicyAuthority() => SeparateAuthority;
        public bool HasExternalMatch(string host, IReadOnlySet<int> owned) => Allows.Contains(host) ||
            Slots.Any(entry => !owned.Contains(entry.Key) && entry.Value == "." + host);
        public bool TryWrite(int slot, string filter) => Slots.TryAdd(slot, filter);
        public bool RemoveIfOwned(int slot, string filter) => !Slots.TryGetValue(slot, out var existing) || existing == filter && Slots.Remove(slot);
        public void Dispose() { }
    }
}

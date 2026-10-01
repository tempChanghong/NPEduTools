using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using NPEduTools.Contracts;
using NPEduTools.Core;
using NPEduTools.ClassIsland.Bridge.Contracts;

namespace NPEduTools.Tests;

public sealed class NoiseScheduleTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };
    public static IEnumerable<object[]> Cases()
    {
        using var file = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "noise-schedule-cases.json")));
        foreach (var section in file.RootElement.EnumerateObject())
            foreach (var item in section.Value.EnumerateArray())
                yield return [section.Name, item.GetProperty("name").GetString()!, item.GetRawText()];
    }
    private static T Read<T>(JsonElement element) => element.Deserialize<T>(Json)!;
    private static DateTimeOffset Calendar(string value) => new(DateTime.ParseExact(value,
        ["yyyy-MM-dd'T'HH:mm:ss", "yyyy-MM-dd'T'HH:mm:ss.FFFFFFF"], CultureInfo.InvariantCulture, DateTimeStyles.None), TimeSpan.Zero);
    private static SchoolNoiseWindow? Window(JsonElement value) => value.ValueKind == JsonValueKind.Null ? null
        : new(Calendar(value.GetProperty("start").GetString()!), Calendar(value.GetProperty("end").GetString()!));
    private static void SameWindow(JsonElement expected, SchoolNoiseWindow? actual) => Assert.Equal(Window(expected), actual);
    private static NoiseScheduleInput Input(JsonElement e)
    {
        var clock = e.GetProperty("clock");
        var now = clock.GetProperty("now");
        return new(Read<EffectiveNoiseSchedule>(e.GetProperty("policy")),
            new(now.ValueKind == JsonValueKind.Null ? null : Calendar(now.GetString()!), 0,
                clock.GetProperty("fresh").GetBoolean(), clock.GetProperty("canStart").GetBoolean(), "simulated",
                clock.GetProperty("dateNeedsReview").GetBoolean()),
            e.GetProperty("eligible").GetBoolean(), e.GetProperty("leaseValid").GetBoolean(), e.GetProperty("microphoneConfigured").GetBoolean(),
            e.GetProperty("examBlocked").GetBoolean(), e.GetProperty("owner").GetString()!, e.GetProperty("captureBusy").GetBoolean(),
            e.GetProperty("microphoneKey").GetString()!, e.GetProperty("blocks").EnumerateArray().Select(b =>
                new NoiseWindowBlock(Window(b.GetProperty("window"))!, b.GetProperty("kind").GetString()!,
                    b.GetProperty("microphoneKey").GetString())).ToArray());
    }

    [Theory] [MemberData(nameof(Cases))]
    public void SharedCalendarAndDecisionCases(string section, string name, string json)
    {
        _ = name; // Name appears in the xUnit case identity.
        using var doc = JsonDocument.Parse(json);
        var item = doc.RootElement; var expected = item.GetProperty("expected");
        switch (section)
        {
            case "validation":
                Assert.Equal(expected.GetString(), SchoolNoiseSchedule.Validate(Read<SchoolNoisePolicy?>(item.GetProperty("policy")), item.GetProperty("grade").GetBoolean()));
                break;
            case "resolution":
                var resolved = SchoolNoiseSchedule.Resolve(Read<SchoolNoisePolicy?>(item.GetProperty("grade")), Read<SchoolNoisePolicy?>(item.GetProperty("classroom")));
                Assert.Equal(expected.GetProperty("source").GetString(), resolved.Source);
                Assert.Equal(JsonSerializer.Serialize(expected.GetProperty("rules"), Json), JsonSerializer.Serialize(resolved.Rules, Json));
                break;
            case "windows":
                var windows = SchoolNoiseSchedule.Windows(Read<EffectiveNoiseSchedule>(item.GetProperty("policy")), Calendar(item.GetProperty("now").GetString()!));
                SameWindow(expected.GetProperty("current"), windows.Current); SameWindow(expected.GetProperty("next"), windows.Next);
                break;
            case "decisions":
                var result = NoiseScheduleEvaluator.Evaluate(Input(item.GetProperty("input")));
                Assert.Equal(expected.GetProperty("action").GetString(), result.Action);
                Assert.Equal(expected.GetProperty("reason").GetString(), result.Reason);
                SameWindow(expected.GetProperty("window"), result.Window);
                break;
            case "leases":
                var lease = Read<NoiseScheduleLease>(item.GetProperty("lease")); var current = item.GetProperty("current");
                Assert.Equal(expected.GetBoolean(), lease.Valid(Guid.Parse(current.GetProperty("hostId").GetString()!),
                    current.GetProperty("scope").GetString()!, current.GetProperty("revision").GetInt64(), current.GetProperty("elapsedMs").GetDouble()));
                break;
            default: Assert.Fail("Unknown fixture group"); break;
        }
    }

    [Fact]
    public void RealClockTrackerStopsAutoOnFreezeAndWarmsUpAfterRecovery()
    {
        var clock = new SchoolClockTracker(); var connection = Guid.NewGuid(); var instance = Guid.NewGuid();
        var monday = Calendar("2026-09-28T19:30:00");
        void Feed(int seq, double elapsed, double schoolSeconds, long epoch = 0) => clock.Accept(
            new SchoolClockFrame(connection, instance, seq, epoch, monday.AddSeconds(schoolSeconds), 0, "Advancing", "simulated"), TimeSpan.FromSeconds(elapsed), 0);
        NoiseScheduleDecision Decide(string owner, double elapsed) => NoiseScheduleEvaluator.Evaluate(new(
            new("Grade", [new([1], "19:00", "21:00")]), clock.Read(TimeSpan.FromSeconds(elapsed)), true, true, true, false, owner, false, "mic-1", []));
        Feed(1, 0, 0); Assert.Equal("Wait", Decide("None", 0).Action);
        Feed(2, .5, .5); Feed(3, 1, 1); Assert.Equal("Start", Decide("None", 1).Action);
        Feed(4, 2, 1); Feed(5, 3.1, 1);
        Assert.Equal("Stop", Decide("Schedule", 3.1).Action);
        Assert.Equal("MANUAL_ACTIVE", Decide("Manual", 3.1).Reason);
        Feed(6, 4, 4); Feed(7, 4.5, 4.5); Feed(8, 5, 5);
        Assert.Equal("Start", Decide("None", 5).Action);
        Feed(9, 5.5, -114.5, 1); Assert.Equal("Stop", Decide("Schedule", 5.5).Action);
        Feed(10, 6, -114, 1); Feed(11, 6.5, -113.5, 1);
        Assert.Equal("Start", Decide("None", 6.5).Action);
        Assert.Equal("Stop", Decide("Schedule", 10).Action); // Bridge age, not Windows wall time.
    }

    [Fact]
    public void NaturalMidnightKeepsScheduleButDateJumpRequiresReview()
    {
        var tracker = new SchoolClockTracker(); var connection = Guid.NewGuid(); var instance = Guid.NewGuid();
        var boundary = Calendar("2026-09-28T23:59:59");
        void Feed(int seq, double elapsed, DateTimeOffset now, long epoch = 0) => tracker.Accept(
            new SchoolClockFrame(connection, instance, seq, epoch, now, 0, "Advancing", "simulated"), TimeSpan.FromSeconds(elapsed), 0);
        NoiseScheduleDecision Decide(double elapsed) => NoiseScheduleEvaluator.Evaluate(new(
            new("Grade", [new([1], "23:00", "01:00")]), tracker.Read(TimeSpan.FromSeconds(elapsed)), true, true, true, false, "Schedule", false, "mic", []));
        for (int i = 0; i < 5; i++) Feed(i + 1, i * .5, boundary.AddSeconds(i * .5));
        Assert.Equal("Keep", Decide(2).Action);
        for (int i = 0; i < 3; i++) Feed(i + 6, 2.5 + i * .5, boundary.AddDays(7).AddSeconds(2.5 + i * .5), 1);
        Assert.True(tracker.Read(TimeSpan.FromSeconds(3.5)).DateNeedsReview);
        Assert.Equal("Stop", Decide(3.5).Action);
        tracker.ConfirmDate(); Assert.Equal("Keep", Decide(3.5).Action);
    }

    [Theory] [InlineData(double.NaN)] [InlineData(double.PositiveInfinity)] [InlineData(double.NegativeInfinity)]
    public void NonfiniteLeaseCannotRenew(double value)
    {
        var host = Guid.NewGuid(); var lease = new NoiseScheduleLease(host, "scope", 1, 0, 100);
        Assert.False(lease.Valid(host, "scope", 1, value));
        Assert.False((lease with { ConfirmedAtMs = value }).Valid(host, "scope", 1, 1));
        Assert.False((lease with { LifetimeMs = value }).Valid(host, "scope", 1, 1));
    }

    [Fact]
    public void InvalidCalendarAndUnrecognizedFieldsAreRejected()
    {
        var policy = new EffectiveNoiseSchedule("Grade", [new([1], "19:00", "21:00")]);
        Assert.Throws<ArgumentException>(() => SchoolNoiseSchedule.Windows(policy, Calendar("2026-09-28T19:00:00").ToOffset(TimeSpan.FromHours(8))));
        Assert.Throws<ArgumentException>(() => SchoolNoiseSchedule.Windows(policy, new(1999, 1, 1, 0, 0, 0, TimeSpan.Zero)));
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<SchoolNoisePolicy>("{\"mode\":\"Disabled\",\"rules\":[],\"startMicrophone\":true}", Json));
        var normalJson = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<SchoolNoisePolicy>("{\"mode\":\"Disabled\",\"rules\":[],\"extra\":true}", normalJson));
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<SchoolNoisePolicy>("{\"mode\":\"Disabled\"}", normalJson));
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<SchoolNoiseRule>("{\"days\":[1],\"start\":\"19:00\"}", normalJson));
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<SchoolNoiseRule>("{\"days\":[1],\"start\":\"19:00\",\"end\":\"20:00\",\"extra\":true}", normalJson));
    }
}

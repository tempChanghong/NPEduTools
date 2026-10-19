using NPEduTools.Contracts;
using NPEduTools.Core;

namespace NPEduTools.Tests;

public sealed class StudyDisplayContinuationTests
{
    private sealed class Clock : TimeProvider
    {
        public long Ms;
        public override long TimestampFrequency => 1000;
        public override long GetTimestamp() => Ms;
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.MinValue;
    }
    private static StudyHostState State()
    {
        var start = new DateTimeOffset(2026, 10, 19, 19, 15, 0, TimeSpan.Zero);
        return new("version", "APPLIED", "", "ACTIVE", "Active", "", true, start.AddMinutes(10),
            new(start, start.AddMinutes(135)), null, null, Display: new(Guid.NewGuid(), true, true, 0));
    }
    [Fact]
    public void Failure_does_not_renew_the_confirmed_end_or_advance_school_time()
    {
        var clock = new Clock(); var continuation = new StudyDisplayContinuation(clock); var state = State();
        Assert.False(continuation.CanKeep); continuation.Observe(state, TimeSpan.FromMilliseconds(100));
        long remaining = (long)(state.Window!.End - state.SchoolNow!.Value).TotalMilliseconds + 5000 - 100;
        for (clock.Ms = 0; clock.Ms < remaining; clock.Ms += 500) Assert.True(continuation.CanKeep);
        clock.Ms = remaining - 1; Assert.True(continuation.CanKeep);
        clock.Ms++; Assert.False(continuation.CanKeep);
        Assert.Equal(state.Window.Start.AddMinutes(10), state.SchoolNow);
        clock.Ms += 60_000; Assert.False(continuation.CanKeep);
    }
    [Theory]
    [InlineData("withdrawn")][InlineData("unselected")][InlineData("clock")][InlineData("policy")]
    [InlineData("phase")][InlineData("projection")][InlineData("expired")][InlineData("early")]
    [InlineData("stale")][InlineData("negative-age")][InlineData("no-window")][InlineData("invalid-window")]
    public void New_authoritative_denial_invalidates_old_permission(string reason)
    {
        var clock = new Clock(); var continuation = new StudyDisplayContinuation(clock); var state = State();
        continuation.Observe(state); Assert.True(continuation.CanKeep);
        var denied = reason switch
        {
            "withdrawn" => state with { Display = state.Display! with { Available = false } },
            "unselected" => state with { Display = state.Display! with { Selected = false } },
            "clock" => state with { ClockReady = false },
            "policy" => state with { PolicyState = "DISABLED" },
            "phase" => state with { Phase = "Outside" },
            "projection" => state with { ProjectionPriority = true },
            "expired" => state with { SchoolNow = state.Window!.End.AddSeconds(5) },
            "early" => state with { SchoolNow = state.Window!.Start.AddSeconds(-301) },
            "no-window" => state with { Window = null },
            "invalid-window" => state with { Window = new(state.Window!.End, state.Window.Start) },
            _ => state
        };
        continuation.Observe(denied, reason == "stale" ? TimeSpan.FromSeconds(1.5) : reason == "negative-age" ? TimeSpan.FromTicks(-1) : default);
        Assert.False(continuation.CanKeep);
    }
    [Fact]
    public void Fresh_shorter_window_replaces_deadline_and_preparation_and_end_are_bounded()
    {
        var clock = new Clock(); var continuation = new StudyDisplayContinuation(clock); var state = State();
        continuation.Observe(state); clock.Ms = 1000;
        state = state with { Window = new(state.Window!.Start, state.SchoolNow!.Value.AddSeconds(10)) };
        continuation.Observe(state); clock.Ms += 14999; Assert.True(continuation.CanKeep);
        clock.Ms++; Assert.False(continuation.CanKeep);
        continuation.Observe(state with { Phase = "Preparing", SchoolNow = state.Window.Start.AddMinutes(-5) }); Assert.True(continuation.CanKeep);
        clock.Ms += 300000; Assert.True(continuation.CanKeep); // Bridge an ordinary phase-change poll without flicker.
        clock.Ms += 5000; Assert.False(continuation.CanKeep);
        continuation.Observe(state with { Phase = "Ended", SchoolNow = state.Window.End.AddSeconds(4) }); Assert.True(continuation.CanKeep);
        clock.Ms += 1000; Assert.False(continuation.CanKeep);
    }
}

using NPEduTools.Core;

namespace NPEduTools.Tests;

public sealed class StudyIdleReturnTests
{
    private sealed class Clock : TimeProvider
    {
        public long Ms;
        public override long TimestampFrequency => 1000;
        public override long GetTimestamp() => Ms;
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.MaxValue; // Wall time cannot decide idle return.
    }
    [Fact]
    public void Activity_in_another_application_restarts_idle_without_restarting_the_hide()
    {
        var clock = new Clock(); long lastInput = 0;
        var timer = new StudyIdleReturn(clock, () => TimeSpan.FromMilliseconds(clock.Ms - lastInput));
        timer.Start(600); clock.Ms = 599000; Assert.Equal(1, timer.Remaining);
        lastInput = clock.Ms; clock.Ms = 600000; Assert.Equal(599, timer.Remaining);
        clock.Ms = 1198000; Assert.Equal(1, timer.Remaining);
        clock.Ms = 1199000; Assert.Equal(0, timer.Remaining);
        lastInput = clock.Ms; Assert.Equal(0, timer.Remaining); // Returned display never hides itself again.
    }
    [Fact]
    public void Idle_before_hide_does_not_make_the_display_immediately_return()
    {
        var clock = new Clock { Ms = 7200000 };
        var timer = new StudyIdleReturn(clock, () => TimeSpan.FromHours(2));
        timer.Start(600); Assert.Equal(600, timer.Remaining);
        clock.Ms += 599999; Assert.Equal(.001, timer.Remaining, 3);
        clock.Ms++; Assert.Equal(0, timer.Remaining);
    }
    [Theory]
    [InlineData(null)] [InlineData(-1d)]
    public void Missing_or_invalid_idle_information_does_not_force_return(double? seconds)
    {
        var clock = new Clock(); TimeSpan? idle = seconds is { } s ? TimeSpan.FromSeconds(s) : null;
        var timer = new StudyIdleReturn(clock, () => idle);
        timer.Start(600); clock.Ms = 3600000;
        Assert.Equal(600, timer.Remaining); Assert.False(timer.Ready);
        idle = TimeSpan.FromMinutes(9); Assert.Equal(60, timer.Remaining); Assert.True(timer.Ready);
        idle = TimeSpan.FromMinutes(10); Assert.Equal(0, timer.Remaining);
    }
    [Theory]
    [InlineData(60)] [InlineData(120)] [InlineData(600)] [InlineData(10800)]
    public void Manual_custom_intervals_use_the_same_idle_algorithm(int seconds)
    {
        var clock = new Clock(); var timer = new StudyIdleReturn(clock, () => TimeSpan.FromMilliseconds(clock.Ms));
        timer.Start(seconds); clock.Ms = seconds * 1000L - 1; Assert.True(timer.Remaining > 0);
        clock.Ms++; Assert.Equal(0, timer.Remaining);
    }
    [Fact]
    public void Native_tick_wrap_is_decoded_as_recent_input()
    {
        var input = new DesktopInactivity(() => (true, uint.MaxValue - 999), () => (long)uint.MaxValue + 1001);
        Assert.Equal(TimeSpan.FromSeconds(2), input.Read());
    }
    [Fact]
    public void Non_increasing_native_input_timestamps_still_restart_observed_inactivity()
    {
        long now = 2000000; uint last = 1990000;
        var input = new DesktopInactivity(() => (true, last), () => now);
        Assert.Equal(TimeSpan.FromSeconds(10), input.Read());
        last = 1980000; Assert.Equal(TimeSpan.Zero, input.Read());
        now += 1000; Assert.Equal(TimeSpan.FromSeconds(1), input.Read());
    }
    [Fact]
    public void Failed_or_future_native_input_observations_are_unknown()
    {
        Assert.Null(new DesktopInactivity(() => (false, 0), () => 1000).Read());
        Assert.Null(new DesktopInactivity(() => (true, 1001), () => 1000).Read());
    }
    [Fact]
    public void New_input_after_wrapping_restarts_idle_reading()
    {
        long now = uint.MaxValue - 1000L; uint tick = uint.MaxValue - 2000;
        var input = new DesktopInactivity(() => (true, tick), () => now);
        Assert.Equal(TimeSpan.FromSeconds(1), input.Read());
        now = (long)uint.MaxValue + 1001; tick = 500;
        Assert.Equal(TimeSpan.Zero, input.Read());
    }
}

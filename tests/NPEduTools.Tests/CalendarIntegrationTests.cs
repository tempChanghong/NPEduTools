using NPEduTools.Contracts;

namespace NPEduTools.Tests;

public sealed class CalendarIntegrationTests
{
    private static string Pipe() => "NPEduTools.Test.calendar." + Guid.NewGuid().ToString("N");
    [Fact]
    public async Task ForecastUsesSchoolDateAndNeverReplacesLiveClock()
    {
        string upstream = Pipe(), pipe = Pipe();
        await using var peer = await TestProcess.StartAsync("NPEduTools.ClassIsland.TestPeer", true, upstream, "bridge");
        await using var host = await TestProcess.StartAsync("NPEduTools.Host", false, "--pipe", pipe, "--classisland-pipe", upstream);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        foreach (int offset in new[] { 0, 1, 30, -1, 31 })
        {
            var date = new DateOnly(2031, 4, 7).AddDays(offset);
            var response = await HostClient.RequestAsync(pipe, new HostRequest(Protocol.Version, Guid.NewGuid(), "classisland.day-plan", 8000, SchoolDate: date), deadline.Token);
            if (offset is < 0 or > 30)
            {
                Assert.Equal("Unavailable", response.Outcome); Assert.Equal("CalendarOutOfRange", response.ErrorCode); Assert.Null(response.Forecast);
                continue;
            }
            Assert.Equal("Succeeded", response.Outcome);
            var forecast = Assert.IsType<DayForecast>(response.Forecast);
            Assert.Equal(new DateOnly(2031, 4, 7), forecast.SchoolDate); Assert.Equal(date, forecast.RequestedDate);
            Assert.True(forecast.Forecast); Assert.False(forecast.Schedule.ClockVerified);
            Assert.Equal(date, forecast.Schedule.Date); Assert.Null(response.SchoolClock);
            Assert.All(forecast.Schedule.Lessons, l => Assert.Equal(date, DateOnly.FromDateTime(l.Start.Date)));
        }
        HostResponse clock;
        do
        {
            clock = await HostClient.RequestAsync(pipe, new HostRequest(Protocol.Version, Guid.NewGuid(), "classisland.school-clock"), deadline.Token);
            if (clock.SchoolClock?.Schedule is null) await Task.Delay(100, deadline.Token);
        } while (clock.SchoolClock?.Schedule is null);
        Assert.Equal(new DateOnly(2031, 4, 7), clock.SchoolClock.Schedule.Date);
        Assert.Equal("预演数学", clock.SchoolClock.Schedule.Lessons[0].Subject);
    }
    [Fact]
    public void QueryRequiresAnExplicitCalendarDateAndForbidsObservation()
    {
        var request = new HostRequest(Protocol.Version, Guid.NewGuid(), "classisland.day-plan");
        Assert.NotNull(Protocol.Validate(request));
        Assert.Null(Protocol.Validate(request with { SchoolDate = new(2031, 4, 8) }));
        Assert.NotNull(Protocol.Validate(request with { SchoolDate = new(2031, 4, 8), ObserveMs = 10 }));
        Assert.NotNull(Protocol.Validate(request with { Capability = "classisland.school-clock", SchoolDate = new(2031, 4, 8) }));
    }
}

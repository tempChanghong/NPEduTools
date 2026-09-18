using NPEduTools.Contracts;

namespace NPEduTools.Tests;

public sealed class ScheduleIntegrationTests
{
    private static string Pipe() => "NPEduTools.Test.schedule." + Guid.NewGuid().ToString("N");
    private static async Task<HostResponse> Read(string pipe)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        return await HostClient.RequestAsync(pipe, new HostRequest(Protocol.Version, Guid.NewGuid(), "classisland.schedule", 10000), deadline.Token);
    }
    [Theory]
    [InlineData("schedule", true)] [InlineData("schedule-clock", false)]
    public async Task PublicProfileIpcProducesBoundedMappedDailySchedule(string mode, bool clock)
    {
        string upstream = Pipe(), host = Pipe();
        await using var peer = await TestProcess.StartAsync("NPEduTools.ClassIsland.TestPeer", true, upstream, mode);
        await using var server = await TestProcess.StartAsync("NPEduTools.Host", false, "--pipe", host, "--classisland-pipe", upstream);
        var response = await Read(host);
        Assert.Equal("Succeeded", response.Outcome); Assert.NotNull(response.Schedule);
        Assert.Equal(clock, response.Schedule.ClockVerified);
        Assert.Equal(2, response.Schedule.Lessons.Length);
        Assert.Equal("预演数学", response.Schedule.Lessons[0].Subject);
        Assert.Equal(2, response.Schedule.Lessons[1].Number);
        Assert.False(response.Schedule.Lessons[1].Enabled);
        Assert.NotEqual(Guid.Empty, response.Schedule.PlanId);
        Assert.Equal(response.Schedule.Revision, (await Read(host)).Schedule!.Revision);
    }
    [Fact]
    public async Task DeletedSubjectIsVisibleButCannotEnterRecordingPlan()
    {
        string upstream = Pipe(), host = Pipe();
        await using var peer = await TestProcess.StartAsync("NPEduTools.ClassIsland.TestPeer", true, upstream, "schedule-undefined");
        await using var server = await TestProcess.StartAsync("NPEduTools.Host", false, "--pipe", host, "--classisland-pipe", upstream);
        var response = await Read(host);
        Assert.Equal("Succeeded", response.Outcome);
        Assert.Equal("未定义科目", response.Schedule!.Lessons[0].Subject);
        Assert.False(response.Schedule.Lessons[0].Enabled);
        Assert.DoesNotContain(NPEduTools.Core.RecordingPlanner.Build(response.Schedule, new()), p => p.Selected);
    }
    [Fact]
    public async Task TemporarySubjectSwitchReplacesSnapshotWithoutChangingOccurrenceTimes()
    {
        string upstream = Pipe(), host = Pipe();
        await using var peer = await TestProcess.StartAsync("NPEduTools.ClassIsland.TestPeer", true, upstream, "schedule-switch");
        await using var server = await TestProcess.StartAsync("NPEduTools.Host", false, "--pipe", host, "--classisland-pipe", upstream);
        var first = (await Read(host)).Schedule!;
        Assert.Equal("预演数学", first.Lessons[0].Subject);
        await Task.Delay(3200);
        var next = (await Read(host)).Schedule!;
        Assert.Equal("临时英语", next.Lessons[0].Subject);
        Assert.NotEqual(first.Revision, next.Revision);
        Assert.Equal(first.Lessons[0].Start, next.Lessons[0].Start);
    }
    [Fact]
    public async Task MissingClassIslandReturnsNoScheduleAndHostStaysAlive()
    {
        string host = Pipe();
        await using var server = await TestProcess.StartAsync("NPEduTools.Host", false, "--pipe", host, "--classisland-pipe", Pipe());
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        var result = await HostClient.RequestAsync(host, new HostRequest(Protocol.Version, Guid.NewGuid(), "classisland.schedule", 400), deadline.Token);
        Assert.Equal("TimedOut", result.Outcome); Assert.Null(result.Schedule);
        Assert.Equal("Succeeded", (await HostClient.RequestAsync(host, "host.ping", deadline.Token)).Outcome);
    }
}

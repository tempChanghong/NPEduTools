using NPEduTools.Contracts;

namespace NPEduTools.Tests;

public sealed partial class StudyExitTests
{
    [Theory]
    [InlineData("clock", false)]
    [InlineData("clock", true)]
    [InlineData("session", false)]
    [InlineData("session", true)]
    [InlineData("version", false)]
    [InlineData("version", true)]
    [InlineData("ended", false)]
    [InlineData("ended", true)]
    public async Task ContextLossDuringFailureRecordingDoesNotThrowOrAccuseAnotherSession(string change, bool wrongPassword)
    {
        await using var f = new Fixture();
        var id = Guid.NewGuid();
        Assert.Equal("Waiting", f.Guard.Begin(f.Begin(id)).StudyExit!.State);
        f.Time.Ms = wrongPassword ? 1000 : 30000;
        var valid = f.State;
        var changed = change switch
        {
            "clock" => valid with { ClockReady = false, SchoolNow = null },
            "session" => valid with { Execution = valid.Execution! with { SessionId = Guid.NewGuid() } },
            "version" => valid with { Version = new string('b', 64) },
            _ => valid with { Phase = "Outside", Window = null }
        };
        int samples = 0;
        // The first Tick sees a valid attempt. The next sample, obtained while
        // constructing the failure record, belongs to an ineligible context.
        f.ExitSample = () => ++samples == 1 ? valid : changed;
        var error = Record.Exception(() =>
        {
            if (wrongPassword) f.Guard.ObserveAuthorization(f.Authorize(id),
                new(1, Guid.NewGuid(), "Rejected", "MANAGEMENT_SECRET_INVALID", "synthetic refusal"));
            else f.Guard.Tick();
        });
        Assert.Null(error);
        Assert.Equal("Invalidated", f.Guard.Snapshot().State);
        Assert.Null(f.Guard.PendingExit());
        Assert.Null(f.Journal.Next("scope"));
        Assert.Equal(0, f.Resumed);
        f.ExitSample = () => valid;
        var next = Guid.NewGuid();
        Assert.Equal("Waiting", f.Guard.Begin(f.Begin(next)).StudyExit!.State);
        f.Time.Ms += 30000;
        Assert.Equal("Failed", f.Guard.Snapshot().State);
        Assert.Equal("Timeout", f.Guard.Snapshot().Reason);
        Assert.Equal(next.ToString(), f.Guard.PendingExit()!.Metadata["eventId"]!.GetValue<string>());
    }
}

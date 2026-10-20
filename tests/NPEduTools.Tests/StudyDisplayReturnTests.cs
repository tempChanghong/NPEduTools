using NPEduTools.App;
using NPEduTools.Contracts;

namespace NPEduTools.Tests;

public sealed class StudyDisplayReturnTests
{
    private sealed class Clock : TimeProvider
    {
        public long Ms;
        public override long TimestampFrequency => 1000;
        public override long GetTimestamp() => Ms;
    }
    private static StudyHostState Hidden() => new(new string('a', 64), "APPLIED", "HIDDEN", "ACTIVE", "Active", "WINDOW_ACTIVE", true,
        DateTimeOffset.UtcNow, new(DateTimeOffset.UtcNow.AddMinutes(-10), DateTimeOffset.UtcNow.AddMinutes(10)), null, null,
        Display: new(Guid.NewGuid(), true, false, 600));
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void Return_uses_current_display_identity_and_expires_without_a_fresh_poll(bool manual)
    {
        var clock = new Clock(); var action = new StudyDisplayReturn(clock); var state = Hidden(); action.Observe(state);
        var request = action.Request(manual)!; Assert.Null(Protocol.Validate(request));
        Assert.Equal(state.Window, request.StudyHide!.Window); Assert.Equal(state.Version, request.StudyHide.Version);
        clock.Ms = 3500; Assert.Null(action.Request(manual));
        action.Observe(state with { ProjectionPriority = true }); Assert.Null(action.Request(manual));
        action.Observe(state with { Phase = "Ended" }); Assert.Null(action.Request(manual));
        action.Observe(state with { ClockReady = false }); Assert.Null(action.Request(manual));
        action.Observe(null); Assert.Null(action.Request(manual));
    }
}

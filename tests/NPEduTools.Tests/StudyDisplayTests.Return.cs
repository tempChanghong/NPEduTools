using NPEduTools.Contracts;
using NPEduTools.Host;

namespace NPEduTools.Tests;

public sealed partial class StudyDisplayTests
{
    private static HostRequest Show(StudyHostState state, Guid? request = null) => Hide(state, request) with { Capability = "study.show" };
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void Explicit_return_clears_only_the_current_hide_and_keeps_capture_unchanged(bool manual)
    {
        var state = State();
        if (manual) state = state with { Manual = new(Guid.NewGuid(), 1, state.Execution!.SessionId, "Active", null, null, null, false, null) };
        var service = new StudyDisplayService(() => ("scope", true, state));
        var hidden = service.Handle(Hide(service.Snapshot())).Study!;
        Assert.True(hidden.Display!.HiddenRemainingSeconds > 0);
        var command = Show(hidden); Assert.Null(Protocol.Validate(command));
        Assert.Null(Protocol.Validate(command with { Capability = "manual.study.show" }));
        var shown = service.Handle(command);
        Assert.Equal("Succeeded", shown.Outcome);
        Assert.True(shown.Study!.Display!.Available); Assert.Equal(0, shown.Study.Display.HiddenRemainingSeconds);
        Assert.Same(state.Execution, shown.Study.Execution);
        // Retrying the old request must not cancel a later, deliberate temporary-use request.
        service.Handle(Hide(service.Snapshot()));
        Assert.True(service.Handle(command).Study!.Display!.HiddenRemainingSeconds > 0);
    }
    [Fact]
    public void Return_rejects_old_window_identity_and_respects_exam_projection_priority()
    {
        var state = State(); bool projection = false;
        var service = new StudyDisplayService(() => ("scope", true, state), projection: () => projection);
        var command = Show(service.Handle(Hide(service.Snapshot())).Study!);
        projection = true;
        Assert.Equal("Rejected", service.Handle(command).Outcome);
        Assert.True(service.Snapshot().Display!.HiddenRemainingSeconds > 0);
        projection = false; state = state with { Version = new string('b', 64) };
        Assert.Equal("STUDY_STATE_CHANGED", service.Handle(command).ErrorCode);
        Assert.True(service.Snapshot().Display!.HiddenRemainingSeconds > 0);
    }
    [Fact]
    public void Display_payload_is_still_rejected_on_unrelated_capabilities()
    {
        var service = new StudyDisplayService(() => ("scope", true, State()));
        var command = Show(service.Snapshot());
        Assert.NotNull(Protocol.Validate(command with { Capability = "study.status" }));
        Assert.NotNull(Protocol.Validate(command with { StudyHide = null }));
    }
}

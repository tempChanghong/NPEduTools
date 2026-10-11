using NPEduTools.Contracts;
using NPEduTools.Host;

namespace NPEduTools.Tests;

public sealed class StudyPresentationTests
{
    private sealed class Clock : TimeProvider
    { public long Ms; public override long TimestampFrequency => 1000; public override long GetTimestamp() => Ms; }
    private readonly Clock _time = new();
    private StudyHostState _state;
    private string _scope = "one";
    private readonly StudyDisplayService _service;
    public StudyPresentationTests()
    {
        var start = new DateTimeOffset(2026,10,12,19,0,0,TimeSpan.Zero);
        _state = new(new string('a',64),"APPLIED","NOT_INTEGRATED","ACTIVE","Active","WINDOW_ACTIVE",true,start.AddSeconds(20),
            new(start,start.AddMinutes(150)),null,new(new("Asia/Shanghai","2026-10-12",null,[],[]),new("StatusTrend",180),
                new(false,"OwnClass",[],60),new(true,-40,-50,600,5),new(false,5,30,30,true,false)));
        _service = new(() => (_scope,true,_state),_time);
    }
    private HostRequest Pulse(string shown = "Visible", Guid? app = null, long sequence = 1)
    {
        var state = _service.Snapshot();
        return new(1,Guid.NewGuid(),"study.presentation",StudyPresentation:new(state.Display!.InstanceId,app ?? Guid.NewGuid(),sequence,
            state.Version!,state.Window!,state.Phase,shown,shown == "Visible" ? 100 : null));
    }
    private string Shown => _service.ObservePresentation()!["status"]!["state"]!.GetValue<string>();
    [Fact]
    public void Study_eligibility_never_claims_visible_and_frame_heartbeat_expires()
    {
        Assert.True(_service.Snapshot().Display!.Available);Assert.Equal("Unknown",Shown);
        var pulse = Pulse(); Assert.Null(Protocol.Validate(pulse));Assert.False(Protocol.NoiseInterruption(pulse));
        Assert.Equal("Succeeded",_service.Handle(pulse).Outcome);Assert.Equal("Visible",Shown);
        _time.Ms=4899;Assert.Equal("Visible",Shown);_time.Ms=4900;Assert.Equal("Unknown",Shown);
        Assert.Equal("Suppressed",_service.Handle(pulse).Outcome);Assert.Equal("Unknown",Shown);
    }
    [Fact]
    public void Study_hidden_App_does_not_replace_another_fresh_visible_App()
    {
        var visible=Pulse();_service.Handle(visible);_time.Ms=1000;_service.Handle(Pulse("Hidden"));Assert.Equal("Visible",Shown);
        _time.Ms=4900;Assert.Equal("Hidden",Shown);_time.Ms=6000;Assert.Equal("Unknown",Shown);
    }
    [Fact]
    public void Study_same_App_hidden_or_stale_with_new_sequence_withdraws_visible()
    {
        var app=Guid.NewGuid();_service.Handle(Pulse(app:app));_service.Handle(Pulse("Hidden",app,2));Assert.Equal("Hidden",Shown);
        Assert.Equal("STUDY_SEQUENCE_CONFLICT",_service.Handle(Pulse(app:app)).ErrorCode);
        _service.Handle(Pulse("Stale",app,3));Assert.Equal("Unknown",Shown);
    }
    [Theory]
    [InlineData("scope")] [InlineData("version")] [InlineData("window")] [InlineData("phase")] [InlineData("clock")]
    public void Study_old_presentation_is_fenced_by_current_scope_and_stage(string change)
    {
        var pulse=Pulse();_service.Handle(pulse);
        switch(change)
        {
            case "scope":_scope="two";break;
            case "version":_state=_state with {Version=new string('b',64)};break;
            case "window":_state=_state with {Window=new(_state.Window!.Start.AddMinutes(10),_state.Window.End)};break;
            case "phase":_state=_state with {Phase="Preparing"};break;
            case "clock":_state=_state with {ClockReady=false,Phase="Blocked",Reason="SCHOOL_CLOCK_UNAVAILABLE"};break;
        }
        Assert.NotEqual("Visible",Shown);Assert.Equal("Rejected",_service.Handle(pulse).Outcome);
    }
    [Fact]
    public void Study_hide_lease_is_not_proof_of_hidden_and_does_not_change_capture()
    {
        var pulse=Pulse();_service.Handle(pulse);var state=_service.Snapshot();
        _service.Handle(new(1,Guid.NewGuid(),"study.hide",StudyHide:new(state.Display!.InstanceId,state.Version!,state.Window!)));
        Assert.Equal("Unknown",Shown);Assert.Equal("Rejected",_service.Handle(Pulse()).Outcome);
        Assert.Equal("WAITING_HIDE_CONFIRMATION",_service.ObservePresentation()!["status"]!["reason"]!.GetValue<string>());
        _service.Handle(Pulse("Hidden",pulse.StudyPresentation!.AppInstanceId,2));Assert.Equal("Hidden",Shown);
        Assert.Equal("ACTIVE",_service.Snapshot().CaptureState);
    }
    [Fact]
    public void Study_presentation_clients_are_bounded_without_recycling_replay_tombstones()
    {
        for(int i=0;i<16;i++)Assert.Equal("Succeeded",_service.Handle(Pulse("Hidden")).Outcome);
        Assert.Equal("STUDY_CLIENT_LIMIT",_service.Handle(Pulse("Hidden")).ErrorCode);
    }
    [Fact]
    public void Study_next_window_reclaims_clients_and_reconnection_cannot_replay_an_old_epoch()
    {
        var old=Pulse();_service.Handle(old);
        for(int i=1;i<16;i++)_service.Handle(Pulse("Hidden"));
        _state=_state with {Window=new(_state.Window!.Start.AddDays(1),_state.Window.End.AddDays(1)),SchoolNow=_state.SchoolNow!.Value.AddDays(1)};
        Assert.Equal("Succeeded",_service.Handle(Pulse()).Outcome);Assert.Equal("Rejected",_service.Handle(old).Outcome);
        var disconnected=Pulse();_state=_state with {Version=null,PolicyState="UNAVAILABLE"};_service.Snapshot();
        _state=_state with {Version=new string('a',64),PolicyState="APPLIED"};
        Assert.Equal("Rejected",_service.Handle(disconnected).Outcome);Assert.Equal("Unknown",Shown);
    }
    [Theory]
    [InlineData(-1)] [InlineData(1000)] [InlineData(5000)]
    public void Study_invalid_render_age_cannot_claim_visible(int age)
    {
        var request=Pulse();request=request with {StudyPresentation=request.StudyPresentation! with {RenderAgeMs=age}};
        Assert.NotNull(Protocol.Validate(request));Assert.Equal("Rejected",_service.Handle(request).Outcome);
    }
}

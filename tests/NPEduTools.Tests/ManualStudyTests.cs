using System.Text.Json;
using NPEduTools.Contracts;
using NPEduTools.Core;
using NPEduTools.Host;
namespace NPEduTools.Tests;

public sealed class ManualStudyTests
{
    private sealed class Clock:TimeProvider
    {
        public long Ms;public DateTimeOffset Base=DateTimeOffset.Parse("2026-10-11T10:00:00Z");
        public override long TimestampFrequency=>1000;public override long GetTimestamp()=>Ms;public override DateTimeOffset GetUtcNow()=>Base.AddMilliseconds(Ms);
        public override ITimer CreateTimer(TimerCallback c,object? s,TimeSpan d,TimeSpan p)=>new Timer();
        private sealed class Timer:ITimer{public bool Change(TimeSpan d,TimeSpan p)=>true;public void Dispose(){}public ValueTask DisposeAsync()=>ValueTask.CompletedTask;}
    }
    private sealed class Capture:INoiseCapture
    {
        public string DeviceName=>"Synthetic; no microphone";public bool Disposed;
        public event Action<NoiseFrame>? Frame;public event Action<string>? Failed{add{}remove{}}
        public void Start(){}public void Dispose()=>Disposed=true;
        public void Sample()=>Frame?.Invoke(new(.1,.0001,.01,0,false));
    }
    private sealed class Fixture:IAsyncDisposable
    {
        public readonly Clock Time=new();public readonly List<Capture> Captures=[];
        public readonly string Folder=Path.Combine(Path.GetTempPath(),"manual-study-test-"+Guid.NewGuid());
        public bool Blocked=false;public int Lead=120;public long LastInput;public Func<int>? ReadLead;public readonly NoiseService Noise;public readonly ManualStudyService Manual;
        public (string? Scope, bool Selected, StudyHostState State) School = (null, false,
            new(null,"UNAVAILABLE","","","Outside","",false,null,null,null,null));
        public Fixture()
        {
            Noise=new(_=>{var c=new Capture();Captures.Add(c);return c;},()=>[new("mic","Synthetic")],Time);
            Manual=new(Noise,()=>School,()=>Blocked,Folder,Time,false,displayLeadSeconds:()=>ReadLead?.Invoke()??Lead,
                inactivity:()=>TimeSpan.FromMilliseconds(Time.Ms-LastInput));
        }
        public HostResponse Start(DateTimeOffset? start=null,DateTimeOffset? end=null,int? hide=600)
        {var s=Manual.Status();return Manual.Handle(new(1,Guid.NewGuid(),"manual.study.command",ManualStudy:new("start",s.InstanceId,s.Revision,DeviceId:"mic",StartUtc:start,EndUtc:end,HideSeconds:hide)));}
        public HostRequest End(ManualStudyState s)=>new(1,Guid.NewGuid(),"manual.study.command",ManualStudy:new("end",s.InstanceId,s.Revision,s.SessionId));
        public async Task Active(){for(int i=0;i<100;i++){Manual.Tick();if(Noise.Snapshot().State=="Active"){Captures.Last().Sample();Manual.Tick();return;}await Task.Delay(10);}Assert.Fail("Synthetic capture did not start");}
        public async Task Ended(){for(int i=0;i<100 && Manual.Status().State!="Ended";i++)await Task.Delay(10);Assert.Equal("Ended",Manual.Status().State);}
        public async ValueTask DisposeAsync(){await Noise.DisposeAsync();await Manual.DisposeAsync();if(Directory.Exists(Folder)){Assert.Equal(Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar),Path.GetDirectoryName(Path.GetFullPath(Folder)));Directory.Delete(Folder,true);}}
    }
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task Background_manual_study_counts_thresholds_without_display_or_photography(bool reserved)
    {
        await using var f = new Fixture();
        var policy = new StudyNoise(true, -45, -55, 600, 3);
        var settings = new StudySettings(new("Asia/Shanghai", "2026-10-01", null, [], []), new("StatusTrend", 600),
            new(false, "OwnClass", [], 60), policy, new(false, 3, 30, 30, true, false));
        f.School = ("synthetic-class", true, new("version-a", "APPLIED", "", "", "Outside", "OUTSIDE_WINDOW", true,
            null, null, null, settings));
        var start = f.Time.GetUtcNow().AddMinutes(reserved ? 5 : 0);
        Assert.Equal("Succeeded", f.Start(start, start.AddMinutes(1)).Outcome);
        if (reserved) { Assert.Empty(f.Captures); f.Time.Ms += 300000; }
        await f.Active();
        var before = f.Noise.StudyReportSnapshot()!;
        // Drive only Host ticks and synthetic frames; never read Display or use a photo observer.
        for (int i = 0; i < 50; i++) { f.Time.Ms += 100; f.Captures[0].Sample(); f.Manual.Tick(); }
        var after = f.Noise.StudyReportSnapshot()!;
        Assert.Equal(5, after.Raw.ThresholdSampledSeconds - before.Raw.ThresholdSampledSeconds, 6);
        Assert.Equal(5, after.Raw.HighSeconds - before.Raw.HighSeconds, 6);
        Assert.Equal(5, after.Raw.MediumSeconds - before.Raw.MediumSeconds, 6);
        f.School = f.School with { State = f.School.State with { Version = "version-b",
            Settings = settings with { Noise = policy with { RedLineDbfs = -30 } } } };
        f.Manual.Tick();
        for (int i = 0; i < 10; i++) { f.Time.Ms += 100; f.Captures[0].Sample(); f.Manual.Tick(); }
        var updated = f.Noise.StudyReportSnapshot()!;
        Assert.Equal(after.Raw.HighSeconds, updated.Raw.HighSeconds, 6);
        Assert.Equal(1, updated.Raw.MediumSeconds - after.Raw.MediumSeconds, 6);
        Assert.Single(f.Captures); Assert.Equal("Active", f.Noise.Snapshot().State);
        Assert.Equal("Succeeded", f.Manual.Handle(f.End(f.Manual.Status())).Outcome); await f.Ended();
        var report = f.Manual.Report().Data!;
        Assert.True(report.Complete); Assert.Equal("dBFS", report.Raw.Unit);
        Assert.Equal(after.Raw.HighSeconds, report.Raw.HighSeconds, 6);
    }

    [Fact]
    public async Task Consecutive_manual_reports_keep_Host_identity_but_have_distinct_session_versions()
    {
        await using var f = new Fixture();
        async Task<StudyReportState> Finish()
        {
            Assert.Equal("Succeeded", f.Start().Outcome); await f.Active();
            f.Time.Ms += 6000; f.Captures.Last().Sample(); f.Manual.Tick();
            Assert.Equal("Succeeded", f.Manual.Handle(f.End(f.Manual.Status())).Outcome); await f.Ended();
            f.Time.Ms += 5000;
            var report = f.Manual.Report(); Assert.True(report.Available); return report;
        }
        var first = await Finish(); var second = await Finish();
        Assert.Equal(first.InstanceId, second.InstanceId);
        Assert.NotEqual(first.Version, second.Version);
        Assert.NotEqual(first.Data!.Window, second.Data!.Window);
    }

    [Fact]
    public async Task Immediate_start_shares_noise_device_has_no_planned_progress_and_ends_with_a_saved_report()
    {
        await using var f=new Fixture();Assert.Equal("Succeeded",f.Start().Outcome);await f.Active();
        Assert.False(f.Noise.Protection().Protected);var state=f.Manual.Display.Snapshot();Assert.NotNull(state.Manual);Assert.False(state.Manual.HasPlannedEnd);Assert.Equal("Manual",state.Execution!.Owner);Assert.Null(state.Seating);
        f.Time.Ms=6000;f.Captures.Last().Sample();f.Manual.Tick();var end=f.End(f.Manual.Status());Assert.Null(Protocol.Validate(end));Assert.Equal("Succeeded",f.Manual.Handle(end).Outcome);await f.Ended();
        f.Time.Ms+=5000;Assert.True(f.Manual.Report().Available);Assert.True(f.Manual.Report().Data!.Complete);
        var file=Assert.Single(Directory.GetFiles(f.Folder,"*.json"));Assert.Contains("Manual",File.ReadAllText(file));Assert.Contains("dBFS",File.ReadAllText(file));
        f.Time.Ms+=300000;Assert.False(f.Manual.Report().Available);
    }
    [Fact]
    public async Task Future_reservation_prepares_two_minutes_before_start_and_does_not_open_microphone_early()
    {
        await using var f=new Fixture();var start=f.Time.GetUtcNow().AddMinutes(5);Assert.Equal("Succeeded",f.Start(start,start.AddMinutes(30)).Outcome);
        Assert.Equal("Waiting",f.Manual.Status().State);Assert.Empty(f.Captures);
        f.Time.Ms=180000;f.Manual.Tick();Assert.Equal("Preparing",f.Manual.Display.Snapshot().Phase);Assert.Empty(f.Captures);
        f.Time.Ms=300000;f.Manual.Tick();await f.Active();Assert.Single(f.Captures);
    }
    [Fact]
    public async Task Local_lead_is_frozen_per_reservation_and_only_presentation_starts_early()
    {
        await using var f=new Fixture();f.Lead=300;var start=f.Time.GetUtcNow().AddMinutes(8);f.Start(start,start.AddMinutes(30));f.Lead=0;
        f.Time.Ms=179999;f.Manual.Tick();Assert.Equal("Waiting",f.Manual.Status().State);
        f.Time.Ms=180000;f.Manual.Tick();Assert.Equal("Preparing",f.Manual.Status().State);Assert.Empty(f.Captures);Assert.Equal(300,f.Manual.Status().DisplayLeadSeconds);
        f.Time.Ms=480000;f.Manual.Tick();await f.Active();Assert.Single(f.Captures);
    }
    [Fact]
    public async Task Reservation_inside_lead_opens_immediately_with_short_preparation_but_no_early_sensor()
    {
        await using var f=new Fixture();var start=f.Time.GetUtcNow().AddSeconds(3);f.Start(start);Assert.Equal("Preparing",f.Manual.Status().State);
        Assert.Equal(3,f.Manual.Status().DisplayLeadSeconds);Assert.Empty(f.Captures);
        f.Time.Ms=3000;f.Manual.Tick();await f.Active();Assert.Single(f.Captures);
    }
    [Theory]
    [InlineData("range")][InlineData("version")][InlineData("size")][InlineData("json")]
    public async Task Invalid_local_preference_rejects_start_without_throwing_or_touching_device_and_preserves_file(string kind)
    {
        await using var f=new Fixture();Directory.CreateDirectory(f.Folder);
        string path=Path.Combine(f.Folder,"preferences.json");
        string content=kind switch {"range"=>"{\"Version\":1,\"DisplayLeadSeconds\":-1}","version"=>"{\"Version\":2,\"DisplayLeadSeconds\":120}","size"=>new string(' ',4097),_=>"{"};
        File.WriteAllText(path,content);f.ReadLead=()=>new ManualStudyPreferencesStore(path).Read().DisplayLeadSeconds;
        var reply=f.Start();Assert.Equal("Rejected",reply.Outcome);Assert.Equal("MANUAL_PREFERENCES_UNAVAILABLE",reply.ErrorCode);
        Assert.Equal("Idle",f.Manual.Status().State);Assert.Empty(f.Captures);Assert.Equal(content,File.ReadAllText(path));
        new ManualStudyPreferencesStore(path).Save(new(DisplayLeadSeconds:90));
        Assert.Equal("Succeeded",f.Start(f.Time.GetUtcNow().AddMinutes(5)).Outcome);Assert.Equal(90,f.Manual.Status().DisplayLeadSeconds);Assert.Empty(f.Captures);
    }
    [Theory]
    [InlineData(0)][InlineData(-1)][InlineData(181)]
    public async Task End_must_follow_start_and_arrangement_cannot_exceed_three_hours(int minutes)
    {await using var f=new Fixture();Assert.Equal("MANUAL_TIME_INVALID",f.Start(end:f.Time.GetUtcNow().AddMinutes(minutes)).ErrorCode);Assert.Empty(f.Captures);}
    [Fact]
    public async Task Blank_end_stops_at_three_hour_limit_using_monotonic_time()
    {
        await using var f=new Fixture();f.Start();await f.Active();f.Time.Ms=10800000;f.Manual.Tick();await f.Ended();Assert.Equal("ThreeHourLimit",f.Manual.Status().Reason);Assert.True(f.Captures[0].Disposed);
    }
    [Theory]
    [InlineData(600)][InlineData(null)]
    public async Task Hide_is_session_bound_and_keeps_capture_alive_without_renewing_on_duplicates(int? seconds)
    {
        await using var f=new Fixture();f.Start(hide:seconds);await f.Active();var s=f.Manual.Display.Snapshot();
        var request=new HostRequest(1,Guid.NewGuid(),"manual.study.hide",StudyHide:new(s.Display!.InstanceId,s.Version!,s.Window!));Assert.Null(Protocol.Validate(request));
        var hidden=f.Manual.Display.Handle(request with{Capability="study.hide"}).Study!;Assert.False(hidden.Display!.Available);Assert.Equal(seconds??10801,hidden.Display.HiddenRemainingSeconds);
        f.Time.Ms=1000;f.Captures.Last().Sample();var duplicate=f.Manual.Display.Handle(request with{Capability="study.hide"}).Study!;Assert.Equal(seconds is null?10801:599,duplicate.Display!.HiddenRemainingSeconds);
        Assert.Equal("Active",f.Noise.Snapshot().State);f.Time.Ms=601000;f.Captures.Last().Sample();Assert.Equal(seconds is not null,f.Manual.Display.Snapshot().Display!.Available);
    }
    [Fact]
    public async Task Scheduled_takeover_saves_manual_report_and_old_end_cannot_stop_new_scheduled_capture()
    {
        await using var f=new Fixture();f.Start();await f.Active();var old=f.End(f.Manual.Status());
        f.Noise.SetScheduledPriority(true);await f.Ended();Assert.Equal("ScheduledTakeover",f.Manual.Status().Reason);Assert.False(f.Manual.Report().Available);
        for(int i=0;i<100 && f.Noise.CaptureBusy;i++)await Task.Delay(10);
        var scheduled=Guid.NewGuid();Assert.Equal("Accepted",f.Noise.StartScheduled(scheduled).Outcome);
        for(int i=0;i<100 && f.Noise.Snapshot().State!="Active";i++)await Task.Delay(10);
        Assert.Equal("MANUAL_STATE_CHANGED",f.Manual.Handle(old).ErrorCode);Assert.Equal(scheduled,f.Noise.Snapshot().SessionId);Assert.Equal("Active",f.Noise.Snapshot().State);
        Assert.Equal(2,f.Captures.Count);Assert.True(f.Captures[0].Disposed);Assert.False(f.Captures[1].Disposed);
    }
    [Theory]
    [InlineData(120)] [InlineData(600)] [InlineData(null)]
    public async Task Manual_desktop_activity_keeps_synthetic_capture_alive_and_delays_only_presentation(int? seconds)
    {
        await using var f=new Fixture();f.Start(hide:seconds);await f.Active();var state=f.Manual.Display.Snapshot();
        f.Manual.Display.Handle(new(1,Guid.NewGuid(),"study.hide",StudyHide:new(state.Display!.InstanceId,state.Version!,state.Window!)));
        f.Time.Ms=700000;f.LastInput=f.Time.Ms;f.Captures.Last().Sample();
        Assert.False(f.Manual.Display.Snapshot().Display!.Available);Assert.Equal("Active",f.Noise.Snapshot().State);
        f.Time.Ms+=(seconds??600)*1000L;f.Captures.Last().Sample();
        Assert.Equal(seconds is not null,f.Manual.Display.Snapshot().Display!.Available);
        Assert.Equal("Active",f.Noise.Snapshot().State);
    }
    [Fact]
    public async Task Command_retry_returns_fresh_noise_and_reusing_id_with_changed_body_is_rejected()
    {
        await using var f=new Fixture();var s=f.Manual.Status();var request=new HostRequest(1,Guid.NewGuid(),"manual.study.command",ManualStudy:new("start",s.InstanceId,s.Revision,DeviceId:"mic"));
        f.Manual.Handle(request);await f.Active();Assert.Equal("Active",f.Manual.Handle(request).Noise!.State);
        Assert.Equal("MANUAL_REQUEST_CONFLICT",f.Manual.Handle(request with{ManualStudy=request.ManualStudy! with{HideSeconds=null}}).ErrorCode);
        Assert.Single(f.Captures);
    }
    [Fact]
    public async Task Graceful_background_exit_saves_manual_report_and_never_restores_capture()
    {
        await using var f=new Fixture();f.Start();await f.Active();f.Time.Ms=6000;f.Captures.Last().Sample();
        await f.Manual.DisposeAsync();Assert.Equal("Ended",f.Manual.Status().State);Assert.Equal("BackgroundExit",f.Manual.Status().Reason);
        Assert.Contains("BackgroundExit",File.ReadAllText(Assert.Single(Directory.GetFiles(f.Folder,"*.json"))));
        Assert.Equal("Manual", f.Manual.Report().Data!.Mode);
        Assert.Equal("Windows", f.Manual.Report().Data!.ClockSource);
    }
    [Fact]
    public async Task Restart_does_not_restore_a_manual_reservation_or_capture()
    {
        await using var f=new Fixture();f.Start(f.Time.GetUtcNow().AddMinutes(5));await using var second=new ManualStudyService(f.Noise,()=>f.Manual.Source(),()=>false,f.Folder,f.Time,false);
        Assert.Equal("Idle",second.Status().State);Assert.Empty(f.Captures);
    }
}

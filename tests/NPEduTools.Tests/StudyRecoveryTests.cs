using System.Text.Json.Nodes;
using NPEduTools.Contracts;
using NPEduTools.Core;
using NPEduTools.Host;
namespace NPEduTools.Tests;

public sealed class StudyRecoveryTests
{
    private sealed class Clock:TimeProvider
    {public long Ms;public override long TimestampFrequency=>1000;public override long GetTimestamp()=>Ms;public override DateTimeOffset GetUtcNow()=>DateTimeOffset.Parse("2026-10-11T12:00:00Z").AddMilliseconds(Ms);}
    private sealed class Capture:INoiseCapture
    {public string DeviceName=>"Synthetic; no microphone";public event Action<NoiseFrame>? Frame;public event Action<string>? Failed{add{}remove{}}public void Start(){}public void Dispose(){}public void Emit()=>Frame?.Invoke(new(.1,.0001,.01,0,false));}
    private sealed class Fixture:IAsyncDisposable
    {
        public readonly Clock Time=new();public readonly Capture Capture=new();public readonly NoiseService Noise;
        public readonly string Folder=Path.Combine(Path.GetTempPath(),"study-recovery-test-"+Guid.NewGuid());
        public readonly GuardFiles Files;public readonly Guid Generation=Guid.NewGuid(),Session=Guid.NewGuid(),Incident=Guid.NewGuid();
        public StudyHostState State;public readonly StudyPhotoService Photos;public StudyRecoveryService Recovery;
        public int Shots,Resumes;public readonly StudyExitJournal Journal;
        public Fixture()
        {
            Files=new(Path.Combine(Folder,"guard"));Noise=new(_=>Capture,()=>[],Time);
            var n=Noise.Snapshot();Noise.Handle(new(1,Guid.NewGuid(),"noise.command",Noise:new("select",n.InstanceId,n.Revision,"mic")));Noise.StartScheduled(Session);
            var wall=Time.GetUtcNow().AddHours(8);var window=new SchoolNoiseWindow(wall.AddHours(-1),wall.AddMinutes(90));
            var settings=new StudySettings(new("Asia/Shanghai","2026-10-11",null,[],[]),new("StatusTrend",180),new(false,"OwnClass",[],60),new(true,-30,-40,600,3),new(true,3,30,30,true,false));
            State=new(new string('a',64),"APPLIED","","ACTIVE","Active","WINDOW_ACTIVE",true,wall,window,null,settings,
                new("Schedule","Active",Session,"WINDOW_ACTIVE",true,"2026-10-11T20:00:00.000",null,true));
            var devices=new StudyDeviceService(()=>[new("camera","Synthetic")],Folder);var d=devices.Snapshot();devices.Handle(new(1,Guid.NewGuid(),"study.devices.select",StudyCamera:new("select",d.InstanceId,d.Revision,"camera")));
            byte[] Protect(byte[] b,bool enc)=>b.Select(v=>(byte)(v^0x55)).ToArray();
            Photos=new(()=>("scope",true,State),Noise,devices,(_,_)=>{Shots++;return Task.FromResult(new StudyCameraFrame([255,216,255,217],2,2));},new(Path.Combine(Folder,"photos"),Protect,Time),Time,false);Photos.BindPhotos("scope");
            Journal=new(Path.Combine(Folder,"audit"),Protect);Recovery=NewRecovery();
            Files.Write("registration.json",new GuardRegistration(Generation,new(10,100),null));
            Files.Write("recovery.json",new GuardRecoveryEvent(Incident,Generation,new(10,100),new(20,200),Time.GetUtcNow(),Time.GetUtcNow().AddMinutes(90),GuardRecoveryEvent.Key("scope",State.Version!)));
        }
        public StudyRecoveryService NewRecovery(){var r=new StudyRecoveryService(Files,()=>("scope",true,State),Photos,Journal,Time,false,_=>Resumes++);r.BindExit("scope");return r;}
        public async Task Active(){for(int i=0;i<100 && Noise.Snapshot().State!="Active";i++)await Task.Delay(10);Capture.Emit();}
        public void Lease(){var now=Time.GetUtcNow();Photos.ConfirmPhotoLease(new JsonObject{["leaseId"]=Guid.NewGuid().ToString(),["version"]=State.Version,["sessionId"]=Session.ToString(),["leaseSeconds"]=60,["createdAt"]=now.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'"),["expiresAt"]=now.AddSeconds(60).ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'"),["maxBytes"]=262144,["maxEdge"]=1280},TimeSpan.Zero);}
        public void Advance(int ms){Time.Ms+=ms;State=State with{SchoolNow=State.SchoolNow!.Value.AddMilliseconds(ms)};Capture.Emit();}
        public async ValueTask DisposeAsync(){await Recovery.DisposeAsync();await Photos.DisposeAsync();await Noise.DisposeAsync();if(Directory.Exists(Folder)){Assert.Equal(Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar),Path.GetDirectoryName(Path.GetFullPath(Folder)));Directory.Delete(Folder,true);}}
    }
    [Fact]
    public async Task Recovery_waits_for_fresh_capture_and_lease_then_captures_once_with_truthful_warning_and_audit()
    {
        await using var f=new Fixture();await f.Active();f.State=f.State with{Execution=f.State.Execution! with{SignalFresh=false}};f.Recovery.Tick();Assert.NotEqual("Failed",f.Recovery.Snapshot().State);Assert.Equal(0,f.Shots);
        f.State=f.State with{Execution=f.State.Execution! with{SignalFresh=true}};f.Recovery.Tick();Assert.Equal("Failed",f.Recovery.Snapshot().State);Assert.Equal(0,f.Shots);
        f.Lease();f.Recovery.Tick();await f.Photos.FlushAsync(CancellationToken.None);Assert.Equal(1,f.Shots);f.Recovery.Tick();Assert.Equal(1,f.Shots);
        var status=f.Recovery.Snapshot();Assert.Equal("Captured",status.PhotoState);Assert.Equal("SavedLocal",status.AuditState);Assert.Equal("ProcessRecovery",f.Recovery.PendingExit()!.Metadata["reason"]!.GetValue<string>());
        Assert.DoesNotContain("已经自动捕获了摄像头画面，并将该画面安全上传",StudyExitNotice.Explanation(status));Assert.Contains("异常中断",StudyExitNotice.Explanation(status));
        f.Advance(30000);Assert.Equal("Completed",f.Recovery.Snapshot().State);Assert.Equal(1,f.Resumes);f.Recovery.Tick();Assert.Equal(1,f.Resumes);
    }
    [Fact]
    public async Task Exam_recovery_requires_the_exam_guard_context()
    {
        await using var f=new Fixture();await f.Active();f.Lease();
        f.State=f.State with{Mode="Exam",ClockSource="Windows"};
        Assert.NotEqual("Failed",f.Recovery.Snapshot().State);Assert.Equal(0,f.Shots);
        f.Files.Write("recovery.json",new GuardRecoveryEvent(f.Incident,f.Generation,new(10,100),new(20,200),f.Time.GetUtcNow(),f.Time.GetUtcNow().AddMinutes(90),GuardRecoveryEvent.Key("scope",f.State.Version!+":Exam")));
        Assert.Equal("Failed",f.Recovery.Snapshot().State);
        await f.Photos.FlushAsync(CancellationToken.None);Assert.Equal(1,f.Shots);
    }
    [Fact]
    public async Task Restart_keeps_original_warning_deadline_and_never_repeats_recovery_shot()
    {
        await using var f=new Fixture();await f.Active();f.Lease();f.Recovery.Tick();await f.Photos.FlushAsync(CancellationToken.None);f.Advance(10000);
        await f.Recovery.DisposeAsync();f.Recovery=f.NewRecovery();var status=f.Recovery.Snapshot();Assert.Equal(20,status.WarningRemainingSeconds);Assert.Equal(1,f.Shots);f.Recovery.Tick();Assert.Equal(1,f.Shots);
    }
    [Theory]
    [InlineData("expected")][InlineData("binding")][InlineData("clock")][InlineData("stale")]
    public async Task Normal_exit_changed_binding_or_unfresh_execution_does_not_trigger_recovery(string mode)
    {
        await using var f=new Fixture();await f.Active();f.Lease();
        if(mode=="expected")f.Files.Write("expected-exit.json",new GuardExpectedExit(f.Generation,new(20,200),f.Time.GetUtcNow().AddMinutes(90)));
        if(mode=="binding")f.State=f.State with{Version=new string('b',64)};
        if(mode=="clock")f.State=f.State with{ClockReady=false};
        if(mode=="stale")f.State=f.State with{Execution=f.State.Execution! with{SignalFresh=false}};
        Assert.NotEqual("Failed",f.Recovery.Snapshot().State);Assert.Equal(0,f.Shots);Assert.Null(f.Recovery.PendingExit());
    }
    [Fact]
    public async Task Actual_manual_capture_requests_manual_lease_and_takeover_withdraws_that_lease()
    {
        await using var f=new Fixture();await f.Active();f.Noise.StopScheduled(f.Session);
        for(int i=0;i<100 && f.Noise.CaptureBusy;i++)await Task.Delay(10);Assert.False(f.Noise.CaptureBusy);
        var id=Guid.NewGuid();Assert.Equal("Accepted",f.Noise.StartManualStudy(id,"mic",600).Outcome);await f.Active();
        var utc=f.Time.GetUtcNow();f.State=f.State with{Execution=f.State.Execution! with{Owner="Manual",SessionId=id},Manual=new(Guid.NewGuid(),1,id,"Active",null,utc,utc.AddMinutes(10),true,600),
            SchoolNow=utc.AddHours(8),Window=new(utc.AddHours(8),utc.AddHours(8).AddMinutes(10))};
        var request=f.Photos.PhotoLeaseRequest();Assert.NotNull(request?["manual"]);Assert.Equal(id.ToString(),request!["manual"]!["sessionId"]!.GetValue<string>());
        f.Noise.SetScheduledPriority(true);for(int i=0;i<100 && f.Noise.CaptureBusy;i++)await Task.Delay(10);
        Assert.Null(f.Photos.PhotoLeaseRequest());Assert.Equal(0,f.Shots);
    }
    [Fact]
    public async Task Corrupt_claim_cannot_rearm_recovery_or_duplicate_a_camera_capture()
    {
        await using var f=new Fixture();await f.Active();f.Lease();f.Recovery.Tick();await f.Photos.FlushAsync(CancellationToken.None);
        File.WriteAllText(Path.Combine(f.Folder,"guard","recovery-claim.json"),"{broken");
        await f.Recovery.DisposeAsync();f.Recovery=f.NewRecovery();var state=f.Recovery.Snapshot();
        Assert.Equal("RECOVERY_CLAIM_INVALID",state.AuditError);Assert.NotEqual("Failed",state.State);Assert.Equal(1,f.Shots);
    }
    [Fact]
    public async Task Recovery_shares_noise_exit_cooldown_and_unavailable_lease_never_claims_a_photo()
    {
        await using var f=new Fixture();await f.Active();f.Lease();f.Photos.ExitIntent(new(1,Guid.NewGuid(),"study.photos.exit-intent"));await f.Photos.FlushAsync(CancellationToken.None);
        f.Recovery.Tick();Assert.Equal("Suppressed",f.Recovery.Snapshot().PhotoState);Assert.Equal(1,f.Shots);
    }
}

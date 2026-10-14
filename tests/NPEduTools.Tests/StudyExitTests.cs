using System.Text.Json.Nodes;
using NPEduTools.Contracts;
using NPEduTools.Core;
using NPEduTools.Host;
namespace NPEduTools.Tests;
public sealed partial class StudyExitTests
{
    private sealed class Clock:TimeProvider {public long Ms;public override long TimestampFrequency=>1000;public override long GetTimestamp()=>Ms;public override DateTimeOffset GetUtcNow()=>new DateTimeOffset(2026,10,11,12,0,0,TimeSpan.Zero).AddMilliseconds(Ms);}
    // These tests advance verification time abruptly, not microphone sample time.
    // A fixed capture clock keeps the independent no-audio fixture from racing the 3 s watchdog.
    private sealed class CaptureClock : TimeProvider { public override long GetTimestamp() => 0; }
    private sealed class Capture:INoiseCapture {public string DeviceName=>"Synthetic; no microphone";public event Action<NoiseFrame>? Frame{add{}remove{}}public event Action<string>? Failed{add{}remove{}}public void Start(){}public void Dispose(){}}
    private sealed class Fixture:IAsyncDisposable
    {
        public readonly string Folder=Path.Combine(Path.GetTempPath(),"npep-exit-test-"+Guid.NewGuid().ToString("N"));
        public readonly Clock Time=new();public readonly Guid Host=Guid.NewGuid(),Session=Guid.NewGuid();
        public StudyHostState State;public StudyExitService Guard;public readonly StudyPhotoService Photos;public readonly NoiseService Noise;
        public readonly StudyExitJournal Journal;public int Resumed;
        public Func<StudyHostState>? ExitSample;
        public Fixture(bool protectedNoise=false){
            Directory.CreateDirectory(Folder);var window=new SchoolNoiseWindow(Time.GetUtcNow().AddHours(7),Time.GetUtcNow().AddHours(9.5));
            State=new(new string('a',64),"APPLIED","APPLIED","ACTIVE","Active","WINDOW_ACTIVE",true,Time.GetUtcNow().AddHours(8),window,null,
                new(new("Asia/Shanghai","2026-10-11",null,[],[]),new("StatusTrend",180),new(false,"OwnClass",[],60),new(true,-30,-40,600,3),new(false,3,30,30,true,false)),
                new("Schedule","Active",Session,"WINDOW_ACTIVE",true,"2026-10-11T20:00:00.000",new("2026-10-11T19:00:00.000","2026-10-11T21:30:00.000"),false));
            NoiseManagementStore? management=null;
            if(protectedNoise){management=new(Path.Combine(Folder,"management"),Time);Assert.Null(management.Configure(null,"synthetic-teacher-secret",false));}
            Noise=new(_=>new Capture(),()=>[],new CaptureClock(),management:management);
            if(protectedNoise){var n=Noise.Snapshot();Noise.Handle(new(1,Guid.NewGuid(),"noise.command",Noise:new("select",n.InstanceId,n.Revision,"synthetic")));Noise.StartScheduled(Session);Host=Noise.Protection().InstanceId;}
            static byte[] Protect(byte[] data,bool encrypt)=>data.Select(b=>(byte)(b^101)).ToArray();
            Photos=new(()=>("scope",true,State),Noise,new(()=>[],Folder),(_,_)=>throw new InvalidOperationException("Camera must not open"),new(Path.Combine(Folder,"photos"),Protect,Time),Time,false);
            Journal=new(Path.Combine(Folder,"exit"),Protect);
            Guard=new(()=>("scope",true,ExitSample?.Invoke()??State),()=>protectedNoise?Noise.Protection():new(true,true,Host,Session),Photos,Journal,Time,false,_=>Resumed++);Guard.BindExit("scope");
        }
        public HostRequest Begin(Guid id)=>new(1,id,"study.exit.begin");
        public HostRequest Authorize(Guid id,Guid? session=null)=>new(1,Guid.NewGuid(),"noise.management.command",NoiseManagement:new("authorize","bad-password",Purpose:"maintenance",TargetRequestId:id,InstanceId:Host,SessionId:session??Session));
        public async ValueTask DisposeAsync(){await Guard.DisposeAsync();await Photos.DisposeAsync();await Noise.DisposeAsync();var full=Path.GetFullPath(Folder);Assert.Equal(Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar),Path.GetDirectoryName(full));Assert.StartsWith("npep-exit-test-",Path.GetFileName(full));Directory.Delete(full,true);}
    }
    private sealed class NeverRead:ILessonStatusReader {public Task<StatusResult> ReadAsync(StatusQuery query,CancellationToken token)=>throw new InvalidOperationException("No ClassIsland fixture reads");}
    [Theory]
    [InlineData("wrong")][InlineData("timeout")][InlineData("correct")]
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public async Task Real_named_pipe_password_verification_and_host_exit_are_fenced_by_this_attempt(string mode)
    {
        await using var f=new Fixture(true);for(int i=0;i<100 && f.Noise.Snapshot().State!="Active";i++)await Task.Delay(10);
        Assert.True(f.Noise.Protection().Protected);string pipe="NPEduTools.Test.exit."+Guid.NewGuid().ToString("N");
        using var lifetime=new CancellationTokenSource(TimeSpan.FromSeconds(10));int stops=0;
        var running=new PipeServer(pipe,new NeverRead(),_=>{},noise:f.Noise,studyPhotos:f.Photos,studyExit:f.Guard,stop:()=>stops++,prepareStop:()=>true).RunAsync(lifetime.Token);
        try {
            Assert.Null((await HostClient.RequestAsync(pipe,"host.ping",lifetime.Token)).StudyExit);
            var target=new HostRequest(1,Guid.NewGuid(),"host.stop");var begin=await HostClient.RequestAsync(pipe,target with{Capability="study.exit.begin"},lifetime.Token);
            Assert.Equal("Waiting",begin.StudyExit!.State);Assert.Equal("EXIT_VERIFICATION_REQUIRED",(await HostClient.RequestAsync(pipe,target,lifetime.Token)).ErrorCode);
            if(mode=="timeout")f.Time.Ms=30000;
            var command=f.Authorize(target.RequestId);if(mode!="wrong")command=command with{NoiseManagement=command.NoiseManagement! with{Secret="synthetic-teacher-secret"}};
            var result=await HostClient.RequestAsync(pipe,command,lifetime.Token);
            if(mode=="correct"){
                Assert.Equal("Succeeded",result.Outcome);Assert.NotNull(result.NoiseProtection!.Ticket);
                Assert.Equal("Succeeded",(await HostClient.RequestAsync(pipe,target with{NoiseAuthorization=result.NoiseProtection.Ticket},lifetime.Token)).Outcome);
                for(int i=0;i<100 && stops==0;i++)await Task.Delay(10);Assert.Equal(1,stops);Assert.Null(f.Guard.PendingExit());
            }else{
                Assert.Equal("Rejected",result.Outcome);var state=(await HostClient.RequestAsync(pipe,"study.exit.status",lifetime.Token)).StudyExit!;
                Assert.Equal(mode=="wrong"?"WrongPassword":"Timeout",state.Reason);Assert.Equal("Failed",state.State);Assert.Equal(0,stops);Assert.True(f.Noise.Protection().Protected);
                Assert.DoesNotContain("synthetic-teacher-secret",f.Guard.PendingExit()!.Metadata.ToJsonString());
            }
        }finally{await lifetime.CancelAsync();try{await running;}catch(OperationCanceledException){}}
    }
    [Fact]
    public async Task Monotonic_deadline_cannot_be_renewed_by_reopening_and_warning_returns_without_stopping_capture()
    {
        await using var f=new Fixture();var id=Guid.NewGuid();Assert.Equal("Waiting",f.Guard.Begin(f.Begin(id)).StudyExit!.State);
        f.Time.Ms=29999;Assert.Equal("Waiting",f.Guard.Begin(f.Begin(id)).StudyExit!.State);Assert.Equal("Rejected",f.Guard.Begin(f.Begin(Guid.NewGuid())).Outcome);
        f.Time.Ms=30000;var fail=f.Guard.Snapshot();Assert.Equal("Failed",fail.State);Assert.Equal("Timeout",fail.Reason);Assert.Equal(30,fail.WarningRemainingSeconds);
        Assert.Equal("SavedLocal",fail.AuditState);Assert.Equal("Suppressed",fail.PhotoState);Assert.False(f.Guard.CanComplete(id));
        Assert.NotNull(f.Guard.PendingExit());f.Time.Ms=59999;Assert.Equal("Failed",f.Guard.Snapshot().State);
        f.Time.Ms=60000;Assert.Equal("Completed",f.Guard.Snapshot().State);Assert.Equal(1,f.Resumed);f.Guard.Tick();Assert.Equal(1,f.Resumed);
        Assert.Equal("Waiting",f.Guard.Begin(f.Begin(Guid.NewGuid())).StudyExit!.State);
    }
    [Fact]
    public async Task Verification_timeout_fixture_keeps_synthetic_capture_alive_after_watch_tick()
    {
        await using var f = new Fixture(true);
        for (int i = 0; i < 100 && f.Noise.Snapshot().State != "Active"; i++) await Task.Delay(10);
        Assert.Equal("Waiting", f.Guard.Begin(f.Begin(Guid.NewGuid())).StudyExit!.State);
        f.Time.Ms = 30000;
        await Task.Delay(350); // Let the separate microphone watchdog observe the fixture clock.
        Assert.True(f.Noise.Protection().Protected);
        Assert.Equal("Failed", f.Guard.Snapshot().State);
        Assert.Equal("Timeout", f.Guard.Snapshot().Reason);
    }
    [Fact]
    public async Task First_wrong_password_is_bound_to_the_original_attempt_and_invalidated_context_does_not_accuse_another_session()
    {
        await using var f=new Fixture();var id=Guid.NewGuid();f.Guard.Begin(f.Begin(id));
        f.Guard.ObserveAuthorization(f.Authorize(id,Guid.NewGuid()),new(1,Guid.NewGuid(),"Rejected","MANAGEMENT_SECRET_INVALID","fixture"));Assert.Equal("Waiting",f.Guard.Snapshot().State);
        f.Guard.ObserveAuthorization(f.Authorize(id),new(1,Guid.NewGuid(),"Rejected","MANAGEMENT_SECRET_INVALID","fixture"));Assert.Equal("WrongPassword",f.Guard.Snapshot().Reason);
        Assert.False(f.Guard.CanAuthorize(f.Authorize(id).NoiseManagement!));var packet=f.Guard.PendingExit()!;
        Assert.Equal(id.ToString(),packet.Metadata["eventId"]!.GetValue<string>());Assert.DoesNotContain("bad-password",packet.Metadata.ToJsonString());
        Assert.Equal(id.ToString(),f.Journal.Next("scope")!.Metadata["eventId"]!.GetValue<string>());
        f.Guard.ExitStored(id);Assert.Equal("Stored",f.Guard.Snapshot().AuditState);Assert.Null(f.Guard.PendingExit());
        f.State=f.State with{ClockReady=false};Assert.Equal("Invalidated",f.Guard.Snapshot().State);Assert.Equal(0,f.Resumed);
    }
    [Fact]
    public async Task Success_before_deadline_has_no_failure_record_while_late_success_cannot_stop_host()
    {
        await using var f=new Fixture();var id=Guid.NewGuid();f.Guard.Begin(f.Begin(id));f.Time.Ms=29900;
        f.Guard.ObserveAuthorization(f.Authorize(id),new(1,Guid.NewGuid(),"Succeeded",null,"fixture"));Assert.True(f.Guard.CanComplete(id));Assert.Null(f.Guard.PendingExit());
        f.Time.Ms=90000;Assert.Equal("Succeeded",f.Guard.Snapshot().State);
        var late=Guid.NewGuid();f.Guard.Begin(f.Begin(late));f.Time.Ms+=30000;
        f.Guard.ObserveAuthorization(f.Authorize(late),new(1,Guid.NewGuid(),"Succeeded",null,"fixture"));Assert.False(f.Guard.CanComplete(late));Assert.Equal("Failed",f.Guard.Snapshot().State);
        f.Guard.BindExit("other-scope");Assert.Equal("NotRequired",f.Guard.Snapshot().State);Assert.Null(f.Guard.PendingExit());
    }
    [Theory]
    [InlineData("Manual",true,"Active")][InlineData("Schedule",false,"Active")][InlineData("Schedule",true,"Preparing")]
    public async Task Manual_invalid_clock_and_preparation_never_create_strict_exit_verification(string owner,bool clock,string phase)
    {await using var f=new Fixture();f.State=f.State with{ClockReady=clock,Phase=phase,Execution=f.State.Execution! with{Owner=owner}};Assert.Equal("NotRequired",f.Guard.Begin(f.Begin(Guid.NewGuid())).Outcome);Assert.Null(f.Guard.PendingExit());}
    [Theory]
    [InlineData("Capturing")][InlineData("Captured")][InlineData("Suppressed")][InlineData("Failed")][InlineData("Unavailable")]
    public void Text_never_claims_an_unacknowledged_photo_was_uploaded(string photo)
    {var s=new StudyExitState(Guid.NewGuid(),"Failed","Timeout",0,30,photo,"SavedLocal");string text=StudyExitNotice.Explanation(s);Assert.DoesNotContain("并将该画面安全上传",text);Assert.Contains("正在等待上传",text);Assert.Equal(10,StudyExitNotice.Translations.Split('\n').Length);}
}

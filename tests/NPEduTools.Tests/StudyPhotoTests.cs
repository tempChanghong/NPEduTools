using System.Security.Cryptography;
using System.Text.Json.Nodes;
using NPEduTools.Contracts;
using NPEduTools.Core;
using NPEduTools.Host;
namespace NPEduTools.Tests;
[Collection(ProcessIntegrationCollection.Name)] // Keep the bounded child-process probe out of parallel CPU-heavy fixtures.
public sealed class StudyPhotoTests
{
    private sealed class Clock : TimeProvider
    { public long Ms; public override long TimestampFrequency => 1000; public override long GetTimestamp() => Ms; }
    private sealed class Folder : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "npep-photo-test-" + Guid.NewGuid().ToString("N"));
        public Folder() => Directory.CreateDirectory(Path);
        public void Dispose() {
            Assert.Equal(System.IO.Path.GetFullPath(System.IO.Path.GetTempPath()).TrimEnd(System.IO.Path.DirectorySeparatorChar), System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(Path)));
            Assert.StartsWith("npep-photo-test-", System.IO.Path.GetFileName(Path)); Directory.Delete(Path, true);
        }
    }
    private static readonly DateTimeOffset Utc = DateTimeOffset.Parse("2026-10-11T12:00:00Z");
    private static readonly byte[] Jpeg = [255, 216, 255, 217]; // journal tests only; worker tests use real synthetic JPEG.
    private static StudyPhotoPacket Packet(string scope = "scope", string camera = "camera", string mic = "mic") => new(scope, camera, mic,
        new JsonObject { ["eventId"] = Guid.NewGuid().ToString(), ["leaseId"] = Guid.NewGuid().ToString(), ["sessionId"] = Guid.NewGuid().ToString(),
            ["version"] = new string('a',64), ["capturedAt"] = Utc.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'"), ["schoolNow"] = "2026-10-11T20:00:00.000",
            ["reason"] = "Noise", ["unit"] = "Dbfs", ["level"] = -20, ["high"] = -30, ["continuousSeconds"] = 3, ["calibrationId"] = null,
            ["hash"] = Convert.ToHexStringLower(SHA256.HashData(Jpeg)), ["width"] = 2, ["height"] = 2, ["byteCount"] = Jpeg.Length }, Jpeg);
    private static byte[] FakeProtection(byte[] bytes, bool encrypt) => bytes.Select(b => (byte)(b ^ 0x7a)).ToArray();
    [Fact]
    public void Shared_interval_survives_restart_and_wall_clock_rollback_is_conservative()
    {
        using var folder = new Folder(); var clock = new Clock(); var journal = new StudyPhotoJournal(folder.Path, FakeProtection, clock);
        Assert.True(journal.Claim(Utc,30)); clock.Ms=29999; Assert.False(journal.Claim(Utc.AddHours(10),30));
        clock.Ms=30000; Assert.True(journal.Claim(Utc.AddSeconds(30),30));
        var restarted = new StudyPhotoJournal(folder.Path,FakeProtection,clock);
        Assert.False(restarted.Claim(Utc.AddSeconds(59),30)); Assert.True(restarted.Claim(Utc.AddSeconds(60),30));
        var rollback = new StudyPhotoJournal(folder.Path,FakeProtection,clock);
        Assert.False(rollback.Claim(Utc.AddDays(-1),30)); clock.Ms+=30000; Assert.True(rollback.Claim(Utc.AddDays(-1),30));
    }
    [Fact]
    public void Queue_is_protected_scoped_bounded_and_does_not_use_Windows_wall_time_to_expire()
    {
        using var folder = new Folder(); var journal = new StudyPhotoJournal(folder.Path,FakeProtection);
        var packet=Packet(); journal.Save(packet);
        Assert.DoesNotContain("capturedAt",System.Text.Encoding.UTF8.GetString(File.ReadAllBytes(Directory.GetFiles(folder.Path,"*.photo").Single())));
        Assert.Equal(packet.Metadata.ToJsonString(),journal.Next("scope","camera","mic",null)!.Metadata.ToJsonString());
        Assert.Null(journal.Next("scope","camera","other-mic",null)); Assert.Equal(0,journal.Pending);
        journal.Save(packet); Assert.Null(journal.Next("scope","camera","mic",Utc.AddDays(1).AddMilliseconds(1)));
        journal.Save(packet); journal.Remove(Guid.Parse(packet.Metadata["eventId"]!.GetValue<string>())); journal.Remove(Guid.Parse(packet.Metadata["eventId"]!.GetValue<string>()));
        for(int i=0;i<256;i++) journal.Save(Packet());
        Assert.Throws<IOException>(()=>journal.Save(Packet())); Assert.Equal(256,journal.Pending);
    }
    [Fact]
    public void Corrupt_cooldown_and_queue_fail_closed_without_overwriting_evidence()
    {
        using var folder=new Folder(); var journal=new StudyPhotoJournal(folder.Path,FakeProtection);
        string cooldown=System.IO.Path.Combine(folder.Path,"cooldown.json"); File.WriteAllText(cooldown,"bad");
        Assert.False(journal.Claim(Utc,30)); Assert.Equal("bad",File.ReadAllText(cooldown));
        string file=System.IO.Path.Combine(folder.Path,Guid.NewGuid().ToString("N")+".photo"); File.WriteAllText(file,"broken");
        Assert.Null(journal.Next("scope","camera","mic",Utc)); Assert.True(File.Exists(file));
    }
    [Fact]
    public async Task Synthetic_camera_worker_runs_in_actual_child_process_without_opening_a_camera()
    {
        if (!OperatingSystem.IsWindows()) return;
        var root=System.IO.Path.GetFullPath(System.IO.Path.Combine(AppContext.BaseDirectory,"../../../../../"));
        string configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent!.Name;
        string exe=System.IO.Path.Combine(root,"src","NPEduTools.Camera","bin",configuration,"net10.0-windows","NPEduTools.Camera.exe");
        Assert.True(File.Exists(exe));
        using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(6));
        var frame=await StudyPhotoProcess.RunAsync(exe,"synthetic-only",true,timeout.Token);
        Assert.Equal(640,frame.Width); Assert.Equal(360,frame.Height); Assert.InRange(frame.Bytes.Length,100,262144);
    }
    private sealed class Capture : INoiseCapture
    {
        public string DeviceName => "Synthetic; no microphone";
        public event Action<NoiseFrame>? Frame;
        public event Action<string>? Failed { add { } remove { } }
        public void Start() { }
        public void Dispose() { }
        public void Emit(double dbfs) => Frame?.Invoke(new(.1,Math.Pow(10,dbfs/10),Math.Pow(10,dbfs/20),0,false));
    }
    [Fact]
    public async Task Exit_click_is_before_validation_hide_is_irrelevant_and_renewal_does_not_drop_a_valid_frame()
    {
        using var folder=new Folder(); var clock=new Clock(); var capture=new Capture();
        await using var noise=new NoiseService(_=>capture,()=>[],clock);
        var n=noise.Snapshot(); noise.Handle(new(1,Guid.NewGuid(),"noise.command",Noise:new("select",n.InstanceId,n.Revision,"mic")));
        Guid session=Guid.NewGuid(); noise.StartScheduled(session);
        for(int i=0;i<100 && noise.Snapshot().State!="Active";i++) await Task.Delay(10);
        capture.Emit(-20);
        var devices=new StudyDeviceService(()=>[new("camera","Synthetic camera")],folder.Path);
        var d=devices.Snapshot(); devices.Handle(new(1,Guid.NewGuid(),"study.devices.select",StudyCamera:new("select",d.InstanceId,d.Revision,"camera")));
        var window=new SchoolNoiseWindow(Utc.AddHours(7),Utc.AddHours(9.5));
        var policy=new StudyNoise(true,-30,-40,600,3);
        var settings=new StudySettings(new("Asia/Shanghai","2026-10-11",null,[],[]),new("StatusTrend",180),new(false,"OwnClass",[],60),policy,new(true,3,30,30,true,false));
        var state=new StudyHostState(new string('a',64),"APPLIED","NOT_INTEGRATED","ACTIVE","Active","WINDOW_ACTIVE",true,Utc.AddHours(8),window,null,settings,
            new("Schedule","Active",session,"WINDOW_ACTIVE",true,"2026-10-11T20:00:00.000",new("2026-10-11T19:00:00.000","2026-10-11T21:30:00.000"),true),
            new(Guid.NewGuid(),true,false,180));
        var image=new TaskCompletionSource<StudyCameraFrame>(TaskCreationOptions.RunContinuationsAsynchronously); int shots=0;
        var journal=new StudyPhotoJournal(System.IO.Path.Combine(folder.Path,"photos"),FakeProtection,clock);
        await using var service=new StudyPhotoService(()=>("scope",true,state),noise,devices,(_,_)=>{shots++;return image.Task;},journal,clock,false);
        service.BindPhotos("scope");
        JsonObject Lease() => new() { ["leaseId"]=Guid.NewGuid().ToString(),["version"]=state.Version,["sessionId"]=session.ToString(),
            ["leaseSeconds"]=60,["createdAt"]=Utc.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'"),["expiresAt"]=Utc.AddSeconds(60).ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'"),
            ["maxBytes"]=262144,["maxEdge"]=1280 };
        var request=new HostRequest(1,Guid.NewGuid(),"study.photos.exit-intent");
        Assert.Equal("Suppressed",service.ExitIntent(request).Outcome);
        service.ConfirmPhotoLease(Lease(),TimeSpan.Zero);
        Assert.Equal("Succeeded",service.ExitIntent(request).Outcome);
        service.ConfirmPhotoLease(Lease(),TimeSpan.Zero); capture.Emit(-50); // signal at trigger must be frozen.
        image.SetResult(new(Jpeg,2,2)); await service.FlushAsync(CancellationToken.None);
        Assert.Equal(1,shots); Assert.Equal(1,journal.Pending);
        var packet=service.PendingPhoto()!; Assert.Equal(-20,packet.Metadata["level"]!.GetValue<double>());
        Assert.Equal("Suppressed",service.ExitIntent(new(1,Guid.NewGuid(),"study.photos.exit-intent")).Outcome);
        service.BindPhotos(null); Assert.Equal(1,journal.Pending); // graceful shutdown retains queue.
        service.PhotoFailed("PHOTO_NOT_ELIGIBLE"); Assert.Equal(0,journal.Pending);
        service.BindPhotos("scope"); clock.Ms=30000; capture.Emit(-50); service.ConfirmPhotoLease(Lease(),TimeSpan.Zero);
        // No App or selected display is required; actual high frames alone qualify the photo.
        state=state with { Display=null }; service.Tick();
        for(int i=0;i<29;i++) { clock.Ms+=100; capture.Emit(-20); service.Tick(); }
        Assert.Equal(1,shots);
        clock.Ms+=100; capture.Emit(-20); service.Tick(); await service.FlushAsync(CancellationToken.None);
        Assert.Equal(2,shots); Assert.Equal("Noise",service.PendingPhoto()!.Metadata["reason"]!.GetValue<string>());
        var queued=service.PendingPhoto()!;
        service.PhotoFailed("PHOTO_SESSION_CHANGED"); Assert.Equal(1,journal.Pending); // unrelated grant error cannot remove an upload.
        service.PhotoFailed("PHOTO_SESSION_CHANGED",Guid.Parse(queued.Metadata["eventId"]!.GetValue<string>())); Assert.Equal(0,journal.Pending);
        clock.Ms+=30000; state=state with { Phase="Preparing" }; Assert.Equal("Suppressed",service.ExitIntent(request).Outcome);
        state=state with { Phase="Active",Execution=state.Execution! with { Owner="Manual" } }; Assert.Equal("Suppressed",service.ExitIntent(request).Outcome);
        state=state with { Execution=state.Execution! with { Owner="Schedule",SignalFresh=false } }; Assert.Equal("Suppressed",service.ExitIntent(request).Outcome);
        state=state with { Execution=state.Execution! with { SignalFresh=true },Settings=state.Settings! with { Noise=policy with { Estimated=new(70,60) } } };
        capture.Emit(-20); service.ConfirmPhotoLease(Lease(),TimeSpan.Zero);
        Assert.Equal("Succeeded",service.ExitIntent(new(1,Guid.NewGuid(),"study.photos.exit-intent")).Outcome);
        await service.FlushAsync(CancellationToken.None);
        packet=service.PendingPhoto()!;
        Assert.Equal("EstimatedDbA",packet.Metadata["unit"]!.GetValue<string>());Assert.Null(packet.Metadata["level"]);Assert.Null(packet.Metadata["calibrationId"]);
    }
}

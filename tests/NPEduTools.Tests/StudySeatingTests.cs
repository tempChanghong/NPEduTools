using System.Text.Json.Nodes;
using System.Text.Json;
using NPEduTools.Core;
using NPEduTools.Contracts;
using NPEduTools.Host;
using NPEduTools.Integrations.Npep;
namespace NPEduTools.Tests;
public sealed partial class StudySeatingTests
{
    private sealed class Clock : TimeProvider { public long Ms; public override long TimestampFrequency => 1000; public override long GetTimestamp() => Ms; }
    [Fact]
    public async Task Complete_image_is_scope_fenced_chunk_bounded_and_expires_without_affecting_capture()
    {
        var config = JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory,"Fixtures/study-device-cases.json")))![0]!["value"]!.DeepClone().AsObject();
        var clock = new Clock(); long sequence = 0; var connection = Guid.NewGuid(); var bridge = Guid.NewGuid();
        var now = new DateTimeOffset(2026,10,12,18,58,0,TimeSpan.Zero);
        await using var service = new StudySettingsService(() => new(connection,bridge,++sequence,0,now.AddMilliseconds(clock.Ms),0,"Advancing","synthetic"),()=>false,clock,false);
        service.BindStudy(config["identity"]!.ToJsonString());service.ApplyStudy(config);clock.Ms+=500;service.ApplyStudy(config);clock.Ms+=500;service.ApplyStudy(config);
        Assert.NotNull(service.StudySeatingRequest());
        var bytes = Enumerable.Repeat((byte)42,20000).ToArray(); string hash = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes));
        var manifest = new JsonObject { ["version"] = service.Snapshot().Version, ["classId"]=config["scope"]!["classId"]!.DeepClone(),["revision"]=1,["asset"]=new JsonObject {
            ["hash"]=hash,["byteCount"]=bytes.Length,["width"]=1200,["height"]=800,["chunks"]=2 }};
        service.ApplySeating(manifest,bytes);var meta=service.Snapshot().Seating!;Assert.Equal("READY",meta.State);
        using var complete = new MemoryStream();
        for(int i=0;i<2;i++) { var request=new HostRequest(1,Guid.NewGuid(),"study.seating",StudySeating:new(meta.Version!,1,hash,i));Assert.Null(Protocol.Validate(request));
            var response=service.ReadSeating(request);Assert.Null(response.Noise);Assert.Null(response.Study);complete.Write(Convert.FromBase64String(response.SeatingChunk!.Data)); }
        Assert.Equal(bytes,complete.ToArray());
        Assert.Equal("Rejected",service.ReadSeating(new(1,Guid.NewGuid(),"study.seating",StudySeating:new(new string('f',64),1,hash,0))).Outcome);
        var invalid=manifest.DeepClone().AsObject();invalid["asset"]!["chunks"]=3;Assert.Throws<NpepException>(()=>service.ApplySeating(invalid,bytes));
        service.SeatingFailed("SEATING_OFFLINE");Assert.Equal("READY",service.Snapshot().Seating!.State);
        clock.Ms+=15000;service.ApplyStudy(config);Assert.NotEqual("READY",service.Snapshot().Seating!.State);
        clock.Ms+=500;service.ApplyStudy(config);clock.Ms+=500;service.ApplyStudy(config);
        service.ApplySeating(manifest,bytes);service.BindStudy(null);Assert.NotEqual("READY",service.Snapshot().Seating!.State);
        Assert.Null(service.StudySeatingRequest());
    }
    [Fact]
    public void Pipe_reads_and_pulses_reject_invalid_bounds_without_new_capture_capabilities()
    {
        Assert.NotNull(Protocol.Validate(new(1,Guid.NewGuid(),"study.seating",StudySeating:new(new string('a',64),1,new string('a',64),128))));
        Assert.NotNull(Protocol.Validate(new(1,Guid.NewGuid(),"study.status",StudySeating:new(new string('a',64),1,new string('a',64),0))));
        Assert.NotNull(Protocol.Validate(new(1,Guid.NewGuid(),"study.seating.presentation",SeatingPresentation:new(Guid.NewGuid(),Guid.NewGuid(),1,new string('a',64),1,new string('b',64),true,null))));
    }
    private sealed class NeverRead : ILessonStatusReader
    { public Task<StatusResult> ReadAsync(StatusQuery query,CancellationToken token)=>throw new InvalidOperationException("No real clock/device reads"); }
    [Fact]
    public async Task Actual_Host_pipe_dispatcher_exposes_seating_only_after_new_status_capability()
    {
        if (!OperatingSystem.IsWindows()) return;
        var time=new Clock();await using var study=new StudySettingsService(()=>throw new InvalidOperationException("No real clock"),()=>false,time,false);
        var display=new StudyDisplayService(study.DisplaySnapshot,time);string pipe="NPEduTools.Test.seating-compat."+Guid.NewGuid().ToString("N");
        using var lifetime=new CancellationTokenSource();var server=new PipeServer(pipe,new NeverRead(),_=>{},study:study,studyDisplay:display).RunAsync(lifetime.Token);
        try {
            using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var old=await HostClient.RequestAsync(pipe,new HostRequest(1,Guid.NewGuid(),"study.status"),timeout.Token);
            var modern=await HostClient.RequestAsync(pipe,new HostRequest(1,Guid.NewGuid(),"study.status.seating"),timeout.Token);
            Assert.Equal("Succeeded",old.Outcome);Assert.Null(old.Study!.Seating);Assert.DoesNotContain("\"seating\"",JsonSerializer.Serialize(old,Protocol.Json));
            Assert.Equal("Succeeded",modern.Outcome);Assert.NotNull(modern.Study!.Seating);
        } finally {lifetime.Cancel();try{await server;}catch(OperationCanceledException){} }
    }

}

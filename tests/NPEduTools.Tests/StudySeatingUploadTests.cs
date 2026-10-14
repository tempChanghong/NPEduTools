using System.Security.Cryptography;
using System.Text.Json.Nodes;
using NPEduTools.Contracts;
using NPEduTools.Core;
using NPEduTools.Host;
using NPEduTools.Integrations.Npep;
namespace NPEduTools.Tests;
public sealed partial class StudySeatingUploadTests
{
    private sealed class NoSchoolRead : ILessonStatusReader
    { public Task<StatusResult> ReadAsync(StatusQuery query, CancellationToken token) => throw new InvalidOperationException("No real school or device reads"); }
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task Network_failure_during_status_or_upload_must_not_stop_the_Host_pipe(bool uploading)
    {
        if (!OperatingSystem.IsWindows()) return;
        string pipe="NPEduTools.Test.seating-network."+Guid.NewGuid().ToString("N");
        Guid id=Guid.NewGuid(); byte[] bytes=Image();
        var service=new StudySeatingUploadService(pipe,Policy,
            (_,_)=>uploading?Task.FromResult(new NpepSeatingUploadScope(new(),Scope())):Task.FromException<NpepSeatingUploadScope>(new HttpRequestException("Synthetic connection reset; no external server")),
            (_,_,_,_,_)=>Task.FromException<JsonObject>(new HttpRequestException("Synthetic connection reset after send; no external server")));
        using var lifetime=new CancellationTokenSource(TimeSpan.FromSeconds(8));
        var running=new PipeServer(pipe,new NoSchoolRead(),_=>{},seatingUploads:service).RunAsync(lifetime.Token);
        try {
            var request=new HostRequest(1,Guid.NewGuid(),"study.seating.upload.status");
            if (uploading) {
                var status=await HostClient.RequestAsync(pipe,request,lifetime.Token);
                StudySeatingTransfer.Write(pipe,id,bytes); request=Command(status.SeatingUpload!,id,bytes);
            }
            var response=await HostClient.RequestAsync(pipe,request,lifetime.Token);
            Assert.Equal("Rejected",response.Outcome);
            Assert.Equal(uploading?"UPLOAD_PENDING":"UPLOAD_UNAVAILABLE",response.ErrorCode);
            Assert.Equal("Succeeded",(await HostClient.RequestAsync(pipe,"host.ping",lifetime.Token)).Outcome);
            Assert.False(running.IsCompleted);
        } finally {
            lifetime.Cancel(); StudySeatingTransfer.Delete(pipe,id);
            try { await running; } catch(OperationCanceledException) { }
        }
    }
    private static readonly string Version=new('a',64);
    private sealed class Clock:TimeProvider {public long Ms;public override long TimestampFrequency=>1000;public override long GetTimestamp()=>Ms;}
    private static StudyHostState Policy()=>new(Version,"APPLIED","","","Outside","",false,null,null,null,
        new(new("Asia/Shanghai","2026-10-12",null,[],[]),new("StatusTrend",180),new(false,"OwnClass",[],60),new(false,null,null,600,5),new(false,3,30,30,false,false)));
    private static JsonObject Scope(int revision=0)=>new(){["version"]=Version,["classId"]="class-one",["className"]="本班",["revision"]=revision,["hash"]=null};
    private static byte[] Image() {byte[] b=new byte[24];new byte[]{137,80,78,71,13,10,26,10}.CopyTo(b,0);b[19]=2;b[23]=2;return b;}
    private static HostRequest Command(StudySeatingUploadState state,Guid id,byte[] png)=>new(1,Guid.NewGuid(),"study.seating.upload",
        SeatingUpload:new(state.TicketId,id,state.Version,state.Revision,Convert.ToHexStringLower(SHA256.HashData(png)),png.Length,2,2));
    [Fact]
    public async Task Uploaded_response_retries_without_republish_and_ticket_payload_cannot_change()
    {
        string pipe="NPEduTools.Test.seating-upload."+Guid.NewGuid().ToString("N");var id=Guid.NewGuid();byte[] bytes=Image();int publications=0;
        var service=new StudySeatingUploadService(pipe,Policy,(_,_)=>Task.FromResult(new NpepSeatingUploadScope(new(),Scope())),
            (_,_,revision,png,_)=>{publications++;var result=Scope(revision+1);result["hash"]=Convert.ToHexStringLower(SHA256.HashData(png));return Task.FromResult(result);});
        try {
            var state=(await service.HandleAsync(new(1,Guid.NewGuid(),"study.seating.upload.status"),default)).SeatingUpload!;
            StudySeatingTransfer.Write(pipe,id,bytes);var request=Command(state,id,bytes);Assert.Null(Protocol.Validate(request));
            var first=await service.HandleAsync(request,default);Assert.Equal("Saved",first.SeatingUpload!.State);
            StudySeatingTransfer.Delete(pipe,id);
            Assert.Equal(first.SeatingUpload,(await service.HandleAsync(request with {RequestId=Guid.NewGuid()},default)).SeatingUpload);
            Assert.Equal(1,publications);
            Assert.Equal("IDEMPOTENCY_CONFLICT",(await service.HandleAsync(request with {SeatingUpload=request.SeatingUpload! with {UploadId=Guid.NewGuid()}},default)).ErrorCode);
        } finally {StudySeatingTransfer.Delete(pipe,id);}
    }
    [Fact]
    public async Task Uncertain_publish_reuses_same_upload_id_then_saves_once()
    {
        string pipe="NPEduTools.Test.seating-retry."+Guid.NewGuid().ToString("N");var id=Guid.NewGuid();byte[] bytes=Image();int calls=0;
        var service=new StudySeatingUploadService(pipe,Policy,(_,_)=>Task.FromResult(new NpepSeatingUploadScope(new(),Scope())),
            (_,requestId,revision,png,_)=>{Assert.Equal(id,requestId);if(++calls==1)throw new IOException();var result=Scope(revision+1);result["hash"]=Convert.ToHexStringLower(SHA256.HashData(png));return Task.FromResult(result);});
        try {
            var state=(await service.HandleAsync(new(1,Guid.NewGuid(),"study.seating.upload.status"),default)).SeatingUpload!;
            StudySeatingTransfer.Write(pipe,id,bytes);var request=Command(state,id,bytes);
            Assert.Equal("UPLOAD_PENDING",(await service.HandleAsync(request,default)).ErrorCode);
            Assert.Equal("Saved",(await service.HandleAsync(request,default)).SeatingUpload!.State);
            Assert.Equal("Saved",(await service.HandleAsync(request,default)).SeatingUpload!.State);Assert.Equal(2,calls);
        }finally{StudySeatingTransfer.Delete(pipe,id);}
    }
    [Fact]
    public async Task Expiry_version_change_revision_conflict_and_bad_hash_never_publish()
    {
        string pipe="NPEduTools.Test.seating-fences."+Guid.NewGuid().ToString("N");var clock=new Clock();var state=Policy();int calls=0;
        var service=new StudySeatingUploadService(pipe,()=>state,(_,_)=>Task.FromResult(new NpepSeatingUploadScope(new(),Scope())),
            (_,_,_,_,_)=>{calls++;throw new Exception("Must not publish");},clock);
        var ticket=(await service.HandleAsync(new(1,Guid.NewGuid(),"study.seating.upload.status"),default)).SeatingUpload!;
        var id=Guid.NewGuid();var bytes=Image();StudySeatingTransfer.Write(pipe,id,bytes);
        try {
            var request=Command(ticket,id,bytes);
            Assert.Equal("SEATING_CHANGED",(await service.HandleAsync(request with {SeatingUpload=request.SeatingUpload! with {ExpectedRevision=1}},default)).ErrorCode);
            Assert.Equal("INVALID_IMAGE",(await service.HandleAsync(request with {SeatingUpload=request.SeatingUpload! with {Hash=new string('b',64)}},default)).ErrorCode);
            state=state with {Version=new string('b',64)};
            Assert.Equal("POLICY_CHANGED",(await service.HandleAsync(request,default)).ErrorCode);
            state=Policy();clock.Ms=600000;
            Assert.Equal("UPLOAD_EXPIRED",(await service.HandleAsync(request,default)).ErrorCode);Assert.Equal(0,calls);
        }finally{StudySeatingTransfer.Delete(pipe,id);}
    }
    [Fact]
    public async Task Tickets_are_bounded_and_oldest_evicted()
    {
        var clock=new Clock();var service=new StudySeatingUploadService("unused",Policy,(_,_)=>Task.FromResult(new NpepSeatingUploadScope(new(),Scope())),
            (_,_,_,_,_)=>throw new Exception("Must not publish"),clock);
        var first=(await service.HandleAsync(new(1,Guid.NewGuid(),"study.seating.upload.status"),default)).SeatingUpload!;
        for(int i=0;i<8;i++){clock.Ms++;await service.HandleAsync(new(1,Guid.NewGuid(),"study.seating.upload.status"),default);}
        Assert.Equal("UPLOAD_EXPIRED",(await service.HandleAsync(Command(first,Guid.NewGuid(),Image()),default)).ErrorCode);
    }
    [Fact]
    public void Small_pipe_command_rejects_excess_bounds_and_unrelated_capabilities()
    {
        var request=Command(new(Guid.NewGuid(),Version,"one","本班",0,null,"Ready"),Guid.NewGuid(),Image());
        Assert.Null(Protocol.Validate(request));Assert.Equal(65536,Protocol.MaxFrameBytes);
        foreach(var command in new[]{request.SeatingUpload! with {UploadId=Guid.Empty},request.SeatingUpload! with {ByteCount=2097153},request.SeatingUpload! with {Width=4096,Height=4096},request.SeatingUpload! with {Hash="../file"}})
            Assert.NotNull(Protocol.Validate(request with {SeatingUpload=command}));
        Assert.NotNull(Protocol.Validate(request with {Capability="host.ping"}));
    }
}

using System.Text.Json;
using System.IO.Pipes;
using System.Text.Json.Nodes;
using NPEduTools.Contracts;
using NPEduTools.Core;
using NPEduTools.Host;
using NPEduTools.Integrations.Npep;
internal static class SeatingHttpAcceptance
{
    private sealed class Clock : TimeProvider { public long Ms; public override long TimestampFrequency=>1000; public override long GetTimestamp()=>Ms; }
    private sealed class ForbiddenReader : ILessonStatusReader { public Task<StatusResult> ReadAsync(StatusQuery query,CancellationToken token)=>throw new InvalidOperationException("No actual ClassIsland/sensors permitted"); }
    public static async Task<int> RunAsync(string fixturePath,string resultPath)
    {
        var fixture=JsonNode.Parse(await File.ReadAllTextAsync(fixturePath))!.AsObject();var uri=new Uri(fixture["origin"]!.GetValue<string>());
        if(uri.Scheme!="http"||uri.Host!="127.0.0.1"||uri.UserInfo.Length!=0)throw new ArgumentException("Loopback fixture only");
        using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(30));using var api=new NpepApi(uri.GetLeftPart(UriPartial.Authority));
        var context=fixture["context"]!.DeepClone().AsObject();var version=fixture["version"]!.GetValue<string>();string bearer=fixture["bearer"]!.GetValue<string>();
        JsonObject ManifestBody()=>new(){["requestId"]=Guid.NewGuid().ToString(),["context"]=context.DeepClone(),["version"]=version,["observation"]=null,["downloaded"]=null};
        var uploadChecks=new List<string>();
        if(fixture["uploadImage"] is {} uploadImage) {
            var png=await File.ReadAllBytesAsync(uploadImage.GetValue<string>(),deadline.Token);
            if(png.Length>2097152)throw new InvalidDataException();
            var statusBody=new JsonObject{["requestId"]=Guid.NewGuid().ToString(),["context"]=context.DeepClone(),["version"]=version};
            var status=await api.SeatingWriteAsync(bearer,statusBody,false,deadline.Token);
            var body=new JsonObject{["requestId"]=Guid.NewGuid().ToString(),["context"]=context.DeepClone(),["version"]=version,
                ["expectedRevision"]=status["revision"]!.DeepClone(),["image"]=Convert.ToBase64String(png)};
            var saved=await api.SeatingWriteAsync(bearer,body,true,deadline.Token);
            var replay=await api.SeatingWriteAsync(bearer,body,true,deadline.Token);
            if(!JsonNode.DeepEquals(saved,replay)||saved["hash"]!.GetValue<string>()!=Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(png)))throw new InvalidDataException();
            uploadChecks.Add("Actual native-normalized PNG -> native HTTP adapter -> Express image parser and bound-class publication -> idempotent replay");
        }
        var manifest=await api.SeatingAsync(bearer,ManifestBody(),false,deadline.Token);var asset=manifest["asset"]!.AsObject();
        using var buffer=new MemoryStream();int count=asset["chunks"]!.GetValue<int>();string hash=asset["hash"]!.GetValue<string>();int revision=manifest["revision"]!.GetValue<int>();
        for(int i=0;i<count;i++) {
            var body=new JsonObject{["requestId"]=Guid.NewGuid().ToString(),["context"]=context.DeepClone(),["version"]=version,["revision"]=revision,["hash"]=hash,["index"]=i};
            var part=await api.SeatingAsync(bearer,body,true,deadline.Token);
            if(part["index"]!.GetValue<int>()!=i||part["count"]!.GetValue<int>()!=count||part["hash"]!.GetValue<string>()!=hash)throw new InvalidDataException();
            buffer.Write(Convert.FromBase64String(part["data"]!.GetValue<string>()));
        }
        var bytes=buffer.ToArray();if(Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes))!=hash)throw new InvalidDataException();
        var config=fixture["configuration"]!.DeepClone().AsObject();var time=new Clock();long sequence=0;var connection=Guid.NewGuid();var bridge=Guid.NewGuid();var now=new DateTimeOffset(2026,10,12,18,58,0,TimeSpan.Zero);
        await using var study=new StudySettingsService(()=>new(connection,bridge,++sequence,0,now.AddMilliseconds(time.Ms),0,"Advancing","synthetic"),()=>false,time,false);
        study.BindStudy(config["identity"]!.ToJsonString());study.ApplyStudy(config);time.Ms=500;study.ApplyStudy(config);time.Ms=1000;study.ApplyStudy(config);
        study.ApplySeating(manifest,bytes);var display=new StudyDisplayService(study.DisplaySnapshot,time);
        string pipe="NPEduTools.Test.http-seat."+Guid.NewGuid().ToString("N");using var lifetime=new CancellationTokenSource();var server=Task.Run(async()=>{
            for(int n=0;n<count+2;n++) {
                using var stream=new NamedPipeServerStream(pipe,PipeDirection.InOut,1,PipeTransmissionMode.Byte,PipeOptions.Asynchronous|PipeOptions.CurrentUserOnly);
                await stream.WaitForConnectionAsync(lifetime.Token);var request=await Protocol.ReadAsync<HostRequest>(stream,lifetime.Token);
                if(Protocol.Validate(request) is not null)throw new InvalidDataException();
                var response=request.Capability=="study.seating"?study.ReadSeating(request):display.Handle(request with {Capability="study.status"});
                if(request.Capability=="study.status"&&response.Study is { } oldState)response=response with {Study=oldState with {Seating=null}};
                await Protocol.WriteAsync(stream,response,lifetime.Token);
            }
        });
        var checks=new List<string>();
        try {
            var old=await HostClient.RequestAsync(pipe,new HostRequest(1,Guid.NewGuid(),"study.status"),deadline.Token);
            var modern=await HostClient.RequestAsync(pipe,new HostRequest(1,Guid.NewGuid(),"study.status.seating"),deadline.Token);
            if(old.Study?.Seating is not null||modern.Study?.Seating?.Hash!=hash)throw new InvalidDataException("Old/new local protocol isolation failed");
            checks.AddRange(uploadChecks);
            checks.Add("Actual loopback Express 0.14 -> native HTTP adapter -> Host image cache/read handlers -> private Named Pipe fixture; legacy metadata omitted");
            using var decoded=new MemoryStream();for(int i=0;i<count;i++) {
                var response=await HostClient.RequestAsync(pipe,new HostRequest(1,Guid.NewGuid(),"study.seating",StudySeating:new(version,revision,hash,i)),deadline.Token);
                if(response.Outcome!="Succeeded"||response.Noise is not null||response.SeatingChunk is not { } chunk)throw new InvalidDataException();decoded.Write(Convert.FromBase64String(chunk.Data));
            }
            if(!decoded.ToArray().SequenceEqual(bytes))throw new InvalidDataException();checks.Add("HTTP/Host/Pipe bytes and SHA256 agree; image channel issues no microphone command");
            await File.WriteAllBytesAsync(Path.Combine(Path.GetDirectoryName(resultPath)!,"native-http-seating.png"),bytes,deadline.Token);
            using var http=new HttpClient();using var withdrawal=await http.PostAsync(new Uri(uri,"/__seat-fixture/withdraw"),new StringContent("{}"),deadline.Token);withdrawal.EnsureSuccessStatusCode();
            var withdrawn=await api.SeatingAsync(bearer,ManifestBody(),false,deadline.Token);study.ApplySeating(withdrawn,null);
            if(study.Snapshot().Seating?.State=="READY")throw new InvalidDataException();checks.Add("Actual server withdrawal clears Host image availability without capture side effects");
            await File.WriteAllTextAsync(resultPath,JsonSerializer.Serialize(new {status="PASSED",checks,bytes=bytes.Length,chunks=count,realDevices=false,production=false,database="in-memory adapter"}),deadline.Token);return 0;
        } finally { lifetime.Cancel();try{await server;}catch(OperationCanceledException){} }
    }
}

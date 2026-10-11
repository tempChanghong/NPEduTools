using System.Net;
using System.Text.Json.Nodes;
using NPEduTools.Integrations.Npep;
namespace NPEduTools.Npep.Tests;
public sealed class SeatingUploadProtocolTests
{
    private sealed class Handler(Func<HttpRequestMessage,Task<HttpResponseMessage>> respond):HttpMessageHandler
    {protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken token)=>respond(request);}
    [Fact]
    public async Task Image_endpoint_admits_large_bounded_request_and_validates_response_identity()
    {
        var context=JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory,"seating-cases.json")))!.AsArray()
            .First(v=>v!["definition"]!.GetValue<string>()=="manifestRequest" && v["expected"]!.GetValue<bool>())!["value"]!["context"]!.DeepClone();
        var body=new JsonObject{["requestId"]=Guid.NewGuid().ToString(),["version"]=new string('a',64),["context"]=context,
            ["expectedRevision"]=0,["image"]=Convert.ToBase64String(new byte[100000])};
        int calls=0;bool wrong=false;
        using var api=new NpepApi("https://isolated.invalid",new Handler(async request=>{
            calls++;Assert.EndsWith("/seating/upload",request.RequestUri!.AbsolutePath);Assert.Equal("0.14",request.Headers.GetValues("X-NPEP-Version").Single());
            Assert.True((await request.Content!.ReadAsByteArrayAsync()).Length>65536);
            return new(HttpStatusCode.OK){Content=new StringContent(new JsonObject{
                ["protocolVersion"]="0.14",["requestId"]=wrong?Guid.NewGuid().ToString():body["requestId"]!.DeepClone(),
                ["serverTime"]="2026-10-11T12:00:00.000Z",["data"]=new JsonObject{["version"]=new string('a',64),["classId"]="one",["className"]="本班",["revision"]=1,["hash"]=new string('b',64)}
            }.ToJsonString())};
        }));
        Assert.Equal(1,(await api.SeatingWriteAsync("synthetic",body,true,default))["revision"]!.GetValue<int>());
        wrong=true;await Assert.ThrowsAsync<NpepException>(()=>api.SeatingWriteAsync("synthetic",body,true,default));
        body["classId"]="foreign";await Assert.ThrowsAsync<NpepException>(()=>api.SeatingWriteAsync("synthetic",body,true,default));Assert.Equal(2,calls);
    }
}

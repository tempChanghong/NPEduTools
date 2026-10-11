using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using NPEduTools.Contracts;
using NPEduTools.Integrations.Npep;
namespace NPEduTools.Npep.Tests;
public sealed class NpepPhotoApiTests
{
    private sealed class Handler(Func<HttpRequestMessage,Task<HttpResponseMessage>> send) : HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken token)=>send(request); }
    [Fact]
    public async Task Exit_record_transport_is_private_strict_and_carries_no_password_or_picture()
    {
        var context=new JsonObject{["identity"]=new JsonObject{["serverInstanceId"]=Guid.NewGuid().ToString(),["deploymentEpoch"]=Guid.NewGuid().ToString(),["deviceId"]=Guid.NewGuid().ToString(),["bindingRevision"]=1,["credentialGeneration"]=1},
            ["runId"]=Guid.NewGuid().ToString(),["sessionId"]=Guid.NewGuid().ToString(),["statusEpoch"]=1};
        var body=new JsonObject{["requestId"]=Guid.NewGuid().ToString(),["context"]=context,["event"]=new JsonObject{
            ["eventId"]=Guid.NewGuid().ToString(),["version"]=new string('a',64),["sessionId"]=Guid.NewGuid().ToString(),["reason"]="Timeout",
            ["deviceTime"]="2026-10-11T12:00:00.000Z",["schoolNow"]="2026-10-11T20:00:00.000",["photoEventId"]=null}};
        using var api=new NpepApi("https://isolated.invalid",new Handler(async request=>{
            Assert.Equal("/api/v2/npep/device/study-settings/photos/exit-events",request.RequestUri!.AbsolutePath);
            Assert.Equal("application/json",request.Content!.Headers.ContentType!.MediaType);
            var actual=JsonNode.Parse(await request.Content.ReadAsStringAsync())!.AsObject();Assert.True(JsonNode.DeepEquals(body,actual));
            Assert.False(request.Headers.Contains("X-NPEP-Photo"));
            return new(HttpStatusCode.OK){Content=new StringContent(new JsonObject{["protocolVersion"]="0.15",["requestId"]=body["requestId"]!.DeepClone(),["serverTime"]="2026-10-11T12:00:00.000Z",
                ["data"]=new JsonObject{["eventId"]=body["event"]!["eventId"]!.DeepClone(),["state"]="Stored"}}.ToJsonString())};
        }));
        Assert.Equal("Stored",(await api.ExitEventAsync("synthetic-token",body,CancellationToken.None))["state"]!.GetValue<string>());
        body["event"]!["password"]="synthetic-secret";Assert.Throws<NpepException>(()=>NpepPhotoProtocol.Validate("exitRequest",body));
    }
    [Fact]
    public async Task Binary_photo_metadata_header_and_acknowledgment_are_fenced_without_real_network()
    {
        byte[] bytes=[255,216,255,217];string hash=Convert.ToHexStringLower(SHA256.HashData(bytes));string id=Guid.NewGuid().ToString();
        var metadata=JsonNode.Parse($$"""
            {"eventId":"{{id}}","leaseId":"{{Guid.NewGuid()}}","sessionId":"{{Guid.NewGuid()}}","version":"{{new string('a',64)}}",
            "capturedAt":"2026-10-11T12:00:00.000Z","schoolNow":"2026-10-11T20:00:00.000","reason":"ExitAttempt","unit":"Dbfs",
            "level":null,"high":-30,"continuousSeconds":3,"calibrationId":null,"hash":"{{hash}}","width":2,"height":2,"byteCount":4}
            """)!.AsObject();
        string mode="ok";
        using var api=new NpepApi("https://isolated.invalid",new Handler(async request=>{
            Assert.Equal("/api/v2/npep/device/study-settings/photos/upload",request.RequestUri!.AbsolutePath);
            Assert.Equal("0.15",request.Headers.GetValues("X-NPEP-Version").Single());
            Assert.Equal("image/jpeg",request.Content!.Headers.ContentType!.MediaType);Assert.Equal(bytes,await request.Content.ReadAsByteArrayAsync());
            string h=request.Headers.GetValues("X-NPEP-Photo").Single().Replace('-','+').Replace('_','/');h=h.PadRight((h.Length+3)/4*4,'=');
            Assert.True(JsonNode.DeepEquals(metadata,JsonNode.Parse(Encoding.UTF8.GetString(Convert.FromBase64String(h)))));
            if(mode=="redirect")return new(HttpStatusCode.TemporaryRedirect);
            if(mode=="unsupported")return new(HttpStatusCode.NotFound);
            if(mode=="large")return new(HttpStatusCode.OK){Content=new StringContent(new string('x',16385))};
            return new(HttpStatusCode.OK){Content=new StringContent(new JsonObject{
                ["protocolVersion"]=mode=="wrong-version"?"0.14":"0.15",["requestId"]=request.Headers.GetValues("X-Request-Id").Single(),["serverTime"]="2026-10-11T12:00:00.000Z",
                ["data"]=new JsonObject{["eventId"]=id,["state"]="Stored",["storedHash"]=hash}}.ToJsonString())};
        }));
        var packet=new StudyPhotoPacket("scope","camera","mic",metadata,bytes);
        Assert.Equal("Stored",(await api.PhotoUploadAsync("synthetic-token",packet,CancellationToken.None))["state"]!.GetValue<string>());
        foreach(var (kind,code) in new[]{("redirect","REDIRECT_REJECTED"),("unsupported","STUDY_PHOTOS_UNSUPPORTED"),("large","INVALID_RESPONSE"),("wrong-version","INVALID_RESPONSE")})
        { mode=kind;Assert.Equal(code,(await Assert.ThrowsAsync<NpepException>(()=>api.PhotoUploadAsync("synthetic-token",packet,CancellationToken.None))).Code); }
    }
}

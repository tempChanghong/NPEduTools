using System.Net;
using System.Text.Json.Nodes;
using NPEduTools.Integrations.Npep;

namespace NPEduTools.Npep.Tests;

public sealed partial class RuntimeTests
{
    private sealed class PresentationFixture(int remaining) : INpepStudySettings
    {
        public void BindStudy(string? scope) { }
        public void ApplyStudy(JsonObject configuration) { }
        public JsonObject? ObserveStudy() => null;
        public void StudyFailed(string code) => throw new InvalidOperationException("Display failure cannot stop capture.");
        public JsonObject? ObserveStudyPresentation() => new() { ["version"] = new string('a',64), ["status"] = new JsonObject {
            ["state"] = "Visible", ["reason"] = "FRAME_CURRENT", ["phase"] = "Active", ["clockReady"] = true,
            ["schoolNow"] = "2026-10-12T19:10:00.000", ["window"] = new JsonObject { ["start"] = "2026-10-12T19:00:00.000", ["end"] = "2026-10-12T21:30:00.000" },
            ["hostInstanceId"] = NpepProtocol.Id(), ["appInstanceId"] = NpepProtocol.Id(), ["pulse"] = 1, ["remainingMs"] = remaining } };
    }
    [Theory]
    [InlineData("valid")] [InlineData("expired")] [InlineData("unsupported")] [InlineData("version")] [InlineData("pause")]
    public async Task Study_presentation_uses_independent_protocol_and_fences_late_results(string mode)
    {
        using var dir=new TestDirectory();var server=new FakeServer();NpepRuntime? active=null;int messages=0;
        async Task<HttpResponseMessage> Send(HttpRequestMessage request)
        {
            if(!request.RequestUri!.AbsolutePath.EndsWith("/study-settings/presentation",StringComparison.Ordinal)) return await server.Send(request);
            messages++;Assert.Equal("0.13",request.Headers.GetValues("X-NPEP-Version").Single());
            var body=JsonNode.Parse(await request.Content!.ReadAsStringAsync())!.AsObject();NpepStudyProtocol.Validate("presentationRequest",body);
            Assert.Equal(mode=="expired"?"Unknown":"Visible",body["status"]!.AsObject().Text("state"));
            Assert.Equal(mode=="expired"?0:4000,body["status"]!["remainingMs"]!.GetValue<int>());
            Assert.False(body["context"]!.AsObject().ContainsKey("controlEpoch"));
            if(mode=="pause")Assert.Equal("Accepted",active!.Handle(new(1,Guid.NewGuid(),"npep.command",Npep:new("pause",active.Snapshot().Revision))).Outcome);
            var reply=Fixtures.Reply(request,new() { ["confirmed"]=true,["version"]=body["version"]!.DeepClone() });
            var envelope=JsonNode.Parse(await reply.Content.ReadAsStringAsync())!.AsObject();envelope["protocolVersion"]=mode=="version"?"0.10":"0.13";
            reply.Content=new StringContent(envelope.ToJsonString());if(mode=="unsupported")reply.StatusCode=HttpStatusCode.NotFound;return reply;
        }
        await using var runtime=new NpepRuntime(()=>new(dir.Path,origin=>new(origin,new Handler(Send))),"presentation-test",Sample);active=runtime;
        await Command(runtime,"inspect");await Command(runtime,"pair",server);await Command(runtime,"poll");await Command(runtime,"confirm",server);
        await Until(()=>runtime.Snapshot().Connection=="ONLINE");var study=new PresentationFixture(mode=="expired"?500:5000);
        if(mode is "valid" or "expired")await runtime.StudyPresentationCycleAsync(study,CancellationToken.None);
        else {var error=await Assert.ThrowsAsync<NpepException>(()=>runtime.StudyPresentationCycleAsync(study,CancellationToken.None));
            Assert.Equal(mode=="unsupported"?"STUDY_PRESENTATION_UNSUPPORTED":mode=="pause"?"SESSION_SUPERSEDED":"INVALID_RESPONSE",error.Code);}
        Assert.Equal(1,messages);
    }
}

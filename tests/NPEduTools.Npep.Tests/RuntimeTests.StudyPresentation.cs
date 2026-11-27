using System.Net;
using System.Text.Json.Nodes;
using NPEduTools.Integrations.Npep;

namespace NPEduTools.Npep.Tests;

public sealed partial class RuntimeTests
{
    private sealed class PresentationFixture(int remaining, bool enabled = false) : INpepStudySettings
    {
        private readonly string _host = NpepProtocol.Id(), _app = NpepProtocol.Id();
        private int _pulse;
        public bool SupportsPresentation => enabled;
        public void BindStudy(string? scope) { }
        public void ApplyStudy(JsonObject configuration) { }
        public JsonObject? ObserveStudy() => null;
        public void StudyFailed(string code)
        { if (!enabled) throw new InvalidOperationException("Display failure cannot stop capture."); }
        public JsonObject? ObserveStudyPresentation() => new() { ["version"] = new string('a',64), ["status"] = new JsonObject {
            ["state"] = "Visible", ["reason"] = "FRAME_CURRENT", ["phase"] = "Active", ["clockReady"] = true,
            ["schoolNow"] = "2026-10-12T19:10:00.000", ["window"] = new JsonObject { ["start"] = "2026-10-12T19:00:00.000", ["end"] = "2026-10-12T21:30:00.000" },
            ["hostInstanceId"] = _host, ["appInstanceId"] = _app, ["pulse"] = ++_pulse, ["remainingMs"] = remaining } };
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

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task Slow_presentation_does_not_delay_heartbeat_or_carry_a_late_reply_across_pause(bool denied)
    {
        using var directory = new TestDirectory();
        var server = new FakeServer();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        NpepDevice? device = null;
        async Task<HttpResponseMessage> Send(HttpRequestMessage request)
        {
            if (!request.RequestUri!.AbsolutePath.EndsWith("/study-settings/presentation", StringComparison.Ordinal))
                return await server.Send(request);
            entered.TrySetResult();
            await release.Task;
            return await PresentationReply(request, denied ? "AUTH_INVALID" : null, denied ? 401 : 200);
        }
        using (var paired = new NpepDevice(directory.Path, server.Api)) await server.ActivateAsync(paired);
        await using var runtime = new NpepRuntime(() => device = new(directory.Path, origin => new(origin, new Handler(Send))),
            "presentation-slow", Sample);
        await Until(() => runtime.Snapshot().Connection == "ONLINE");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(12));
        var observation = runtime.StudyPresentationCycleAsync(new PresentationFixture(5000), timeout.Token);
        try
        {
            await entered.Task.WaitAsync(timeout.Token);
            var status = await device!.ReportAsync(NpepStatusReader.Map(Sample(), "presentation-slow"), 0, timeout.Token)
                .WaitAsync(TimeSpan.FromSeconds(2));
            Assert.Equal(2, status.Number("acceptedSequence"));
            Assert.True((await Command(runtime, "pause")).ReportingPaused);
            Assert.False((await Command(runtime, "resume")).ReportingPaused);
            release.TrySetResult();
            var error = await Assert.ThrowsAsync<NpepException>(() => observation);
            Assert.Equal("SESSION_SUPERSEDED", error.Code);
            Assert.Equal("ACTIVE", device.View().Text("state"));
        }
        finally
        {
            release.TrySetResult();
            try { await observation; } catch (Exception e) when (e is NpepException or OperationCanceledException) { }
        }
    }

    [Fact]
    public async Task Presentation_retry_after_resamples_instead_of_replaying_the_old_pulse()
    {
        using var directory = new TestDirectory();
        var server = new FakeServer();
        var time = new ReceiptTime();
        var first = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var attempts = new System.Collections.Concurrent.ConcurrentQueue<(string Request, long Sequence, long Pulse)>();
        async Task<HttpResponseMessage> Send(HttpRequestMessage request)
        {
            string path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith("/study-settings", StringComparison.Ordinal))
            {
                var configuration = StudyProtocolTests.Configuration;
                configuration["identity"] = NpepDevice.RegistrationIdentity(server.Registration());
                return await HomeworkReply(request, configuration, version: "0.10");
            }
            if (!path.EndsWith("/study-settings/presentation", StringComparison.Ordinal)) return await server.Send(request);
            var body = JsonNode.Parse(await request.Content!.ReadAsStringAsync())!.AsObject();
            attempts.Enqueue((body.Text("requestId"), body.Number("sequence"), body["status"]!.AsObject().Number("pulse")));
            if (attempts.Count != 1) return await PresentationReply(request);
            var limited = await PresentationReply(request, "RATE_LIMITED", 429);
            limited.Headers.RetryAfter = new(TimeSpan.FromSeconds(9));
            first.SetResult();
            return limited;
        }
        using (var paired = new NpepDevice(directory.Path, server.Api)) await server.ActivateAsync(paired);
        await using var runtime = new NpepRuntime(() => new(directory.Path, origin => new(origin, new Handler(Send))),
            "presentation-retry", Sample, study: new PresentationFixture(5000, enabled: true), receiptTime: time);
        await first.Task.WaitAsync(TimeSpan.FromSeconds(12));
        await Task.Delay(1200);
        time.Advance(TimeSpan.FromSeconds(6));
        await Task.Delay(1200);
        Assert.Single(attempts);
        time.Advance(TimeSpan.FromSeconds(3));
        await Until(() => attempts.Count >= 2);
        var messages = attempts.ToArray();
        Assert.Equal(1, messages[0].Sequence);
        Assert.Equal(2, messages[1].Sequence);
        Assert.NotEqual(messages[0].Request, messages[1].Request);
        Assert.Equal(1, messages[0].Pulse);
        Assert.Equal(2, messages[1].Pulse);
    }

    private static async Task<HttpResponseMessage> PresentationReply(HttpRequestMessage request, string? error = null, int status = 200)
    {
        var body = JsonNode.Parse(await request.Content!.ReadAsStringAsync())!.AsObject();
        var reply = Fixtures.Reply(request, new() { ["confirmed"] = true, ["version"] = body["version"]!.DeepClone() }, error, status);
        using var content = reply.Content;
        var envelope = JsonNode.Parse(await content.ReadAsStringAsync())!.AsObject();
        envelope["protocolVersion"] = "0.13";
        reply.Content = new StringContent(envelope.ToJsonString());
        return reply;
    }
}

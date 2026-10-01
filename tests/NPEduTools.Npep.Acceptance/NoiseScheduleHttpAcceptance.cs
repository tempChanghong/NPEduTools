using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;
using NPEduTools.Contracts;
using NPEduTools.Core;
using NPEduTools.Host;
using NPEduTools.Integrations.Npep;

internal static class NoiseScheduleHttpAcceptance
{
    internal static async Task<int> RunAsync(string directory,string origin,string screenToken,string path,
        Func<string,string,JsonObject?,Task<JsonObject>> admin,Func<HostResponse> sample)
    {
        using var http=new HttpClient {BaseAddress=new Uri(origin+"/api/v2/npep/"),Timeout=TimeSpan.FromSeconds(10)};
        http.DefaultRequestHeaders.Add("X-Classworks-Screen-Token",screenToken);
        async Task<JsonObject> Screen(string endpoint="noise-schedule",JsonObject? body=null)
        {
            using var request=new HttpRequestMessage(body is null?HttpMethod.Get:HttpMethod.Post,"screen/"+endpoint);
            request.Headers.Add("X-NPEP-Version",endpoint.StartsWith("noise-schedule",StringComparison.Ordinal)?"0.7":"0.6");
            request.Headers.Add("X-Request-Id",body?.Text("requestId")??NpepProtocol.Id());
            if(body is not null) request.Content=new StringContent(body.ToJsonString(),Encoding.UTF8,"application/json");
            using var response=await http.SendAsync(request);
            var reply=NpepProtocol.Parse(await response.Content.ReadAsByteArrayAsync());
            if(!response.IsSuccessStatusCode) throw new NpepException(reply["error"]!["code"]!.GetValue<string>());
            return reply["data"]!.AsObject();
        }
        async Task<JsonObject> Wait(Func<JsonObject,bool> condition,string endpoint="noise-schedule")
        {
            using var stop=new CancellationTokenSource(TimeSpan.FromSeconds(40));
            while(true) {var value=await Screen(endpoint);if(condition(value))return value;await Task.Delay(100,stop.Token);}
        }
        int starts=0; long sequence=0; var elapsed=Stopwatch.StartNew(); var bridge=Guid.NewGuid();
        await using var noise=new NoiseService(_=>{Interlocked.Increment(ref starts);return new Capture();},()=>[]);
        var s=noise.Snapshot(); noise.Handle(new(Protocol.Version,Guid.NewGuid(),"noise.command",Noise:new("select",s.InstanceId,s.Revision,"synthetic")));
        var transport=new NoiseTransport(noise,directory);
        await using var schedules=new NoiseScheduleService(noise,()=>new(bridge,bridge,Interlocked.Increment(ref sequence),0,
            new DateTimeOffset(2026,10,1,19,0,0,TimeSpan.Zero).Add(elapsed.Elapsed),0,"Advancing","synthetic school calendar"),()=>false,directory,bindReports:transport.Bind);
        await using var runtime=new NpepRuntime(directory,"schedule-acceptance",sample,noise:transport,schedules:schedules);
        var view=await Wait(v=>v["online"]?.GetValue<bool>()==true&&v["applied"]?.GetValue<bool>()==true&&v["status"]?["owner"]?.GetValue<string>()=="Schedule");
        if(starts!=1 || noise.Snapshot().State!="Active") throw new Exception("Automatic capture not started exactly once");
        var manual=await Wait(v=>v["online"]?.GetValue<bool>()==true&&v["status"]?["state"]?.GetValue<string>()=="Active","noise");
        var command=new JsonObject {["requestId"]=NpepProtocol.Id(),["action"]="STOP",["durationSeconds"]=60,
            ["instanceId"]=manual["status"]!["instanceId"]!.DeepClone(),["revision"]=manual["status"]!["revision"]!.DeepClone(),["sessionId"]=manual["status"]!["sessionId"]!.DeepClone()};
        await Screen("noise/commands",command);
        view=await Wait(v=>v["status"]?["reason"]?.GetValue<string>()=="WINDOW_SKIPPED");
        await Wait(v=>v["reports"]!.AsArray().Count==1,"noise");
        if(starts!=1) throw new Exception("User stop restarted automatic capture");
        var resume=new JsonObject {["requestId"]=NpepProtocol.Id(),["version"]=view["policy"]!["version"]!.DeepClone(),["window"]=view["status"]!["window"]!.DeepClone()};
        var first=await Screen("noise-schedule/resume",resume);
        var duplicate=await Screen("noise-schedule/resume",resume);
        if(!JsonNode.DeepEquals(first,duplicate)) throw new Exception("Resume not idempotent");
        view=await Wait(v=>v["status"]?["owner"]?.GetValue<string>()=="Schedule"&&v["commands"]![0]!["receipt"]?["outcome"]?.GetValue<string>()=="ACCEPTED");
        if(starts!=2 || view["sessions"]!.AsArray().Count!=2) throw new Exception("Missing schedule source metadata");
        var management=await admin(path+"/noise-schedule","0.7",null);
        if(management["applied"]?.GetValue<bool>()!=true) throw new Exception("Missing management applied state");
        Console.WriteLine("PASS NOISE SCHEDULE: real 0.7 .NET/Node/PostgreSQL policy delivery, school-clock automatic capture, 0.6 manual stop/report, sticky skip, idempotent resume and schedule metadata (synthetic audio only)");
        return 0;
    }
    private sealed class Capture : INoiseCapture
    {
        private Timer? _timer;
        public string DeviceName=>"Synthetic schedule microphone";
        public event Action<NoiseFrame>? Frame;
        public event Action<string>? Failed {add{}remove{}}
        public void Start()=>_timer=new(_=>Frame?.Invoke(new(0.1,0.25,0.5,0,false)),null,0,100);
        public void Dispose()=>_timer?.Dispose();
    }
}

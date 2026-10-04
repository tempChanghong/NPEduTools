using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;
using NPEduTools.Contracts;
using NPEduTools.Core;
using NPEduTools.Host;
using NPEduTools.Integrations.Npep;

internal static class NoiseScheduleHttpAcceptance
{
    internal static async Task<int> RunAsync(string directory,string origin,string screenToken,string screenPin,string path,
        Func<string,string,JsonObject?,Task<JsonObject>> admin,Func<HostResponse> sample)
    {
        using var http=new HttpClient {BaseAddress=new Uri(origin+"/api/v2/npep/"),Timeout=TimeSpan.FromSeconds(10)};
        http.DefaultRequestHeaders.Add("X-Classworks-Screen-Token",screenToken);
        async Task<JsonObject> Screen(string endpoint="noise-schedule",JsonObject? body=null)
        {
            using var request=new HttpRequestMessage(body is null?HttpMethod.Get:HttpMethod.Post,"screen/"+endpoint);
            request.Headers.Add("X-NPEP-Version",endpoint == "noise-display/presence" ? "0.9" : endpoint.StartsWith("noise-display",StringComparison.Ordinal)?"0.8":endpoint.StartsWith("noise-management",StringComparison.Ordinal)?"0.8":endpoint.StartsWith("noise-schedule",StringComparison.Ordinal)?"0.7":"0.6");
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
            try { while(true) {var value=await Screen(endpoint);if(condition(value))return value;await Task.Delay(100,stop.Token);} }
            catch(OperationCanceledException) when(stop.IsCancellationRequested)
            {throw new NpepException("SCHEDULE_WAIT_"+endpoint.ToUpperInvariant().Replace('-', '_')+"_TIMEOUT");}
        }
        int starts=0; long sequence=0; var elapsed=Stopwatch.StartNew(); var bridge=Guid.NewGuid();
        var localManagement = new NoiseManagementStore(directory);
        if (localManagement.Configure(null, "isolated-teacher-only", false) is not null) throw new Exception("Local management provisioning failed");
        await using var noise=new NoiseService(_=>{Interlocked.Increment(ref starts);return new Capture();},()=>[],management:localManagement);
        var s=noise.Snapshot(); noise.Handle(new(Protocol.Version,Guid.NewGuid(),"noise.command",Noise:new("select",s.InstanceId,s.Revision,"synthetic")));
        var transport=new NoiseTransport(noise,directory);
        await using var schedules=new NoiseScheduleService(noise,()=>new(bridge,bridge,Interlocked.Increment(ref sequence),0,
            new DateTimeOffset(2026,10,1,19,0,0,TimeSpan.Zero).Add(elapsed.Elapsed),0,"Advancing","synthetic school calendar"),()=>false,directory,bindReports:transport.Bind);
        transport.ManagementWindow = schedules.ProtectionWindow;
        var display = new NoiseDisplayService(() => {
            var state = noise.Snapshot(); var w = schedules.ProtectionWindow(state.SessionId);
            return (state, w is null ? null : new SchoolNoiseWindow(DateTimeOffset.Parse(w.Text("start") + "Z"), DateTimeOffset.Parse(w.Text("end") + "Z")));
        }, () => false, directory);
        transport.Display = display;
        await using var runtime=new NpepRuntime(directory,"schedule-acceptance",sample,noise:transport,schedules:schedules);
        var view=await Wait(v=>v["online"]?.GetValue<bool>()==true&&v["applied"]?.GetValue<bool>()==true&&v["status"]?["owner"]?.GetValue<string>()=="Schedule");
        if(starts!=1 || noise.Snapshot().State!="Active") throw new Exception("Automatic capture not started exactly once");
        var manual=await Wait(v=>v["online"]?.GetValue<bool>()==true&&v["status"]?["state"]?.GetValue<string>()=="Active","noise");
        using (var waitDisplay = new CancellationTokenSource(TimeSpan.FromSeconds(35)))
            while (display.Snapshot().Phase == "NEGOTIATING") await Task.Delay(100, waitDisplay.Token);
        if (display.Snapshot().ErrorCode is not null) throw new Exception("0.9 display observation did not negotiate: " + display.Snapshot().ErrorCode);
        string displayTab = NpepProtocol.Id();
        async Task Heartbeat(string state, int number)
        {
            var actual = noise.Snapshot();
            await Screen("noise-display/presence", new() { ["requestId"] = NpepProtocol.Id(), ["displaySessionId"] = displayTab,
                ["sequence"] = number, ["state"] = state, ["instanceId"] = actual.InstanceId.ToString("D"),
                ["revision"] = actual.Revision, ["captureSessionId"] = actual.SessionId!.Value.ToString("D"),
                ["window"] = view["status"]!["window"]!.DeepClone() });
        }
        async Task WaitDisplay(string expected)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(35));
            try { while (display.Snapshot().Phase != expected) await Task.Delay(100, timeout.Token); }
            catch (OperationCanceledException) { throw new Exception($"0.9 expected {expected}, got {display.Snapshot().Phase}: {display.Snapshot().ErrorCode}"); }
        }
        await Heartbeat("DISPLAY_VISIBLE", 1); await WaitDisplay("WEB_VISIBLE");
        await Heartbeat("BLOCKED", 2); await WaitDisplay("BLOCKED");
        await Heartbeat("DISPLAY_VISIBLE", 3); await WaitDisplay("WEB_VISIBLE");
        var nativeReturn = new HostRequest(1, Guid.NewGuid(), "noise.display.return", NoiseDisplay:
            new("return", noise.Snapshot().InstanceId, noise.Snapshot().SessionId!.Value));
        if (display.Handle(nativeReturn).Outcome != "Succeeded") throw new Exception("Native local return failed");
        using (var returnTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(35)))
            while (display.PendingReturn() is not null) await Task.Delay(100, returnTimeout.Token);
        var webReturn = await Screen("noise-display");
        var lease = webReturn["activeReturn"]!.AsObject();
        var duplicateReturn = await Screen("noise-display/return", new() { ["requestId"] = NpepProtocol.Id(), ["window"] = lease["window"]!.DeepClone() });
        if (!JsonNode.DeepEquals(lease["expiresAt"], duplicateReturn["activeReturn"]!["expiresAt"]) || noise.Snapshot().State != "Active" || starts != 1)
            throw new Exception("Display return changed deadline or microphone capture");
        Console.WriteLine("PASS NOISE DISPLAY: real C#/KV/PostgreSQL 0.9 visible/blocked heartbeats, native return registration, web/native shared deadline and capture continuity");
        var command=new JsonObject {["requestId"]=NpepProtocol.Id(),["action"]="STOP",["durationSeconds"]=60,
            ["instanceId"]=manual["status"]!["instanceId"]!.DeepClone(),["revision"]=manual["status"]!["revision"]!.DeepClone(),["sessionId"]=manual["status"]!["sessionId"]!.DeepClone()};
        bool rejected = false;
        try { await Screen("noise/commands",command); }
        catch (NpepException e) when (e.Code == "MANAGEMENT_REQUIRED") { rejected = true; }
        if (!rejected || noise.Snapshot().State != "Active") throw new Exception("Legacy STOP bypassed scheduled protection");
        var managedCommand = command.Copy(); managedCommand.Remove("requestId");
        var protectedStop = new JsonObject { ["requestId"] = command["requestId"]!.DeepClone(), ["command"] = managedCommand, ["pin"] = screenPin };
        // The receipt remains separate from actual release and durable WINDOW_SKIPPED.
        await Screen("noise-management/commands", protectedStop);
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
        Console.WriteLine("PASS NOISE SCHEDULE: real .NET/Node/PostgreSQL 0.6/0.7 statistics and schedule, 0.8 school PIN STOP authorization, legacy STOP rejected, sticky skip, idempotent resume and source metadata (synthetic audio only)");
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

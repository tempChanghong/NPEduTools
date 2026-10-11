using System.Text.Json.Nodes;
using NPEduTools.Contracts;
using NPEduTools.Host;
using NPEduTools.Integrations.Npep;

internal static class StudyHttpAcceptance
{
    private sealed class Clock : TimeProvider
    {
        public long Ms;
        public override long TimestampFrequency => 1000;
        public override long GetTimestamp() => Ms;
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) => new NoTimer();
        private sealed class NoTimer : ITimer
        {
            public bool Change(TimeSpan dueTime, TimeSpan period) => true;
            public void Dispose() { }
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }
    private sealed class Capture : INoiseCapture
    {
        public string DeviceName => "Synthetic acceptance capture; no hardware";
        public event Action<NPEduTools.Core.NoiseFrame>? Frame;
        public event Action<string>? Failed { add { } remove { } }
        public void Start() { }
        public void Sample(double dbfs = -60) => Frame?.Invoke(new(0.1, Math.Pow(10, dbfs / 10), Math.Pow(10, dbfs / 20), 0, false));
        public void Dispose() { }
    }
    private static async Task Until(Func<bool> condition)
    { using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(4)); while (!condition()) await Task.Delay(10, deadline.Token); }
    public static async Task<int> RunAsync(string fixturePath, string resultPath)
    {
        var checks = new List<string>();
        void Check(bool valid, string label) { if (!valid) throw new InvalidDataException(label); checks.Add(label); }
        string directory = Path.Combine(Path.GetTempPath(), "npep-study-acceptance-" + Guid.NewGuid().ToString("N"));
        try
        {
            var fixture = NpepProtocol.Parse(await File.ReadAllBytesAsync(fixturePath));
            string origin = NpepApi.ValidateOrigin(fixture.Text("origin"));
            if (new Uri(origin).Host != "127.0.0.1" || !new Uri(origin).IsLoopback || fixture["isolatedStudyFixture"]?.GetValue<bool>() != true)
                throw new InvalidDataException("ISOLATED_LOOPBACK_REQUIRED");
            var time = new Clock(); long sequence = 0; var connection = Guid.NewGuid(); var bridge = Guid.NewGuid();
            bool frozen = false; var captures = new List<Capture>(); long receiptSequence = 0, executionSequence = 0, resumeSequence = 0;
            SchoolClockFrame SchoolClock() => new(connection,bridge,++sequence,0,
                new DateTimeOffset(2026,10,12,18,58,0,TimeSpan.Zero).AddMilliseconds(frozen?0:time.Ms),0,"Advancing","synthetic school clock");
            await using var noise = new NoiseService(_ => { var capture = new Capture(); lock(captures) captures.Add(capture); return capture; }, () => [], time);
            var initial = noise.Snapshot(); noise.Handle(new(Protocol.Version,Guid.NewGuid(),"noise.command",Noise:new("select",initial.InstanceId,initial.Revision,"synthetic")));
            var transport = new NoiseTransport(noise,directory);
            await using var scheduler = new NoiseScheduleService(noise,SchoolClock,()=>false,directory,time,false,transport.Bind,()=>transport.Available);
            await using var study = new StudySettingsService(SchoolClock,()=>false,time,false,scheduler);
            var display = new StudyDisplayService(study.DisplaySnapshot,time,noise.StudyThresholds,directory);
            study.PresentationObservation=display.ObservePresentation;
            using var api = new NpepApi(origin); string bearer = fixture.Text("deviceBearer");
            var configuration = await api.StudySettingsAsync(bearer,CancellationToken.None);
            Check(configuration["effective"]!.AsObject().Text("source") == "Grade","real KV grade inheritance reaches real native parser");
            study.BindStudy(configuration["identity"]!.ToJsonString());study.ApplyStudy(configuration);
            time.Ms=500;study.Tick();time.Ms=1000;study.Tick();
            Check(study.Snapshot().Phase=="Preparing","native Host computes preparation from synthetic ClassIsland frame");
            Check(display.Snapshot().Display is { Selected:true,Available:true },"new WPF display eligibility is independent of microphone start during preparation");
            var preparing = display.Snapshot();
            HostRequest StageClaim(StudyHostState state) => new(1,Guid.NewGuid(),"study.reminder",StudyReminder:
                new(state.Display!.InstanceId,state.Version!,state.Window!,state.Phase));
            Check(display.Handle(StageClaim(preparing)).Outcome=="Succeeded" && File.Exists(Path.Combine(directory,"study-stage-reminders.json")),
                "actual Host saves preparation audio claim before approval without starting capture");
            var stageRestart=new StudyDisplayService(study.DisplaySnapshot,time,noise.StudyThresholds,directory);
            Check(stageRestart.Handle(StageClaim(stageRestart.Snapshot())).Outcome=="Suppressed" && captures.Count==0,
                "reconstructed Host suppresses already granted preparation audio and keeps capture independent");
            async Task<JsonObject> Receipt(long n)
            {
                var body = new JsonObject { ["requestId"]=NpepProtocol.Id(),["context"]=fixture["context"]!.DeepClone(),["sequence"]=n,["status"]=study.ObserveStudy() };
                try { return await api.StudyReceiptAsync(bearer,body,CancellationToken.None); }
                catch (NpepException) { await File.WriteAllTextAsync(Path.Combine(Path.GetDirectoryName(resultPath)!, "invalid-receipt-status.json"), body["status"]?.ToJsonString() ?? "null"); throw; }
            }
                        async Task Execute()
            {
                await Receipt(++receiptSequence);
                var status = study.ObserveStudyExecution() ?? throw new InvalidDataException("Missing execution status");
                var reply = await api.StudyExecutionAsync(bearer,new JsonObject { ["requestId"] = NpepProtocol.Id(), ["context"] = fixture["context"]!.DeepClone(),
                    ["sequence"] = ++executionSequence, ["version"] = configuration["version"]!.DeepClone(), ["status"] = status },CancellationToken.None);
                study.ConfirmStudyExecution(configuration,reply);
            }
            Check((await Receipt(++receiptSequence))["confirmed"]!.GetValue<bool>(),"real KV accepts the native application receipt");
            Check(captures.Count == 0,"configuration receipt grants no microphone execution");
            long presentationSequence=0,pulseSequence=0;var presentationApp=Guid.NewGuid();
            async Task Present(string? ui=null)
            {
                if(ui is not null)
                {
                    var current=display.Snapshot();var result=display.Handle(new(1,Guid.NewGuid(),"study.presentation",StudyPresentation:new(
                        current.Display!.InstanceId,presentationApp,++pulseSequence,current.Version!,current.Window!,current.Phase,ui,ui=="Visible"?0:null)));
                    Check(result.Outcome=="Succeeded","synthetic App observation is accepted by actual Host display service");
                }
                var observation=study.ObserveStudyPresentation()!;
                observation["status"]!["remainingMs"]=Math.Max(0,observation["status"]!["remainingMs"]!.GetValue<int>()-1000);
                var body=new JsonObject { ["requestId"]=NpepProtocol.Id(),["context"]=fixture["context"]!.DeepClone(),["sequence"]=++presentationSequence,
                    ["version"]=observation["version"]!.DeepClone(),["status"]=observation["status"]!.DeepClone() };
                var response=await api.StudyPresentationAsync(bearer,body,CancellationToken.None);
                Check(response["confirmed"]!.GetValue<bool>(),"actual native 0.13 API posts separate display observation to real Express");
            }
            async Task<JsonObject> Management()
            {
                using var client=new HttpClient {Timeout=TimeSpan.FromSeconds(4)};
                using var request=new HttpRequestMessage(HttpMethod.Get,$"{origin}/api/v2/npep/schools/school/devices/{configuration["identity"]!.AsObject().Text("deviceId")}/study-settings");
                request.Headers.Authorization=new("Bearer",fixture.Text("adminBearer"));request.Headers.Add("X-NPEP-Version","0.10");request.Headers.Add("X-Request-Id",NpepProtocol.Id());
                using var response=await client.SendAsync(request);response.EnsureSuccessStatusCode();
                return NpepProtocol.Parse(await response.Content.ReadAsByteArrayAsync())["data"]!.AsObject();
            }
            Check(!(await Management())["presentationVerified"]!.GetValue<bool>(),"actual admin read does not confuse applied policy with visible WPF");
            await Present("Visible");var presentationView=await Management();
            Check(presentationView["presentationVerified"]!.GetValue<bool>() && !presentationView["captureVerified"]!.GetValue<bool>() && captures.Count==0,
                "synthetic visible preparation crosses actual Host/API/KV with no capture claim or hardware");
            await Present("Hidden");presentationView=await Management();
            Check(!presentationView["presentationVerified"]!.GetValue<bool>() && presentationView["presentation"]!.AsObject().Text("state")=="Hidden",
                "actual admin read distinguishes App-hidden observation from visible preparation");
            var homeworkRequest=study.StudyHomeworkRequest()!;
            using(var homeworkBuffer=new MemoryStream())
            {
                string? snapshotId=null;int chunks=1;
                for(int index=0;index<chunks;index++)
                {
                    var chunk=await api.StudyHomeworkAsync(bearer,new JsonObject { ["requestId"]=NpepProtocol.Id(),["context"]=fixture["context"]!.DeepClone(),
                        ["version"]=configuration["version"]!.DeepClone(),["date"]=homeworkRequest["date"]!.DeepClone(),["snapshotId"]=snapshotId,["index"]=index },CancellationToken.None);
                    snapshotId=chunk.Text("snapshotId");chunks=chunk["count"]!.GetValue<int>();homeworkBuffer.Write(Convert.FromBase64String(chunk.Text("data")));
                }
                var homeworkBytes=homeworkBuffer.ToArray();study.ApplyStudyHomework(NpepProtocol.Parse(homeworkBytes),homeworkBytes,snapshotId!);
                Check(chunks>1 && StudyHomeworkContract.Parse(homeworkBytes).Items[0].Content==string.Concat(Enumerable.Repeat("长作业🙂\n",4000)),"real native 0.12 API receives complete multibyte homework from actual Express in multiple bounded chunks");
                Check(study.Snapshot().Homework is { State:"READY",Date:"2026-10-12" } && captures.Count==0,"homework read populates Host display cache without starting microphone");
            }
            await Execute(); Check(captures.Count == 0,"confirmed execution does not collect during preparation");
            for(int i=0;i<238;i++)
            {
                time.Ms+=500;
                if(i%20==0) { configuration = await api.StudySettingsAsync(bearer,CancellationToken.None); study.ApplyStudy(configuration); await Execute(); }
                study.Tick(); scheduler.Tick();
                if(noise.Snapshot().State == "Active") captures[^1].Sample();
            }
            Check(study.Snapshot().Phase=="Active","native same-source start boundary reaches active policy phase");
            Check(study.ObserveStudy()!.Text("captureState")=="NOT_INTEGRATED" && study.Snapshot().PresentationState=="NOT_INTEGRATED","configuration-only compatibility receipt does not replace actual capture evidence");
            Check((await Receipt(++receiptSequence))["confirmed"]!.GetValue<bool>(),"native active-policy receipt passes actual Express validation");
            await Until(() => noise.Snapshot().State == "Active"); captures[^1].Sample(); scheduler.Tick();
            Check(captures.Count == 1 && study.ObserveStudyExecution()!["signalFresh"]!.GetValue<bool>(),"one scheduled owner reports actual synthetic frames independently of configuration");
            var shown = display.Snapshot(); var sessionBeforeHide = noise.Snapshot().SessionId;
            await Execute();await Present("Visible");presentationView=await Management();
            Check(presentationView["presentationVerified"]!.GetValue<bool>() && presentationView["captureVerified"]!.GetValue<bool>(),
                "actual admin read has separate visible and fresh-synthetic-capture facts");
            Check(study.Snapshot().Settings!.Noise is { RedLineDbfs:-40,MediumLineDbfs:-50,HighContinuousSeconds:5,DisciplineCooldownSeconds:600 },
                "actual KV configuration supplies explicit fixture thresholds and durations to native Host");
            display.Snapshot();
            void Level(double dbfs) { time.Ms+=100; study.Tick();scheduler.Tick();captures[^1].Sample(dbfs); }
            Level(-45);Check(display.Snapshot().Threshold is { Band:"Medium",DisciplineId:null },"actual synthetic capture produces silent medium-band state");
            for(int i=0;i<49;i++)Level(-35);
            Check(display.Snapshot().Threshold is { Band:"High",DisciplineId:null },"49 actual 100ms high frames do not manufacture five-second qualification");
            Level(-35);var qualified=display.Snapshot();
            Check(qualified.Threshold is { Band:"High",DisciplineId:not null },"50 actual high frames qualify native discipline offer");
            var discipline=new HostRequest(1,Guid.NewGuid(),"study.reminder",StudyReminder:new(qualified.Display!.InstanceId,qualified.Version!,qualified.Window!,
                "Discipline",qualified.Threshold!.DisciplineId,qualified.Threshold.SessionId));
            Check(display.Handle(discipline).Outcome=="Succeeded" && display.Handle(discipline).Outcome=="Rejected" && display.Snapshot().Threshold!.DisciplineId is null,
                "actual Host consumes one discipline claim and enforces configured cooldown across clients");
            Level(-60);Check(display.Snapshot().Threshold is { Band:"None",DisciplineId:null } && captures.Count==1 && noise.Snapshot().SessionId==sessionBeforeHide,
                "quiet frame removes threshold effect without changing the scheduled capture owner");
            Check(File.Exists(Path.Combine(directory,"study-reminder-state.json")),"actual Host persists cooldown before discipline authorization");
            await StudyReminderRecoveryAcceptance.VerifySeparateProcessesAsync(directory,configuration["identity"]!.ToJsonString(),preparing.Window,"Preparing");
            Check(true,"two separate native processes recover the same durable cooldown without modifying journal or starting hardware/network");
            configuration=await api.StudySettingsAsync(bearer,CancellationToken.None);study.ApplyStudy(configuration);await Execute();
            display=new StudyDisplayService(study.DisplaySnapshot,time,noise.StudyThresholds,directory);study.PresentationObservation=display.ObservePresentation;
            display.Snapshot();
            for(int i=0;i<50;i++)Level(-35);
            var recovered=display.Snapshot();
            Check(recovered.Threshold is { Band:"High",DisciplineId:null,ReminderError:null } && captures.Count==1 && noise.Snapshot().SessionId==sessionBeforeHide,
                "reconstructed actual display owner retains cooldown for fresh qualified frames without changing native capture");
            Check(display.Handle(discipline).Outcome=="Rejected","reconstructed Host rejects old discipline candidate even with matching school policy");
            Level(-60);shown=display.Snapshot();
            var hide = display.Handle(new(1,Guid.NewGuid(),"study.hide",StudyHide:new(shown.Display!.InstanceId,shown.Version!,shown.Window!)));
            Check(hide.Outcome=="Succeeded" && hide.Study!.Display!.HiddenRemainingSeconds==180 && noise.Snapshot().State=="Active" && noise.Snapshot().SessionId==sessionBeforeHide,
                "Host presentation hide leaves the same actual synthetic scheduled capture running");
            await Present("Hidden");presentationView=await Management();
            Check(!presentationView["presentationVerified"]!.GetValue<bool>() && presentationView["captureVerified"]!.GetValue<bool>() &&
                presentationView["presentation"]!.AsObject().Text("reason")=="TEMPORARY_HIDE",
                "temporary hide crosses real 0.13 API while actual synthetic microphone session stays active");
            await Execute(); Check(transport.Reports().Count == 0,"running capture has not fabricated a completed report");
            var stopping = noise.Snapshot();
            noise.Handle(new(Protocol.Version,Guid.NewGuid(),"noise.command",Noise:new("stop",stopping.InstanceId,stopping.Revision)));
            await Until(() => noise.Snapshot().State == "Stopped" && transport.Reports().Count == 1); scheduler.Tick(); await Execute();
            Check(study.ObserveStudyExecution()!.Text("reason") == "WINDOW_SKIPPED","actual scheduled stop records a persistent skip before remote resume");
            async Task<JsonObject> ResumeExchange()
            {
                return await api.StudyResumeAsync(bearer,new JsonObject { ["requestId"]=NpepProtocol.Id(),["context"]=fixture["context"]!.DeepClone(),
                    ["sequence"]=++resumeSequence,["version"]=configuration["version"]!.DeepClone(),["receipt"]=study.ObserveStudyResumeReceipt() },CancellationToken.None);
            }
            Check((await ResumeExchange())["command"] is null,"0.11 capability probe grants no automatic recovery");
            var resumeBody = new JsonObject { ["requestId"]=NpepProtocol.Id(),["version"]=configuration["version"]!.DeepClone(),
                ["window"]=study.ObserveStudyExecution()!["window"]!.DeepClone() };
            using var adminHttp = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
            async Task<JsonObject> RequestResume()
            {
                using var request = new HttpRequestMessage(HttpMethod.Post,$"{origin}/api/v2/npep/schools/school/devices/{configuration["identity"]!.AsObject().Text("deviceId")}/study-settings/resume");
                request.Headers.Authorization = new("Bearer",fixture.Text("adminBearer"));request.Headers.Add("X-NPEP-Version","0.11");
                request.Headers.Add("X-Request-Id",resumeBody.Text("requestId"));request.Content=new StringContent(resumeBody.ToJsonString(),System.Text.Encoding.UTF8,"application/json");
                using var reply=await adminHttp.SendAsync(request);reply.EnsureSuccessStatusCode();
                var envelope=NpepProtocol.Parse(await reply.Content.ReadAsByteArrayAsync());
                Check(envelope.Text("protocolVersion")=="0.11" && envelope.Text("requestId")==resumeBody.Text("requestId"),"admin resume response is versioned and correlated");
                return envelope["data"]!.DeepClone().AsObject();
            }
            var accepted=await RequestResume();
            Check(accepted["receipt"] is null && noise.Snapshot().State=="Stopped","real OWNER request acceptance does not restart capture or fabricate application");
            var delivered=await ResumeExchange();var resumeCommand=delivered["command"]!.AsObject();
            Check(JsonNode.DeepEquals(resumeCommand,accepted["command"]),"real 0.11 native transport receives the exact durable KV command");
            study.ApplyStudyResume(configuration,resumeCommand);
            await Until(() => noise.Snapshot().State=="Active" && captures.Count==2);
            Check(study.ObserveStudyResumeReceipt()!.Text("outcome")=="ACCEPTED" && !study.ObserveStudyExecution()!["signalFresh"]!.GetValue<bool>(),
                "durable Host application precedes actual fresh-frame evidence");
            captures[^1].Sample();await Execute();
            Check(study.ObserveStudyExecution()!["signalFresh"]!.GetValue<bool>(),"resumed native session verifies synthetic live frames through the actual execution endpoint");
            Check((await ResumeExchange())["command"] is null,"KV saves Host application receipt and stops command delivery");
            var repeated=await RequestResume();study.ApplyStudyResume(configuration,resumeCommand);
            Check(repeated["command"]!.AsObject().Text("commandId")==resumeCommand.Text("commandId") && captures.Count==2,
                "repeated admin request and Host delivery are idempotent without duplicate capture");
            configuration=await api.StudySettingsAsync(bearer,CancellationToken.None);study.ApplyStudy(configuration);await Execute();
            frozen=true;for(int i=0;i<8;i++){time.Ms+=500;study.Tick();scheduler.Tick();}
            Check(study.Snapshot().Phase=="Blocked","school clock freeze blocks presentation policy without system-time fallback");
            Check(display.Snapshot().Display is { Selected:true,Available:false },"clock loss cannot revive legacy display while study is selected");
            Check((await Receipt(++receiptSequence))["confirmed"]!.GetValue<bool>(),"blocked clock state reaches KV as a fresh receipt");
            try { await Until(() => noise.Snapshot().State == "Stopped" && transport.Reports().Count == 2); }
            catch (OperationCanceledException) { throw new InvalidDataException($"Clock-loss fixture: capture={noise.Snapshot().State}, reports={transport.Reports().Count}, scheduler={scheduler.Observe().Text("reason")}, statistics={transport.Available}"); }
            Check(!study.ObserveStudyExecution()!["signalFresh"]!.GetValue<bool>(),"clock loss stops scheduled capture and withdraws live-frame evidence");
            frozen=false;
            long nextRefresh=time.Ms+5000;
            while(time.Ms < 180000)
            {
                time.Ms+=500;
                if(time.Ms>=nextRefresh) { nextRefresh=time.Ms+10000;configuration=await api.StudySettingsAsync(bearer,CancellationToken.None);study.ApplyStudy(configuration);await Execute(); }
                study.Tick();scheduler.Tick();if(noise.Snapshot().State == "Active")captures[^1].Sample();
            }
            await Until(() => noise.Snapshot().State == "Stopped" && transport.Reports().Count == 3);
            Check(study.Snapshot().Phase == "Outside","native capture stops at the exclusive end boundary");
            Check(transport.Reports().Count == 3,"separate scheduled sessions produce exactly one existing statistics report each");
            await Execute();
            var noiseContext=fixture["context"]!.DeepClone().AsObject();noiseContext["controlEpoch"]=fixture["noiseControlEpoch"]!.DeepClone();
            var reportBody=new JsonObject { ["requestId"]=NpepProtocol.Id(),["context"]=noiseContext,["sequence"]=1,
                ["status"]=transport.Observe(),["reports"]=transport.Reports(),["receipts"]=transport.Receipts() };
            var uploaded=await api.NoiseAsync(bearer,reportBody,CancellationToken.None);
            Check(uploaded["acceptedReports"]!.AsArray().Count==3 && transport.Reports().Count==3,
                "actual native statistics transport uploads three reports but retains the outbox until acknowledgement");
            var replay=await api.NoiseAsync(bearer,reportBody,CancellationToken.None);
            Check(JsonNode.DeepEquals(uploaded["acceptedReports"],replay["acceptedReports"]),
                "repeated actual HTTP report delivery returns the same accepted session identifiers");
            using(var request=new HttpRequestMessage(HttpMethod.Get,$"{origin}/api/v2/npep/schools/school/devices/{configuration["identity"]!.AsObject().Text("deviceId")}/noise"))
            {
                request.Headers.Authorization=new("Bearer",fixture.Text("adminBearer"));request.Headers.Add("X-NPEP-Version","0.6");request.Headers.Add("X-Request-Id",NpepProtocol.Id());
                using var response=await adminHttp.SendAsync(request);response.EnsureSuccessStatusCode();
                var reports=NpepProtocol.Parse(await response.Content.ReadAsByteArrayAsync())["data"]!["reports"]!.AsArray();
                var sent=reportBody["reports"]!.AsArray();
                Check(reports.Count==3 && sent.All(item=>reports.Any(report=>report!.AsObject().Text("sessionId")==item!.AsObject().Text("sessionId")
                    && JsonNode.DeepEquals(report["summary"],item["summary"]))),
                    "actual OWNER report read returns exactly the uploaded session statistics without duplicate rows");
            }
            transport.Acknowledge(uploaded);
            Check(transport.Reports().Count==0 && new NoiseTransport(noise,directory).Reports().Count==0,
                "actual acknowledgement durably removes only accepted reports from the native outbox");
            study.BindStudy(null);Check(study.ObserveStudy() is null,"unbinding clears local applied state");
            await File.WriteAllTextAsync(resultPath,new JsonObject{["passed"]=checks.Count,["checks"]=new JsonArray(checks.Select(c=>JsonValue.Create(c)).ToArray()),
                ["realSensors"]=false,["realDatabase"]=false,["transport"]="actual native HTTP to actual loopback Express router"}.ToJsonString(new(){WriteIndented=true}));
            Console.WriteLine($"PASS study HTTP chain: {checks.Count} checks, no hardware or database");return 0;
        }
        catch(Exception e) when(e is NpepException or IOException or HttpRequestException or InvalidDataException)
        { Console.Error.WriteLine($"Study fixture failed after {checks.Count} checks ({checks.LastOrDefault() ?? "initial configuration"}): {e.GetType().Name}: {e.Message}\n{e.StackTrace}");return 1; }
        finally
        {
            string full = Path.GetFullPath(directory), root = Path.GetFullPath(Path.GetTempPath());
            if (full.StartsWith(root, StringComparison.OrdinalIgnoreCase) && Path.GetFileName(full).StartsWith("npep-study-acceptance-", StringComparison.Ordinal)
                && Directory.Exists(full)) Directory.Delete(full,true);
        }
    }
}

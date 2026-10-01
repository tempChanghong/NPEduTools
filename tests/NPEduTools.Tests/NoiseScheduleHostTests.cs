using System.Text.Json.Nodes;
using NPEduTools.Contracts;
using NPEduTools.Core;
using NPEduTools.Host;
using NPEduTools.Integrations.Npep;

namespace NPEduTools.Tests;

public sealed class NoiseScheduleHostTests
{
    public static IEnumerable<object[]> WireCases => JsonNode.Parse(File.ReadAllText(System.IO.Path.Combine(AppContext.BaseDirectory,"Fixtures/noise-schedule-wire-cases.json")))!.AsArray()
        .Select(c => new object[] { c!["name"]!.GetValue<string>(),c["definition"]!.GetValue<string>(),c["expected"]!.GetValue<bool>(),c["value"]!.ToJsonString() });
    [Theory] [MemberData(nameof(WireCases))]
    public void Shared_wire_cases_match_real_parser(string name,string definition,bool expected,string json)
    {
        var error=Record.Exception(()=>NpepNoiseScheduleProtocol.Validate(definition,JsonNode.Parse(json)!.AsObject()));
        if(expected) Assert.Null(error);else Assert.IsType<NpepException>(error);
        Assert.NotEmpty(name);
    }
    private sealed class Clock : TimeProvider
    {
        public long Ms;
        public override long TimestampFrequency => 1000;
        public override long GetTimestamp() => Ms;
        // Scheduler tests drive time explicitly; NoiseService's separate watchdog is
        // covered by NoiseTests. Do not let a real timer race synthetic calendar jumps.
        public override ITimer CreateTimer(TimerCallback callback,object? state,TimeSpan dueTime,TimeSpan period) => new NoTimer();
        private sealed class NoTimer : ITimer
        {
            public bool Change(TimeSpan dueTime,TimeSpan period)=>true;
            public void Dispose() { }
            public ValueTask DisposeAsync()=>ValueTask.CompletedTask;
        }
    }
    private sealed class Capture : INoiseCapture
    {
        public string DeviceName => "Synthetic; no audio hardware";
        public event Action<NoiseFrame>? Frame { add { } remove { } }
        public event Action<string>? Failed;
        public bool Disposed;
        public void Start() { }
        public void Break() => Failed?.Invoke("Disconnected");
        public void Dispose() => Disposed = true;
    }
    private sealed class Fixture : IAsyncDisposable
    {
        public readonly Clock Time = new();
        public readonly string Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "noise-schedule-" + Guid.NewGuid());
        public readonly List<Capture> Captures = [];
        public readonly NoiseService Noise;
        public NoiseScheduleService Scheduler;
        public bool Exam, Frozen;
        public bool StatisticsReady=true;
        public string Scope = "test-school-and-binding";
        public DateTimeOffset Base = new(2026, 10, 1, 19, 0, 0, TimeSpan.Zero);
        private readonly Guid _connection = Guid.NewGuid(), _bridge = Guid.NewGuid();
        private long _sequence;
        public Fixture()
        {
            Noise = new(_ => { var c = new Capture(); lock (Captures) Captures.Add(c); return c; }, () => [], Time);
            Noise.Handle(Command("select", "synthetic"));
            Scheduler = NewScheduler(); Scheduler.Bind(Scope, true); Scheduler.Confirm(Policy());
        }
        public NoiseScheduleService NewScheduler() => new(Noise, () => new(_connection,_bridge,++_sequence,0,
            Base.AddMilliseconds(Frozen ? 0 : Time.Ms),0,"Advancing","synthetic school time"), () => Exam,Path,Time,false,statisticsReady:()=>StatisticsReady);
        public HostRequest Command(string action,string? mic=null)
        {
            var s=Noise.Snapshot(); return new(Protocol.Version,Guid.NewGuid(),"noise.command",Noise:new(action,s.InstanceId,s.Revision,mic));
        }
        public JsonObject Policy(string source="Grade",int seconds=86400,bool confirmed=true) => new() {
            ["policy"]=new JsonObject { ["version"]=new string('a',64),["source"]=source,
                ["rules"]=source is "None" or "Disabled" ? new JsonArray() : JsonNode.Parse("[{\"days\":[4],\"start\":\"19:00\",\"end\":\"20:00\"}]") },
            ["leaseSeconds"]=seconds,["confirmed"]=confirmed,["command"]=null };
        public void Tick(int ms=500) { Time.Ms+=ms; Scheduler.Tick(); }
        public async Task Start() { Tick(); Tick(); Tick(); await Until(()=>Noise.Snapshot().State=="Active"); }
        public async ValueTask DisposeAsync()
        { await Scheduler.DisposeAsync(); await Noise.DisposeAsync(); if(Directory.Exists(Path)) Directory.Delete(Path,true); }
    }
    private static async Task Until(Func<bool> condition)
    { using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(4)); while(!condition()) await Task.Delay(10,deadline.Token); }
    [Fact]
    public async Task Automatic_session_starts_only_after_advancing_school_clock_and_stops_at_end()
    {
        await using var f=new Fixture(); f.Tick(); Assert.Empty(f.Captures);
        await f.Start(); Assert.Equal("Schedule",f.Scheduler.Observe()["owner"]!.GetValue<string>());
        f.Time.Ms=3600000; f.Scheduler.Tick(); await Until(()=>f.Noise.Snapshot().State=="Stopped");
        Assert.True(f.Captures[0].Disposed);
    }
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task Local_and_web_manual_stops_persist_skip_across_restart_and_policy_edits(bool web)
    {
        await using var f=new Fixture(); await f.Start();
        var s=f.Noise.Snapshot();
        if(web) f.Noise.RemoteCommand(Guid.NewGuid(),"STOP",s.InstanceId,s.Revision,s.SessionId,60);
        else f.Noise.Handle(f.Command("stop"));
        await Until(()=>f.Noise.Snapshot().State=="Stopped"); f.Tick();
        Assert.Equal("WINDOW_SKIPPED",f.Scheduler.Observe()["reason"]!.GetValue<string>());
        await f.Scheduler.DisposeAsync(); f.Scheduler=f.NewScheduler(); f.Scheduler.Bind(f.Scope,true);
        f.Tick(); Assert.Single(f.Captures); // No persisted lease can start on a new Host.
        var policy=f.Policy(); policy["policy"]!["version"]=new string('b',64);
        policy["policy"]!["rules"]![0]!["start"]="19:01";
        f.Scheduler.Confirm(policy); f.Time.Ms=120000; f.Tick(); f.Tick(); f.Tick();
        Assert.Equal("WINDOW_SKIPPED",f.Scheduler.Observe()["reason"]!.GetValue<string>());
        Assert.Single(f.Captures);
    }
    [Fact]
    public async Task Explicit_resume_clears_skip_and_duplicate_command_never_restarts_capture()
    {
        await using var f=new Fixture(); await f.Start(); f.Noise.Handle(f.Command("stop"));
        await Until(()=>f.Noise.Snapshot().State=="Stopped"); f.Tick();
        var response=f.Policy(); response["command"]=new JsonObject { ["commandId"]=Guid.NewGuid().ToString("D"),
            ["version"]=new string('a',64),["window"]=f.Scheduler.Observe()["window"]!.DeepClone() };
        f.Scheduler.Confirm(response); f.Tick(); await Until(()=>f.Captures.Count==2&&f.Noise.Snapshot().State=="Active");
        f.Scheduler.Confirm(response); f.Tick(); Assert.Equal(2,f.Captures.Count);
        Assert.Equal("ACCEPTED",f.Scheduler.Observe()["receipt"]!["outcome"]!.GetValue<string>());
    }
    [Fact]
    public async Task Exam_stop_does_not_skip_and_return_resumes_current_window()
    {
        await using var f=new Fixture(); await f.Start(); f.Exam=true; f.Tick();
        await Until(()=>f.Noise.Snapshot().State=="Stopped"); Assert.Equal("EXAM_PAUSED",f.Scheduler.Observe()["reason"]!.GetValue<string>());
        f.Exam=false; f.Tick(); await Until(()=>f.Captures.Count==2&&f.Noise.Snapshot().State=="Active");
    }
    [Fact]
    public async Task Manual_capture_is_never_stopped_by_exam_revocation_or_expiry()
    {
        await using var f=new Fixture(); f.Noise.Handle(f.Command("start","synthetic"));
        await Until(()=>f.Noise.Snapshot().State=="Active");
        f.Exam=true; f.Scheduler.Bind(null,false); f.Tick();
        Assert.Equal("Active",f.Noise.Snapshot().State); Assert.Equal("MANUAL_ACTIVE",f.Scheduler.Observe()["reason"]!.GetValue<string>());
    }
    [Fact]
    public async Task Failure_suppresses_retry_until_microphone_changes()
    {
        await using var f=new Fixture(); await f.Start(); f.Captures[0].Break();
        await Until(()=>f.Noise.Snapshot().State=="Faulted"); f.Tick(); f.Tick();
        Assert.Single(f.Captures); Assert.Equal("WINDOW_FAILED",f.Scheduler.Observe()["reason"]!.GetValue<string>());
        f.Noise.Handle(f.Command("select","other")); f.Tick(); await Until(()=>f.Captures.Count==2&&f.Noise.Snapshot().State=="Active");
    }
    [Fact]
    public async Task Frozen_school_clock_stops_auto_without_using_Windows_time()
    {
        await using var f=new Fixture(); await f.Start(); f.Frozen=true; f.Tick();
        await Until(()=>f.Noise.Snapshot().State=="Stopped"); Assert.Equal("SCHOOL_CLOCK_UNAVAILABLE",f.Scheduler.Observe()["reason"]!.GetValue<string>());
    }
    [Fact]
    public async Task Duplicate_response_does_not_renew_lease_and_disabled_policy_stops_auto()
    {
        await using var f=new Fixture(); f.Scheduler.Confirm(f.Policy(seconds:2)); await f.Start();
        f.Scheduler.Confirm(f.Policy(confirmed:false)); f.Tick(1000);
        await Until(()=>f.Noise.Snapshot().State=="Stopped"); Assert.Equal("POLICY_EXPIRED",f.Scheduler.Observe()["reason"]!.GetValue<string>());
        f.Scheduler.Confirm(f.Policy()); f.Tick(); await Until(()=>f.Noise.Snapshot().State=="Active");
        f.Scheduler.Confirm(f.Policy("Disabled")); f.Tick(); await Until(()=>f.Noise.Snapshot().State=="Stopped");
    }
    [Fact]
    public async Task Scope_change_stops_auto_and_clears_old_skips()
    {
        await using var f=new Fixture(); await f.Start(); f.Scheduler.Bind("other-class",true);
        await Until(()=>f.Noise.Snapshot().State=="Stopped"); f.Scheduler.Confirm(f.Policy()); f.Tick();
        await Until(()=>f.Captures.Count==2&&f.Noise.Snapshot().State=="Active");
    }
    [Fact]
    public async Task Reporting_pause_invalidates_lease_and_needs_online_reconfirmation()
    {
        await using var f=new Fixture();await f.Start();f.Scheduler.Bind(f.Scope,false);
        await Until(()=>f.Noise.Snapshot().State=="Stopped");f.Scheduler.Bind(f.Scope,true);f.Tick();
        Assert.Equal("POLICY_EXPIRED",f.Scheduler.Observe()["reason"]!.GetValue<string>());Assert.Single(f.Captures);
        f.Scheduler.Confirm(f.Policy());f.Tick();await Until(()=>f.Captures.Count==2);
    }
    [Fact]
    public async Task Corrupt_store_never_starts_microphone_and_is_not_overwritten()
    {
        await using var f=new Fixture();await f.Scheduler.DisposeAsync();
        var file=System.IO.Path.Combine(f.Path,"noise-schedule-state.json");await File.WriteAllTextAsync(file,"{bad-json");
        f.Scheduler=f.NewScheduler();f.Scheduler.Bind(f.Scope,true);f.Scheduler.Confirm(f.Policy());f.Tick();f.Tick();f.Tick();
        Assert.Empty(f.Captures);Assert.Equal("SCHEDULE_STORE_UNAVAILABLE",f.Scheduler.Observe()["reason"]!.GetValue<string>());
        Assert.Equal("{bad-json",await File.ReadAllTextAsync(file));
    }
    [Fact]
    public async Task Unavailable_statistics_outbox_stops_only_scheduled_capture()
    {
        await using var f=new Fixture();await f.Start();f.StatisticsReady=false;f.Tick();
        await Until(()=>f.Noise.Snapshot().State=="Stopped");Assert.Equal("STATISTICS_STORE_UNAVAILABLE",f.Scheduler.Observe()["reason"]!.GetValue<string>());
        f.Noise.Handle(f.Command("start","synthetic"));await Until(()=>f.Noise.Snapshot().State=="Active");f.Tick();
        Assert.Equal("Active",f.Noise.Snapshot().State);Assert.Equal("MANUAL_ACTIVE",f.Scheduler.Observe()["reason"]!.GetValue<string>());
    }
}

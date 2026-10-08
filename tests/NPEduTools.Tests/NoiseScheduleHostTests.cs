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
        public event Action<NoiseFrame>? Frame;
        public event Action<string>? Failed;
        public bool Disposed;
        public ManualResetEventSlim? StartGate;
        public void Start() => StartGate?.Wait();
        public void Sample() => Frame?.Invoke(new(0.1, 0.000001, 0.002, 0, false));
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
        public Fixture(ManualResetEventSlim? startGate = null)
        {
            Noise = new(_ => { var c = new Capture { StartGate = startGate }; lock (Captures) Captures.Add(c); return c; }, () => [], Time);
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

    private static void DisplayFixture(string name, NoiseTransport transport, NoiseScheduleService scheduler, bool eligible,
        JsonObject? previousSchedule = null, string? expectedPhase = null)
    {
        var noise = transport.Observe();
        var schedule = scheduler.Observe();
        NpepNoiseProtocol.Validate("status", noise);
        NpepNoiseScheduleProtocol.Validate("status", schedule);
        string? directory = Environment.GetEnvironmentVariable("NPEP_NOISE_DISPLAY_FIXTURES");
        if (string.IsNullOrWhiteSpace(directory)) return;
        Directory.CreateDirectory(directory);
        var fixture = new JsonObject { ["schemaVersion"] = 1, ["source"] = "synthetic-desktop-services",
            ["scenario"] = name, ["noiseStatus"] = noise, ["scheduleStatus"] = schedule, ["expectedAutoEligible"] = eligible };
        if (previousSchedule is not null) fixture["previousScheduleStatus"] = previousSchedule.DeepClone();
        if (expectedPhase is not null) fixture["expectedPhase"] = expectedPhase;
        File.WriteAllText(System.IO.Path.Combine(directory, name + ".json"), fixture.ToJsonString(new() { WriteIndented = true }));
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Display_end_snapshots_preserve_session_while_current_window_can_disappear(bool failed)
    {
        await using var f = new Fixture();
        var policy = f.Policy(); policy["policy"]!["rules"]![0]!["end"] = "19:01";
        f.Scheduler.Confirm(policy);
        var transport = new NoiseTransport(f.Noise, f.Path); transport.Bind(f.Scope);
        await f.Start(); f.Captures[0].Sample(); f.Tick();
        var previous = f.Scheduler.Observe(); var session = f.Noise.Snapshot().SessionId;
        if (failed) f.Captures[0].Break();
        else while (f.Time.Ms < 60000) f.Tick(); // Keep school time advancing without a discontinuous jump.
        await Until(() => f.Noise.Snapshot().State == (failed ? "Faulted" : "Stopped") && transport.Reports().Count == 1);
        f.Tick();
        var current = f.Scheduler.Observe();
        Assert.Equal(failed ? "WINDOW_FAILED" : "OUTSIDE_WINDOW", current["reason"]!.GetValue<string>());
        Assert.Equal("None", current["owner"]!.GetValue<string>());
        Assert.Null(current["sessionId"]);
        Assert.Equal(session!.Value.ToString("D"), transport.Observe()["sessionId"]!.GetValue<string>());
        if (!failed) Assert.Null(current["window"]);
        DisplayFixture(failed ? "failed" : "naturally-ended", transport, f.Scheduler, false, previous, failed ? "unknown" : "ended");
    }

    [Fact]
    public async Task Display_snapshots_correlate_start_active_stop_resume_and_manual_capture()
    {
        using var gate = new ManualResetEventSlim(false);
        await using var f = new Fixture(gate);
        var transport = new NoiseTransport(f.Noise, f.Path);
        transport.Bind(f.Scope);
        try
        {
            f.Tick(); f.Tick(); f.Tick();
            await Until(() => f.Captures.Count == 1);
            var starting = transport.Observe();
            Assert.Equal("Starting", starting["state"]!.GetValue<string>());
            Assert.Equal("CAPTURE_STARTING", f.Scheduler.Observe()["reason"]!.GetValue<string>());
            Assert.Equal(starting["sessionId"]!.GetValue<string>(), f.Scheduler.Observe()["sessionId"]!.GetValue<string>());
            DisplayFixture("starting", transport, f.Scheduler, false);

            gate.Set(); await Until(() => f.Noise.Snapshot().State == "Active");
            f.Captures[0].Sample(); f.Tick();
            var active = transport.Observe(); var scheduled = f.Scheduler.Observe();
            Assert.Equal("WINDOW_ACTIVE", scheduled["reason"]!.GetValue<string>());
            Assert.Equal(active["sessionId"]!.GetValue<string>(), scheduled["sessionId"]!.GetValue<string>());
            Assert.Equal("Good", active["quality"]!.GetValue<string>());
            Assert.Equal(-60, active["currentDbfs"]!.GetValue<double>(), 5);
            Assert.Equal("2026-10-01T19:00:02.000", scheduled["schoolNow"]!.GetValue<string>());
            Assert.Equal("2026-10-01T20:00:00.000", scheduled["window"]!["end"]!.GetValue<string>());
            DisplayFixture("active", transport, f.Scheduler, true);

            var state = f.Noise.Snapshot();
            var stop = new JsonObject { ["commandId"] = Guid.NewGuid().ToString("D"), ["action"] = "STOP",
                ["instanceId"] = state.InstanceId.ToString("D"), ["revision"] = state.Revision,
                ["sessionId"] = state.SessionId?.ToString("D"), ["durationSeconds"] = 60,
                ["expiresAt"] = DateTimeOffset.UtcNow.AddSeconds(30).ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'") };
            transport.Execute(stop, () => {});
            await Until(() => f.Noise.Snapshot().State == "Stopped" && transport.Reports().Count == 1); f.Tick();
            Assert.Equal("WINDOW_SKIPPED", f.Scheduler.Observe()["reason"]!.GetValue<string>());
            Assert.Equal("None", f.Scheduler.Observe()["owner"]!.GetValue<string>());
            Assert.Null(f.Scheduler.Observe()["sessionId"]);
            Assert.Equal(state.SessionId!.Value.ToString("D"), transport.Reports()[0]!["sessionId"]!.GetValue<string>());
            DisplayFixture("stopped", transport, f.Scheduler, false);

            var resume = f.Policy();
            resume["command"] = new JsonObject { ["commandId"] = Guid.NewGuid().ToString("D"),
                ["version"] = new string('a', 64), ["window"] = f.Scheduler.Observe()["window"]!.DeepClone() };
            f.Scheduler.Confirm(resume); f.Tick();
            await Until(() => f.Captures.Count == 2 && f.Noise.Snapshot().State == "Active");
            f.Captures[1].Sample(); f.Tick();
            Assert.NotEqual(state.SessionId, f.Noise.Snapshot().SessionId);
            Assert.Equal(transport.Observe()["sessionId"]!.GetValue<string>(), f.Scheduler.Observe()["sessionId"]!.GetValue<string>());
            DisplayFixture("resumed", transport, f.Scheduler, true);
            stop["commandId"] = Guid.NewGuid().ToString("D"); // A new delayed request for the old session, not an idempotent replay.
            transport.Execute(stop, () => {});
            Assert.Equal("REJECTED", transport.Receipts().Last()!["outcome"]!.GetValue<string>());
            Assert.Equal("Active", f.Noise.Snapshot().State);

            f.Noise.Handle(f.Command("stop")); await Until(() => f.Noise.Snapshot().State == "Stopped");
            // Stopped is published before the completion callback finishes saving its report.
            // Wait for capture ownership to be released before starting a manual session.
            await Until(() => f.Noise.Handle(f.Command("start", "synthetic")).Outcome == "Accepted");
            await Until(() => f.Noise.Snapshot().State == "Active");
            f.Tick();
            Assert.Equal("Manual", f.Scheduler.Observe()["owner"]!.GetValue<string>());
            Assert.Null(f.Scheduler.Observe()["sessionId"]);
            DisplayFixture("manual", transport, f.Scheduler, false);
        }
        finally { gate.Set(); }
    }
    [Fact]
    public async Task Automatic_session_starts_only_after_advancing_school_clock_and_stops_at_end()
    {
        await using var f=new Fixture(); f.Tick(); Assert.Empty(f.Captures);
        await f.Start(); Assert.Equal("Schedule",f.Scheduler.Observe()["owner"]!.GetValue<string>());
        f.Time.Ms=3600000; f.Scheduler.Tick(); await Until(()=>f.Noise.Snapshot().State=="Stopped");
        Assert.True(f.Captures[0].Disposed);
    }
    [Theory]
    [InlineData(false, "local")]
    [InlineData(true, "local")]
    [InlineData(false, "web")]
    [InlineData(true, "web")]
    [InlineData(false, "failure")]
    [InlineData(true, "failure")]
    public async Task Continuing_capture_uses_updated_window_for_protection_and_stop_blocks(bool shifted, string action)
    {
        await using var f = new Fixture();
        if (shifted) f.Base = new(2026, 10, 1, 19, 59, 58, TimeSpan.Zero);
        await f.Start();
        var session = f.Noise.Snapshot().SessionId;
        var policy = f.Policy();
        policy["policy"]!["version"] = new string('b', 64);
        policy["policy"]!["rules"]![0]!["start"] = shifted ? "20:00" : "19:00";
        policy["policy"]!["rules"]![0]!["end"] = "21:00";
        f.Scheduler.Confirm(policy); f.Tick();
        Assert.Equal(session, f.Noise.Snapshot().SessionId);
        Assert.Single(f.Captures);
        bool protectionMatches = JsonNode.DeepEquals(f.Scheduler.Observe()["window"], f.Scheduler.ProtectionWindow(session));

        if (action == "failure") f.Captures[0].Break();
        else if (action == "web")
        {
            var state = f.Noise.Snapshot();
            Assert.Equal("Accepted", f.Noise.RemoteCommand(Guid.NewGuid(), "STOP", state.InstanceId, state.Revision, session, 60).Outcome);
        }
        else Assert.Equal("Accepted", f.Noise.Handle(f.Command("stop")).Outcome);
        await Until(() => f.Noise.Snapshot().State == (action == "failure" ? "Faulted" : "Stopped"));
        f.Tick(); f.Tick();
        Assert.Equal(action == "failure" ? "WINDOW_FAILED" : "WINDOW_SKIPPED", f.Scheduler.Observe()["reason"]!.GetValue<string>());
        Assert.Single(f.Captures);
        Assert.True(protectionMatches, "the running capture's protection window still describes the previous policy");
        var journal = JsonNode.Parse(File.ReadAllText(System.IO.Path.Combine(f.Path, "noise-schedule-state.json")))!;
        var block = journal["blocks"]!.AsArray().Single()!["window"]!;
        Assert.Equal(shifted ? "2026-10-01T20:00:00+00:00" : "2026-10-01T19:00:00+00:00", block["start"]!.GetValue<string>());
        Assert.Equal("2026-10-01T21:00:00+00:00", block["end"]!.GetValue<string>());
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Window_update_must_be_durable_but_unchanged_ticks_do_not_rewrite_journal(bool changed)
    {
        await using var f = new Fixture(); await f.Start();
        string path = System.IO.Path.Combine(f.Path, "noise-schedule-state.json");
        byte[] original = File.ReadAllBytes(path);
        using var locked = new FileStream(path + ".tmp", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        var policy = f.Policy();
        if (changed) policy["policy"]!["rules"]![0]!["end"] = "21:00";
        f.Scheduler.Confirm(policy); f.Tick();
        Assert.Equal(changed ? "SCHEDULE_STORE_UNAVAILABLE" : "WINDOW_ACTIVE", f.Scheduler.Observe()["reason"]!.GetValue<string>());
        if (changed) await Until(() => f.Noise.Snapshot().State == "Stopped");
        else Assert.Equal("Active", f.Noise.Snapshot().State);
        Assert.Equal(original, File.ReadAllBytes(path));
        Assert.Single(f.Captures);
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
    public async Task Delayed_resume_after_original_window_end_cannot_clear_skip_or_restart_capture()
    {
        await using var f = new Fixture(); await f.Start(); f.Noise.Handle(f.Command("stop"));
        await Until(() => f.Noise.Snapshot().State == "Stopped"); f.Tick();
        var reply = f.Policy(); reply["command"] = new JsonObject { ["commandId"] = Guid.NewGuid().ToString("D"),
            ["version"] = new string('a', 64), ["window"] = f.Scheduler.Observe()["window"]!.DeepClone() };
        f.Time.Ms = 3600000; f.Tick(); f.Tick(); f.Tick();
        f.Scheduler.Confirm(reply); f.Tick();
        Assert.Equal("REJECTED", f.Scheduler.Observe()["receipt"]!["outcome"]!.GetValue<string>());
        Assert.Equal("OUTSIDE_WINDOW", f.Scheduler.Observe()["reason"]!.GetValue<string>());
        Assert.Single(f.Captures); Assert.Equal("Stopped", f.Noise.Snapshot().State);
    }

    [Fact]
    public async Task Revocation_rejects_late_policy_and_resume_reply_without_starting_capture()
    {
        await using var f = new Fixture(); await f.Start();
        var reply = f.Policy(); reply["command"] = new JsonObject { ["commandId"] = Guid.NewGuid().ToString("D"),
            ["version"] = new string('a', 64), ["window"] = f.Scheduler.Observe()["window"]!.DeepClone() };
        f.Scheduler.Bind(null, false); await Until(() => f.Noise.Snapshot().State == "Stopped");
        f.Scheduler.Confirm(reply); f.Tick(); f.Tick(); f.Tick();
        Assert.Equal("NOT_ELIGIBLE", f.Scheduler.Observe()["reason"]!.GetValue<string>());
        Assert.Null(f.Scheduler.Observe()["version"]); Assert.Single(f.Captures);
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
    [Fact]
    public async Task Hard_host_recovery_requires_new_policy_school_clock_and_clear_exam_gate()
    {
        string directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "noise-guard-recovery-" + Guid.NewGuid());
        Directory.CreateDirectory(directory);
        Guid? oldSession; JsonObject policy; string scope;
        try
        {
            await using (var original = new Fixture())
            {
                await original.Start(); oldSession = original.Noise.Snapshot().SessionId;
                policy = original.Policy(); scope = original.Scope;
                File.Copy(System.IO.Path.Combine(original.Path, "noise-schedule-state.json"), System.IO.Path.Combine(directory, "noise-schedule-state.json"));
            }
            var time = new Clock(); int captures = 0; bool clockReady = false, exam = true; long sequence = 0;
            Guid connection = Guid.NewGuid(), bridge = Guid.NewGuid();
            var start = new DateTimeOffset(2026, 10, 1, 19, 0, 0, TimeSpan.Zero);
            await using var noise = new NoiseService(_ => { captures++; return new Capture(); }, () => [], time);
            var s = noise.Snapshot(); noise.Handle(new(1, Guid.NewGuid(), "noise.command", Noise: new("select", s.InstanceId, s.Revision, "synthetic")));
            await using var schedule = new NoiseScheduleService(noise,
                () => clockReady ? new(connection, bridge, ++sequence, 0, start.AddMilliseconds(time.Ms), 0, "Advancing", "synthetic") : SchoolClockFrame.Unavailable("test"),
                () => exam, directory, time, false, statisticsReady: () => true);
            schedule.Bind(scope, true);
            void Tick() { time.Ms += 500; schedule.Tick(); }
            Tick(); Tick(); Tick(); Assert.Equal(0, captures);
            schedule.Confirm(policy); Tick(); Tick(); Assert.Equal(0, captures);
            clockReady = true; Tick(); Tick(); Tick(); Assert.Equal(0, captures);
            exam = false; Tick(); await Until(() => noise.Snapshot().State == "Active");
            Assert.Equal(1, captures); Assert.NotEqual(oldSession, noise.Snapshot().SessionId);
        }
        finally { Directory.Delete(directory, true); }
    }
}

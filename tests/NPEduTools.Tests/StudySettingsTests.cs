using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using NPEduTools.Contracts;
using NPEduTools.Core;
using NPEduTools.Host;

namespace NPEduTools.Tests;

public sealed class StudySettingsTests
{
    private sealed class ForbiddenReader : ILessonStatusReader
    {
        public Task<StatusResult> ReadAsync(StatusQuery query, CancellationToken cancellationToken)
            => throw new InvalidOperationException("Study status must not query a real ClassIsland or capture device.");
    }
    private static JsonObject Configuration => JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory,"Fixtures/study-device-cases.json")))![0]!["value"]!.DeepClone().AsObject();
    public static IEnumerable<object[]> CalendarCases => JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory,"Fixtures/study-calendar-cases.json")))!.AsArray()
        .Select(c => new object[] {c!.ToJsonString()});
    [Theory, MemberData(nameof(CalendarCases))]
    public void Native_calendar_matches_actual_KV_preview(string json)
    {
        var c = JsonNode.Parse(json)!.AsObject();
        var settings = c["settings"]!.Deserialize<StudySettings>(Protocol.Json)!;
        var spans = SchoolStudySchedule.ForDate(settings,DateOnly.ParseExact(c["date"]!.GetValue<string>(),"yyyy-MM-dd",CultureInfo.InvariantCulture));
        var actual = JsonSerializer.SerializeToNode(spans.Select(w => new {start=w.Start.ToString("yyyy-MM-dd'T'HH:mm:ss",CultureInfo.InvariantCulture),end=w.End.ToString("yyyy-MM-dd'T'HH:mm:ss",CultureInfo.InvariantCulture)}));
        Assert.True(JsonNode.DeepEquals(c["windows"],actual),c["name"]!.GetValue<string>());
    }
    private sealed class Clock : TimeProvider
    {
        public long Ms;
        public override long TimestampFrequency => 1000;
        public override long GetTimestamp() => Ms;
    }
    private sealed class Fixture : IAsyncDisposable
    {
        public readonly Clock Time = new();
        public readonly StudySettingsService Service;
        public readonly JsonObject Config = Configuration;
        public DateTimeOffset Base = new(2026,10,12,18,58,0,TimeSpan.Zero);
        public bool Exam, Frozen;
        private readonly Guid _connection = Guid.NewGuid(), _bridge = Guid.NewGuid();
        private long _sequence;
        public Fixture(DateTimeOffset? start = null)
        {
            if (start is { } initial) Base = initial;
            Service = new(() => new(_connection,_bridge,++_sequence,0,Base.AddMilliseconds(Frozen?0:Time.Ms),0,"Advancing","synthetic school time"),()=>Exam,Time,false);
            Service.BindStudy(Config["identity"]!.ToJsonString()); Service.ApplyStudy(Config);
        }
        public void Tick(int ms=500,bool renew=true) { Time.Ms+=ms; if(renew)Service.ApplyStudy(Config);Service.Tick(); }
        public ValueTask DisposeAsync() => Service.DisposeAsync();
    }
    [Fact]
    public async Task Preparation_start_end_and_execution_claims_are_separate()
    {
        await using var f = new Fixture(); f.Tick();f.Tick();
        Assert.Equal("Preparing",f.Service.Snapshot().Phase);
        for(int i=0;i<238;i++)f.Tick();
        var active=f.Service.Snapshot();Assert.Equal("Active",active.Phase);Assert.Equal(19,active.Window!.Start.Hour);
        Assert.Equal("NOT_INTEGRATED",active.CaptureState);Assert.Equal("NOT_INTEGRATED",active.PresentationState);
        for(int i=0;i<18000;i++)f.Tick();
        Assert.Equal("Outside",f.Service.Snapshot().Phase);Assert.Null(f.Service.Snapshot().Window);
    }
    [Fact]
    public async Task Display_eligibility_begins_five_minutes_early_with_no_capture_claim()
    {
        await using var f = new Fixture(new(2026,10,12,18,54,58,TimeSpan.Zero));
        f.Tick();f.Tick();f.Tick();Assert.Equal("Outside",f.Service.Snapshot().Phase);
        f.Tick();var state=f.Service.Snapshot();Assert.Equal("Preparing",state.Phase);
        Assert.Equal("NOT_INTEGRATED",state.CaptureState);Assert.Null(state.Execution);
    }
    [Fact]
    public async Task Clock_freeze_expiry_pause_and_scope_change_never_use_Windows_time()
    {
        await using var f = new Fixture();f.Tick();f.Tick();f.Frozen=true;
        for(int i=0;i<10;i++)f.Tick();Assert.Equal("Blocked",f.Service.Snapshot().Phase);
        f.Frozen=false;f.Tick();f.Tick();f.Tick();Assert.Equal("Preparing",f.Service.Snapshot().Phase);
        f.Exam=true;Assert.Equal("EXAM_PAUSED",f.Service.Snapshot().Reason);f.Exam=false;
        f.Tick(15000,false);Assert.Equal("UNAVAILABLE",f.Service.Snapshot().PolicyState);
        f.Service.BindStudy(null);Assert.Null(f.Service.Snapshot().Settings);
        Assert.Throws<NPEduTools.Integrations.Npep.NpepException>(()=>f.Service.ApplyStudy(f.Config));
    }
    [Fact]
    public async Task Pause_retains_only_a_fresh_next_window_and_never_enables_execution()
    {
        await using var f = new Fixture(); f.Tick(); f.Tick(); f.Exam = true;
        var paused = f.Service.Snapshot();
        Assert.Equal("Blocked", paused.Phase); Assert.Equal("EXAM_PAUSED", paused.Reason);
        Assert.Null(paused.Window); Assert.NotNull(paused.Next);
        f.Frozen = true;
        for (int i = 0; i < 10; i++) f.Tick();
        Assert.Null(f.Service.Snapshot().Next);
        f.Frozen = false; f.Tick(); f.Tick(); f.Tick(); Assert.NotNull(f.Service.Snapshot().Next);
        f.Tick(15000, false); Assert.Null(f.Service.Snapshot().Next);
    }
    [Fact]
    public async Task Closed_class_and_unconfigured_restart_do_not_activate()
    {
        await using var f = new Fixture();f.Config["effective"] = new JsonObject{["source"]="Disabled",["settings"]=null};
        f.Service.ApplyStudy(f.Config);Assert.Equal("Disabled",f.Service.Snapshot().Phase);Assert.Null(f.Service.Snapshot().Window);
        await using var restart = new StudySettingsService(()=>SchoolClockFrame.Unavailable("no synthetic clock"),()=>false,f.Time,false);
        Assert.Null(restart.Snapshot().Version);Assert.Equal("UNAVAILABLE",restart.Snapshot().PolicyState);
    }
    [Fact]
    public void Overnight_end_is_exclusive_and_effective_date_is_start_date()
    {
        var settings=Configuration["effective"]!["settings"]!.Deserialize<StudySettings>(Protocol.Json)!;
        settings=settings with {Schedule=settings.Schedule with {Rules=[new([1],"23:00","01:00")],EffectiveTo="2026-10-12"}};
        Assert.NotNull(SchoolStudySchedule.Windows(settings,new(2026,10,13,0,59,59,TimeSpan.Zero)).Current);
        Assert.Null(SchoolStudySchedule.Windows(settings,new(2026,10,13,1,0,0,TimeSpan.Zero)).Current);
        Assert.Throws<ArgumentException>(()=>SchoolStudySchedule.Windows(settings,new(2026,10,12,23,0,0,TimeSpan.FromHours(8))));
    }
    [Fact]
    public void Distant_effective_date_and_many_calendar_skips_still_find_next_window()
    {
        var settings=Configuration["effective"]!["settings"]!.Deserialize<StudySettings>(Protocol.Json)!;
        settings=settings with {Schedule=settings.Schedule with {EffectiveFrom="2030-01-01"}};
        Assert.Equal(2030,SchoolStudySchedule.Windows(settings,new(2026,10,12,19,0,0,TimeSpan.Zero)).Next!.Start.Year);
        settings=settings with {Schedule=settings.Schedule with {EffectiveFrom="2026-10-12",Exceptions=Enumerable.Range(0,366).Select(i=>new StudyCalendarException(new DateOnly(2026,10,12).AddDays(i).ToString("yyyy-MM-dd",CultureInfo.InvariantCulture),"Skip",null)).ToArray()}};
        Assert.True(SchoolStudySchedule.Windows(settings,new(2026,10,12,19,0,0,TimeSpan.Zero)).Next!.Start.Year>=2027);
    }
    [Fact, System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public async Task New_display_pipe_reads_and_hides_without_accessing_microphone_or_lesson_bridge()
    {
        await using var f = new Fixture(); f.Tick(120000); f.Tick(); f.Tick();
        var display = new StudyDisplayService(f.Service.DisplaySnapshot, f.Time, inactivity:()=>TimeSpan.FromMilliseconds(f.Time.Ms));
        string pipe = "NPEduTools.Test.Study.Display." + Guid.NewGuid().ToString("N");
        using var lifetime = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        var running = new PipeServer(pipe, new ForbiddenReader(), _ => { }, study: f.Service, studyDisplay: display).RunAsync(lifetime.Token);
        try
        {
            var status = await HostClient.RequestAsync(pipe, "study.status", lifetime.Token);
            Assert.True(status.Study!.Display!.Selected); Assert.True(status.Study.Display.Available);
            var cue = new HostRequest(1, Guid.NewGuid(), "study.reminder", StudyReminder: new(status.Study.Display.InstanceId, status.Study.Version!, status.Study.Window!, "Active"));
            Assert.Equal("Succeeded", (await HostClient.RequestAsync(pipe, cue, lifetime.Token)).Outcome);
            Assert.Equal("Suppressed", (await HostClient.RequestAsync(pipe, cue with { RequestId = Guid.NewGuid() }, lifetime.Token)).Outcome);
            var request = new HostRequest(1, Guid.NewGuid(), "study.hide", StudyHide: new(status.Study.Display.InstanceId, status.Study.Version!, status.Study.Window!));
            Assert.False(Protocol.NoiseInterruption(request));
            var hidden = await HostClient.RequestAsync(pipe, request, lifetime.Token);
            Assert.Equal("Succeeded", hidden.Outcome); Assert.Equal(600, hidden.Study!.Display!.HiddenRemainingSeconds);
            f.Tick(60000); f.Tick(); f.Tick();
            var sameDeadline = await HostClient.RequestAsync(pipe, "study.status", lifetime.Token);
            Assert.Equal(539, sameDeadline.Study!.Display!.HiddenRemainingSeconds);
            f.Service.StudyFailed("STUDY_OFFLINE");
            var offline = await HostClient.RequestAsync(pipe, "study.status", lifetime.Token);
            Assert.True(offline.Study!.Display!.Selected); Assert.False(offline.Study.Display.Available);
            var old = await HostClient.RequestAsync(pipe, "host.cached-status", lifetime.Token); Assert.Null(old.Study);
        }
        finally { await lifetime.CancelAsync(); try { await running; } catch (OperationCanceledException) { } }
    }
    [Fact, System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public async Task Dedicated_pipe_status_preserves_existing_response_shapes_and_opens_no_devices()
    {
        await using var f = new Fixture();f.Tick();f.Tick();
        string pipe="NPEduTools.Test.Study."+Guid.NewGuid().ToString("N");
        using var lifetime=new CancellationTokenSource(TimeSpan.FromSeconds(8));
        var server=new PipeServer(pipe,new ForbiddenReader(),_=>{},study:f.Service);
        var running=server.RunAsync(lifetime.Token);
        try
        {
            var status=await HostClient.RequestAsync(pipe,"study.status",lifetime.Token);
            Assert.Equal("Succeeded",status.Outcome);Assert.Equal("Preparing",status.Study!.Phase);
            Assert.Equal("NOT_INTEGRATED",status.Study.CaptureState);Assert.Null(status.Noise);
            var old=await HostClient.RequestAsync(pipe,"host.cached-status",lifetime.Token);
            Assert.Null(old.Study);
            Assert.DoesNotContain("\"study\"",JsonSerializer.Serialize(old,Protocol.Json));
            Assert.Equal("UnexpectedParameters",Protocol.Validate(new(Protocol.Version,Guid.NewGuid(),"study.status",ObserveMs:100)));
        }
        finally {await lifetime.CancelAsync();try{await running;}catch(OperationCanceledException){}}
    }
}

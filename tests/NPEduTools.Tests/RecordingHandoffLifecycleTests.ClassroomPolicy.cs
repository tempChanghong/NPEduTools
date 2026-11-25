using System.Text.Json;
using NPEduTools.Contracts;
using NPEduTools.Core;
using NPEduTools.Host;

namespace NPEduTools.Tests;

public sealed partial class RecordingHandoffLifecycleTests
{
    private static readonly string PolicyVersion = new('b',64);
    private const string Scope = "synthetic-original-screen";
    private const string Password = "synthetic-class-password";
    private static void SetRule(Fixture f, string rule, Func<bool>? usb = null) =>
        f.Actions.SetClassroomActionPolicy(PolicyVersion,"default","default",usb,Scope,startManualRecording:rule);
    private static HostRequest ManualStart(Fixture f) => new(1,Guid.NewGuid(),"recording.command",
        Recording:new("start",new("display-one",f.DirectoryPath,SpeakerId:"speaker-one",MicrophoneId:"microphone-one"),ClientId:Guid.NewGuid()));
    private static HostRequest Grant(Fixture f, HostRequest target, string? secret = null)
    {
        var state = f.Actions.Protection();
        return new(1,Guid.NewGuid(),"noise.management.command", NoiseManagement:new("authorize",secret,
            Purpose:Protocol.NoisePurpose(target),TargetRequestId:target.RequestId,InstanceId:state.InstanceId,SessionId:state.SessionId,
            ClassroomPolicyVersion:PolicyVersion,RecordingTarget:target.Recording));
    }
    private sealed class NoLessonRead : ILessonStatusReader
    {
        public Task<StatusResult> ReadAsync(StatusQuery query, CancellationToken ct) => throw new InvalidOperationException("No bridge");
    }

    [Fact, System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public async Task Strict_pipe_ticket_binds_full_recording_target_and_later_deny_allows_ongoing_session_to_finish()
    {
        await using var f = await Fixture.Open();
        SetRule(f,"allow");
        string pipe = "NPEduTools.Test.recording-policy." + Guid.NewGuid().ToString("N");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var serving = new PipeServer(pipe,new NoLessonRead(),_ => {},recording:f.Service,noise:f.Actions).RunAsync(timeout.Token);
        Task<HostResponse> Send(HostRequest request) => HostClient.RequestAsync(pipe,request,timeout.Token);
        try
        {
            foreach (string suffix in new[]{"",".v2",".v4",".v5"})
            {
                var observed = await Send(new(1,Guid.NewGuid(),"noise.management.status.classroom-policy"+suffix));
                Assert.Equal(suffix == ".v5" ? "allow" : null,observed.NoiseProtection!.ClassroomPolicy!.StartManualRecording);
                if (suffix != ".v5") Assert.DoesNotContain("startManualRecording",JsonSerializer.Serialize(observed,Protocol.Json));
            }
            var target = ManualStart(f);
            var command = target.Recording!;
            var options = command.Options!;
            Assert.False(Protocol.NoiseInterruption(target));
            Assert.Equal("MANAGEMENT_REQUIRED",(await Send(target)).ErrorCode);
            target = target with {NoiseAuthorization = Assert.IsType<Guid>((await Send(Grant(f,target))).NoiseProtection!.Ticket)};
            var alternatives = new[]{options with {Display="other-display"},options with {OutputDirectory=Path.Combine(f.DirectoryPath,"other")},
                options with {MicrophoneId="other-mic"},options with {SpeakerId="other-speaker"},options with {SystemAudio=false},
                options with {Microphone=false},options with {MaximumHeight=720},options with {FramesPerSecond=15}};
            foreach (var different in alternatives)
                Assert.Equal("MANAGEMENT_REQUIRED",(await Send(target with {Recording=command with {Options=different}})).ErrorCode);
            Assert.Equal("MANAGEMENT_REQUIRED",(await Send(target with {Recording=command with {ClientId=Guid.NewGuid()}})).ErrorCode);
            Assert.Equal("MANAGEMENT_REQUIRED",(await Send(target with {RequestId=Guid.NewGuid()})).ErrorCode);
            Assert.Null(f.Worker);
            Assert.Equal("Succeeded",(await Send(target)).Outcome);
            await WaitForPhase(f,"Recording");
            var control = f.Service.State.Control!;
            Assert.Equal(target.RequestId,control.SessionId);
            SetRule(f,"deny");
            Assert.Equal("Succeeded",(await Send(target with {NoiseAuthorization=null})).Outcome);
            Assert.Equal("RECORDING_REQUEST_CONFLICT",(await Send(target with {Recording=command with {ClientId=Guid.NewGuid()}})).ErrorCode);
            foreach (string action in new[]{"pause","resume","stop"})
            {
                var next = new HostRequest(1,Guid.NewGuid(),"recording.command",Recording:new(action,Control:control,ClientId:command.ClientId));
                Assert.Equal("UnexpectedParameters",Protocol.Validate(next with {NoiseAuthorization=Guid.NewGuid()}));
                Assert.Equal("Succeeded",(await Send(next)).Outcome);
            }
            await WaitForPhase(f,"Saved");
            Assert.Equal("CLASSROOM_ACTION_DENIED",(await Send(ManualStart(f))).ErrorCode);
        }
        finally { await timeout.CancelAsync(); await serving; }
    }

    [Theory]
    [InlineData("both",true,false,"CLASSROOM_USB_REQUIRED")]
    [InlineData("both",false,true,"MANAGEMENT_REQUIRED")]
    [InlineData("both",true,true,null)]
    [InlineData("either",false,true,null)]
    [InlineData("either",true,false,null)]
    [InlineData("password",false,true,"MANAGEMENT_REQUIRED")]
    [InlineData("deny",true,true,"CLASSROOM_ACTION_DENIED")]
    public async Task Manual_recording_factors_use_the_current_class_and_reject_unbound_audio_endpoints(
        string rule, bool usePassword, bool usb, string? error)
    {
        await using var f = await Fixture.Open();
        Assert.Null(f.Actions.BindSchoolManagement(Scope));
        Assert.Null(f.Actions.ApplySchoolManagement(Scope,SchoolManagementTests.Credential(1,Password)));
        SetRule(f,rule,()=>usb);
        var target = ManualStart(f);
        var cmd = target.Recording!;
        var opts = cmd.Options!;
        var secret = usePassword ? Password : null;
        foreach(var aliases in new[] {opts with {SpeakerId="default"},opts with {MicrophoneId="default"}})
        {
            var loose = target with {Recording=cmd with {Options=aliases}};
            string expected = rule == "deny" ? "CLASSROOM_ACTION_DENIED" : "CLASSROOM_RECORDING_SOURCE_REQUIRED";
            Assert.Equal(expected,f.Actions.ManagementHandle(Grant(f,loose,secret)).ErrorCode);
            Assert.Equal(expected,(await f.Service.HandleAsync(loose)).ErrorCode);
        }
        target = target with {Recording=cmd with {Options=opts with {SystemAudio=false,SpeakerId="default"}}};
        var response = f.Actions.ManagementHandle(Grant(f,target,secret));
        Assert.Equal(error,response.ErrorCode);
        if (error is not null) { Assert.Null(response.NoiseProtection?.Ticket); Assert.Null(f.Worker); return; }
        var ticket = Assert.IsType<Guid>(response.NoiseProtection?.Ticket);
        Assert.Equal("Succeeded",(await f.Service.HandleAsync(target with { NoiseAuthorization=ticket })).Outcome);
        await WaitForPhase(f,"Recording");
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task Admission_rechecks_after_previous_worker_cleanup_and_again_before_record_command(bool afterHello)
    {
        await using var f = await Fixture.Open();
        int probes = 0;
        SetRule(f,"usb",() => ++probes < 3);
        var target = ManualStart(f);
        target = target with {NoiseAuthorization=Assert.IsType<Guid>(f.Actions.ManagementHandle(Grant(f,target)).NoiseProtection?.Ticket)};
        if (afterHello)
        {
            Assert.Equal("CLASSROOM_USB_REQUIRED",(await f.Service.HandleAsync(target)).ErrorCode);
            Assert.Equal(3,probes); // Ticket issue, worker creation, final authorization before sending start.
            Assert.Equal("Idle",f.Service.State.Phase);
        }
        else
        {
            var plans = f.Plans(new(12,45));
            await f.FinishedWorker(plans[0]);
            var unsettled = f.Service.HandleAsync(target);
            Assert.False(unsettled.IsCompleted);
            SetRule(f,"deny");
            await f.ReleaseWorker();
            Assert.Equal("CLASSROOM_ACTION_DENIED",(await unsettled).ErrorCode);
            Assert.Equal(1,probes);
            Assert.Equal("Recorded",Assert.Single(f.Ledger.Document.Entries).Phase);
            f.Enable(); f.Fresh(Now.AddSeconds(2)); await f.Tick();
            Assert.Equal(plans[1].Key,f.Worker!.State.Control!.OccurrenceId);
        }
    }

    private static async Task WaitForPhase(Fixture f, string phase)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (f.Service.State.Phase != phase) await Task.Delay(10,timeout.Token);
    }
}

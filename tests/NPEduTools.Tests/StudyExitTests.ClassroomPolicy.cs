using NPEduTools.Contracts;
using NPEduTools.Core;
using NPEduTools.Host;

namespace NPEduTools.Tests;

public sealed partial class StudyExitTests
{
    [Fact]
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public async Task Opted_in_pipe_uses_original_policy_at_intent_and_shutdown_without_false_timeout_audit()
    {
        await using var f = new Fixture(true);
        string blocked = new('9', 64), allowed = new('8', 64), usb = new('7', 64);
        string pipe = "NPEduTools.Test.classroom-actions." + Guid.NewGuid().ToString("N");
        using var lifetime = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        int stops = 0;
        var running = new PipeServer(pipe, new NeverRead(), _ => {}, noise: f.Noise,
            studyPhotos: f.Photos, studyExit: f.Guard, stop: () => Interlocked.Increment(ref stops), prepareStop: () => true)
            .RunAsync(lifetime.Token);
        Task<HostResponse> Send(HostRequest target) => HostClient.RequestAsync(pipe, target, lifetime.Token);
        try
        {
            var target = new HostRequest(1, Guid.NewGuid(), "host.stop");
            f.Noise.SetClassroomActionPolicy(blocked, "allow", "deny", null);
            Assert.Equal("CLASSROOM_ACTION_DENIED", (await Send(f.Begin(target.RequestId))).ErrorCode);
            Assert.Equal(Guid.Empty, f.Guard.Snapshot().AttemptId);
            Assert.Equal("CLASSROOM_ACTION_DENIED", (await Send(target)).ErrorCode);

            f.Noise.SetClassroomActionPolicy(usb, "allow", "usb", () => true);
            var optedIn = await HostClient.RequestAsync(pipe, "noise.management.status.classroom-policy", lifetime.Token);
            Assert.Equal(usb, optedIn.NoiseProtection!.ClassroomPolicy!.Version);
            Assert.Null((await HostClient.RequestAsync(pipe, "noise.management.status", lifetime.Token)).NoiseProtection!.ClassroomPolicy);
            Assert.Equal("Waiting", (await Send(f.Begin(target.RequestId))).StudyExit!.State);
            f.Noise.SetClassroomActionPolicy(allowed, "allow", "allow", null);
            f.Time.Ms += 31000;
            Assert.Equal("Invalidated", (await HostClient.RequestAsync(pipe, "study.exit.status", lifetime.Token)).StudyExit!.State);
            Assert.Null(f.Guard.PendingExit());

            target = target with { RequestId = Guid.NewGuid() };
            Assert.Equal("NotRequired", (await Send(f.Begin(target.RequestId))).StudyExit!.State);
            f.Time.Ms += 31000;
            Assert.Equal("NotRequired", f.Guard.Snapshot().State);
            Assert.Null(f.Guard.PendingExit());
            var grant = await Send(new(1, Guid.NewGuid(), "noise.management.command", NoiseManagement:
                new("authorize", Purpose: "maintenance", TargetRequestId: target.RequestId, InstanceId: f.Host, SessionId: f.Session,
                    ClassroomPolicyVersion: allowed)));
            Assert.Equal("Succeeded", grant.Outcome);
            Assert.NotNull(grant.NoiseProtection!.Ticket);
            target = target with { NoiseAuthorization = grant.NoiseProtection.Ticket };
            Assert.Equal("Succeeded", (await Send(target with { Capability = "noise.management.prepare-exit" })).Outcome);
            f.Noise.SetClassroomActionPolicy(blocked, "allow", "deny", null);
            Assert.Equal("CLASSROOM_ACTION_DENIED", (await Send(target)).ErrorCode);
            Assert.Equal(0, Volatile.Read(ref stops));
            Assert.True(f.Noise.Protection().Protected);
            Assert.Null(f.Guard.PendingExit());
        }
        finally
        {
            await lifetime.CancelAsync();
            try { await running; } catch (OperationCanceledException) { }
        }
    }
}

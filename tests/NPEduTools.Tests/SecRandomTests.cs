using System.IO.Pipes;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;
using NPEduTools.Contracts;
using NPEduTools.Core;
using NPEduTools.Host;
using NPEduTools.Integrations.SecRandom;

namespace NPEduTools.Tests;

public sealed class SecRandomTests
{
    private static byte[] Bytes(string value) => Encoding.UTF8.GetBytes(value);
    [Fact] public void BusinessFailureIsNotSuccess()
    {
        var reply = SecRandomIpcClient.Decode(Bytes("""{"success":true,"type":"url","result":{"status":"error","code":"authorization_denied"}}"""), SecRandomAction.QuickDraw);
        Assert.False(reply.Succeeded); Assert.False(reply.Uncertain); Assert.Equal("authorization_denied", reply.Code);
    }
    [Fact] public void InternalErrorAfterDrawIsUncertain()
    {
        var reply = SecRandomIpcClient.Decode(Bytes("""{"success":false,"type":"url","error":{"code":"internal_error"}}"""), SecRandomAction.QuickDraw);
        Assert.True(reply.Uncertain);
    }
    [Theory]
    [InlineData("{}")]
    [InlineData("null")]
    [InlineData("{\"success\":true,\"type\":\"url\",\"result\":{\"status\":\"success\"}}")]
    [InlineData("{\"success\":true,\"type\":\"url\",\"result\":{\"status\":\"pending\"}}")]
    [InlineData("{\"success\":false,\"type\":\"url\",\"error\":{\"code\":\"bad\\ncode\"}}")]
    public void InvalidDrawReplyIsRejected(string json) => Assert.Throws<InvalidDataException>(() => SecRandomIpcClient.Decode(Bytes(json), SecRandomAction.QuickDraw));
    [Fact] public void DrawParsesBoundedResult()
    {
        var reply = SecRandomIpcClient.Decode(Bytes("""{"success":true,"type":"url","result":{"status":"success","data":{"id":"001","name":"张同学","gender":""}}}"""), SecRandomAction.QuickDraw);
        Assert.True(reply.Succeeded); Assert.Equal("张同学", reply.Winner!.Name); Assert.Equal("001", reply.Winner.Id);
    }
    [Fact] public void ProbeNeedsExpectedNonMutatingResponse()
    {
        var reply = SecRandomIpcClient.Decode(Bytes("""{"success":true,"type":"url","result":{"status":"error","code":"invalid_command"}}"""), SecRandomAction.Probe);
        Assert.True(reply.Succeeded);
        Assert.Throws<InvalidDataException>(() => SecRandomIpcClient.Decode(Bytes("""{"success":true,"type":"url","result":{"status":"success"}}"""), SecRandomAction.Probe));
        Assert.StartsWith("data/", SecRandomIpcClient.Route(SecRandomAction.Probe));
    }
    [Theory] [InlineData("oobe_required")] [InlineData("integrity_confirmation_required")]
    public void SetupGateDoesNotBecomeReady(string code)
    {
        var json = JsonSerializer.Serialize(new { success = true, type = "url", result = new { status = "error", code } });
        Assert.False(SecRandomIpcClient.Decode(Bytes(json), SecRandomAction.Probe).Succeeded);
    }
    [Fact] public async Task ReadRejectsIncompleteAndOversizedFrames()
    {
        await Assert.ThrowsAsync<InvalidDataException>(() => SecRandomIpcClient.ReadLineAsync(new MemoryStream(Bytes("{}")), default));
        await Assert.ThrowsAsync<InvalidDataException>(() => SecRandomIpcClient.ReadLineAsync(new MemoryStream(Bytes(new string('x', SecRandomIpcClient.MaxResponseBytes + 1) + "\n")), default));
        var exactly = await SecRandomIpcClient.ReadLineAsync(new MemoryStream(Bytes(new string('x', SecRandomIpcClient.MaxResponseBytes) + "\n")), default);
        Assert.Equal(SecRandomIpcClient.MaxResponseBytes, exactly.Length);
    }
    [Fact] public async Task WireUsesOneLineAndReturnsBusinessFailure()
    {
        string name = "NPEduTools.SecRandom.test." + Guid.NewGuid().ToString("N");
        using var server = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        string? route = null;
        var serve = Task.Run(async () =>
        {
            await server.WaitForConnectionAsync();
            var line = await SecRandomIpcClient.ReadLineAsync(server, default);
            using var json = JsonDocument.Parse(line);
            Assert.Equal(1, json.RootElement.GetProperty("version").GetInt32());
            route = json.RootElement.GetProperty("payload").GetProperty("url").GetString();
            await server.WriteAsync(Bytes("""{"success":true,"type":"url","result":{"status":"error","code":"authorization_denied"}}""" + "\n"));
        });
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        bool verified = false;
        var client = new SecRandomIpcClient(name, (_, _) => verified = true);
        var result = await client.SendAsync("fixture", SecRandomAction.QuickDraw, timeout.Token);
        await serve.WaitAsync(timeout.Token);
        Assert.True(verified); Assert.Equal("roll_call/quick_draw", route); Assert.False(result.Succeeded);
    }
    [Fact] public async Task IdentityFailureSendsNoCommand()
    {
        string name = "NPEduTools.SecRandom.test." + Guid.NewGuid().ToString("N");
        using var server = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        var accepted = server.WaitForConnectionAsync();
        var serve = Task.Run(async () =>
        {
            try { await accepted; return await server.ReadAsync(new byte[1]); }
            catch (IOException) { return 0; } // Windows can report PIPE_CLOSING when the verified client sends nothing.
        });
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var reply = await new SecRandomIpcClient(name, (_, _) => throw new InvalidOperationException())
            .SendAsync("fixture", SecRandomAction.QuickDraw, timeout.Token);
        Assert.Equal("peer_mismatch", reply.Code); Assert.False(reply.Uncertain); Assert.Equal(0, await serve.WaitAsync(timeout.Token));
    }
    [Fact] public async Task MalformedReplyAfterSendIsUncertain()
    {
        string name = "NPEduTools.SecRandom.test." + Guid.NewGuid().ToString("N");
        using var server = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        var serve = Task.Run(async () => { await server.WaitForConnectionAsync(); await SecRandomIpcClient.ReadLineAsync(server, default); await server.WriteAsync(Bytes("{}\n")); });
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var reply = await new SecRandomIpcClient(name, (_, _) => { }).SendAsync("fixture", SecRandomAction.QuickDraw, timeout.Token);
        await serve.WaitAsync(timeout.Token); Assert.True(reply.Uncertain); Assert.Equal("invalid_response", reply.Code);
    }
    [Fact] public async Task ServerExitingDuringVerificationDoesNotSendCommand()
    {
        string name = "NPEduTools.SecRandom.test." + Guid.NewGuid().ToString("N");
        using var server = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        var accepted = server.WaitForConnectionAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var reply = await new SecRandomIpcClient(name, (_, _) => throw new ArgumentException("PID gone"))
            .SendAsync("fixture", SecRandomAction.QuickDraw, timeout.Token);
        await accepted.WaitAsync(timeout.Token);
        Assert.Equal("peer_unverifiable", reply.Code); Assert.False(reply.Uncertain);
    }
    [Theory] [InlineData(SecRandomAction.Probe, false)] [InlineData(SecRandomAction.QuickDraw, true)]
    public async Task ResponseTimeoutOnlyMakesSentMutationUncertain(SecRandomAction action, bool uncertain)
    {
        string name = "NPEduTools.SecRandom.test." + Guid.NewGuid().ToString("N");
        using var server = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var accepted = server.WaitForConnectionAsync(timeout.Token);
        var client = new SecRandomIpcClient(name, (_, _) => { }).SendAsync("fixture", action, timeout.Token);
        await accepted;
        await SecRandomIpcClient.ReadLineAsync(server, timeout.Token); // Cancel only after the request has been sent.
        await timeout.CancelAsync();
        var reply = await client;
        Assert.Equal("timeout", reply.Code); Assert.False(reply.Succeeded); Assert.Equal(uncertain, reply.Uncertain);
    }
    [Fact] public void LauncherPeerMustBeDirectV3Payload()
    {
        Assert.True(WindowsSecRandomTarget.Matches(@"C:\SecRandom\SecRandomLauncher.exe", @"C:\SecRandom\app-v3.0.0-0\SecRandom.Desktop.exe"));
        Assert.False(WindowsSecRandomTarget.Matches(@"C:\SecRandom\SecRandomLauncher.exe", @"C:\Other\app-v3.0.0-0\SecRandom.Desktop.exe"));
        Assert.False(WindowsSecRandomTarget.Matches(@"C:\SecRandom\SecRandomLauncher.exe", @"C:\SecRandom\app-v3.0.0-0\nested\SecRandom.Desktop.exe"));
        Assert.False(WindowsSecRandomTarget.Matches(@"C:\SecRandom\SecRandomLauncher.exe", @"C:\SecRandom\app-v4.0\SecRandom.Desktop.exe"));
    }
    [Theory] [InlineData("check")] [InlineData("open")] [InlineData("show-float")] [InlineData("hide-float")] [InlineData("quick-draw")]
    public void HostContractAllowsOnlyTypedOperations(string action)
    {
        var request = new HostRequest(Protocol.Version, Guid.NewGuid(), "secrandom.command", ExpectedRevision: 1, SecRandom: new(action));
        Assert.Null(Protocol.Validate(request));
        Assert.NotNull(Protocol.Validate(request with { ExpectedRevision = null }));
        Assert.NotNull(Protocol.Validate(request with { ObserveMs = 1 }));
        Assert.NotNull(Protocol.Validate(request with { SecRandom = new("secrandom://tray/exit") }));
        Assert.NotNull(Protocol.Validate(request with { Capability = "host.ping" }));
    }
    [Fact] public async Task ReceiptsDeduplicateDrawsAndRejectIdReuse()
    {
        await using var f = new Fixture(); f.Configure();
        var request = f.Command("quick-draw");
        Assert.Equal("Accepted", f.Service.Handle(request).Outcome); await f.Wait();
        Assert.Equal("Succeeded", f.Service.Handle(request).Outcome); Assert.Equal(1, f.Draws);
        Assert.Equal("RequestIdReuse", f.Service.Handle(request with { SecRandom = new("show-float") }).ErrorCode);
    }
    [Fact] public async Task ConcurrentCommandsAreRejectedAndGateHeld()
    {
        var pending = new TaskCompletionSource<SecRandomReply>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var f = new Fixture((_, _, token) => pending.Task.WaitAsync(token)); f.Configure();
        Assert.Equal("Accepted", f.Service.Handle(f.Command("quick-draw")).Outcome);
        Assert.Equal("OperationBusy", f.Service.Handle(f.Command("quick-draw")).ErrorCode);
        Assert.Null(f.Gate.TryReserveSwitch());
        pending.SetResult(new(true, "success", "done", Winner: new("1", "同学", ""))); await f.Wait();
        using var lease = f.Gate.TryReserveSwitch(); Assert.NotNull(lease);
    }
    [Fact] public async Task UnknownDrawSurvivesChecksAndRestartUntilExplicitAcknowledgment()
    {
        await using var f = new Fixture(); f.Configure(); f.Uncertain = true;
        var draw = f.Command("quick-draw"); f.Service.Handle(draw); await f.Wait();
        Assert.Equal("Unknown", f.Service.Snapshot().Operation!.State);
        for (int i = 0; i < 36; i++) { f.Service.Handle(f.Command("check")); await f.Wait(); }
        Assert.Equal(draw.RequestId, f.Service.Snapshot().Operation!.RequestId);
        await f.Restart();
        Assert.Equal("ResultUncertain", f.Service.Handle(f.Command("quick-draw")).ErrorCode);
        Assert.Equal("OperationChanged", f.Service.Handle(f.Command("acknowledge", Guid.NewGuid())).ErrorCode);
        Assert.Equal("Succeeded", f.Service.Handle(f.Command("acknowledge", draw.RequestId)).Outcome);
        f.Uncertain = false; f.Service.Handle(f.Command("quick-draw")); await f.Wait(); Assert.Equal(2, f.Draws);
    }
    [Fact] public async Task RunningIntentBecomesUnknownAfterRestart()
    {
        var pending = new TaskCompletionSource<SecRandomReply>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var f = new Fixture((_, _, token) => pending.Task.WaitAsync(token)); f.Configure();
        f.Service.Handle(f.Command("quick-draw"));
        await using var recovered = f.CreateService();
        Assert.Equal("Unknown", recovered.Snapshot().Operation!.State);
        pending.SetResult(new(true, "success", "done")); await f.Wait();
    }
    [Fact] public async Task CorruptConfigIsPreservedAndStatusIsReadable()
    {
        await using var f = new Fixture(); await f.Service.DisposeAsync();
        var path = Path.Combine(f.Directory, "secrandom.json"); File.WriteAllText(path, "{\"version\":1,\"path\":null,\"revision\":0,\"receipts\":null}");
        f.Service = f.CreateService(); Assert.NotNull(f.Service.Snapshot().Error);
        Assert.Equal("Succeeded", f.Service.Handle(new(1, Guid.NewGuid(), "secrandom.status")).Outcome);
        Assert.Equal("SecRandomUnavailable", f.Service.Handle(f.Command("open")).ErrorCode);
        Assert.Contains("\"receipts\":null", File.ReadAllText(path));
    }
    [Fact] public async Task ColdOpenRetriesOnlyProbeAndSendsShowOnce()
    {
        int probes = 0, shows = 0;
        await using var f = new Fixture((_, action, _) =>
        {
            if (action == SecRandomAction.Probe && ++probes == 1) return Task.FromResult(new SecRandomReply(false, "internal_error", "starting"));
            if (action == SecRandomAction.ShowRollCall) shows++;
            return Task.FromResult(new SecRandomReply(true, "success", "done"));
        });
        f.Configure(); f.Service.Handle(f.Command("open")); await f.Wait(); Assert.Equal(2, probes); Assert.Equal(1, shows);
    }
    [Fact] public async Task UnconfiguredQuickDrawDoesNotSendAnything()
    {
        int sends = 0;
        await using var f = new Fixture((_, _, _) => { sends++; return Task.FromResult(new SecRandomReply(true, "success", "done")); });
        Assert.Equal("PathMissing", f.Service.Handle(f.Command("quick-draw")).ErrorCode);
        Assert.Equal(0, sends); Assert.Null(f.Service.Snapshot().Operation);
    }
    [Fact] public async Task ColdQuickDrawRetriesOnlyReadOnlyProbeThenDrawsOnce()
    {
        int probes = 0, draws = 0;
        await using var f = new Fixture((_, action, _) =>
        {
            if (action == SecRandomAction.Probe && ++probes == 1) return Task.FromResult(new SecRandomReply(false, "internal_error", "starting"));
            if (action == SecRandomAction.QuickDraw) draws++;
            return Task.FromResult(new SecRandomReply(true, "success", "done", Winner: new("1", "同学", "")));
        });
        f.Configure(); var draw = f.Command("quick-draw"); f.Service.Handle(draw); await f.Wait();
        Assert.Equal(2, probes); Assert.Equal(1, draws); Assert.Equal("Succeeded", f.Service.Snapshot().Operation!.State);
        f.Service.Handle(draw); Assert.Equal(1, draws);
    }
    [Fact] public async Task StartupGatePreventsQuickDrawWithoutMakingResultUncertain()
    {
        int draws = 0;
        await using var f = new Fixture((_, action, _) =>
        {
            if (action == SecRandomAction.QuickDraw) draws++;
            return Task.FromResult(new SecRandomReply(false, "oobe_required", "先完成首次设置"));
        });
        f.Configure(); f.Service.Handle(f.Command("quick-draw")); await f.Wait();
        Assert.Equal(0, draws); Assert.Equal("Failed", f.Service.Snapshot().Operation!.State);
        Assert.Equal("oobe_required", f.Service.Snapshot().Operation!.ErrorCode);
    }
    [Fact] public async Task ClassSwitchBlocksMutationsButAllowsReadOnlyCheck()
    {
        await using var f = new Fixture(); f.Configure(); using var lease = f.Gate.TryReserveSwitch(); Assert.NotNull(lease);
        Assert.Equal("OperationBusy", f.Service.Handle(f.Command("quick-draw")).ErrorCode);
        Assert.Equal("Accepted", f.Service.Handle(f.Command("check")).Outcome); await f.Wait(); Assert.Equal(0, f.Draws);
    }

    [Fact] [SupportedOSPlatform("windows")]
    public async Task HostPipeRoutesTypedRequestsAndDoesNotQueryClassIsland()
    {
        await using var f = new Fixture();
        string name = "NPEduTools.Test.SecRandomHost." + Guid.NewGuid().ToString("N");
        using var lifetime = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var server = new PipeServer(name, new ForbiddenReader(), _ => { }, secRandom: f.Service);
        var running = server.RunAsync(lifetime.Token);
        try
        {
            var initial = await HostClient.RequestAsync(name, "secrandom.status", lifetime.Token);
            Assert.Equal(0, initial.SecRandom!.Revision); Assert.Equal(0, f.Draws);
            var configure = new HostRequest(1, Guid.NewGuid(), "secrandom.config.set", ExecutablePath: "fixture", ExpectedRevision: 0);
            Assert.Equal("Succeeded", (await HostClient.RequestAsync(name, configure, lifetime.Token)).Outcome);
            var draw = f.Command("quick-draw");
            Assert.Equal("Accepted", (await HostClient.RequestAsync(name, draw, lifetime.Token)).Outcome);
            await f.Wait();
            var status = await HostClient.RequestAsync(name, "secrandom.status", lifetime.Token);
            Assert.Equal("Succeeded", status.SecRandom!.Operation!.State);
            Assert.Equal(draw.RequestId, status.SecRandom.Operation.RequestId);
            Assert.Equal("Succeeded", (await HostClient.RequestAsync(name, draw, lifetime.Token)).Outcome);
            Assert.Equal(1, f.Draws);
            Assert.Equal("InvalidSecRandomCommand", (await HostClient.RequestAsync(name, draw with { SecRandom = new("tray/exit") }, lifetime.Token)).ErrorCode);
        }
        finally
        {
            await lifetime.CancelAsync();
            try { await running; } catch (OperationCanceledException) { }
        }
    }

    private sealed class ForbiddenReader : ILessonStatusReader
    {
        public Task<StatusResult> ReadAsync(StatusQuery query, CancellationToken cancellationToken)
            => throw new InvalidOperationException("SecRandom must not query ClassIsland.");
    }

    private sealed class Fixture : IAsyncDisposable
    {
        public readonly string Directory = Path.Combine(Path.GetTempPath(), "NPEduTools.SecRandomTests", Guid.NewGuid().ToString("N"));
        public readonly RuntimeOperationGate Gate = new();
        private readonly Func<string, SecRandomAction, CancellationToken, Task<SecRandomReply>> _send;
        public SecRandomService Service;
        public int Draws; public bool Uncertain;
        public Fixture(Func<string, SecRandomAction, CancellationToken, Task<SecRandomReply>>? send = null)
        {
            _send = send ?? ((_, action, _) =>
            {
                if (action == SecRandomAction.QuickDraw) Draws++;
                return Task.FromResult(action == SecRandomAction.QuickDraw && Uncertain ? new SecRandomReply(false, "timeout", "unknown", true) : new SecRandomReply(true, "success", "done"));
            });
            Service = CreateService();
        }
        public SecRandomService CreateService() => new(Directory, Gate, _send, x => x, _ => { });
        public void Configure() => Assert.Equal("Succeeded", Service.Handle(new(1, Guid.NewGuid(), "secrandom.config.set", ExecutablePath: "fixture", ExpectedRevision: 0)).Outcome);
        public HostRequest Command(string action, Guid? acknowledge = null) => new(1, Guid.NewGuid(), "secrandom.command", ExpectedRevision: Service.Snapshot().Revision, SecRandom: new(action, acknowledge));
        public async Task Wait()
        {
            await Service.WhenIdle.WaitAsync(TimeSpan.FromSeconds(5));
        }
        public async Task Restart() { await Service.DisposeAsync(); Service = CreateService(); }
        public async ValueTask DisposeAsync() { await Service.DisposeAsync(); System.IO.Directory.Delete(Directory, true); }
    }
}

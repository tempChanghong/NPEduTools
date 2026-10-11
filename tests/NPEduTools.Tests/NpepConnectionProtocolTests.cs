using System.IO.Pipes;
using System.Text.Json.Nodes;
using NPEduTools.App;
using NPEduTools.Contracts;

namespace NPEduTools.Tests;

public sealed class NpepConnectionProtocolTests
{
    [Theory]
    [InlineData("status", "request-id", 0)]
    [InlineData("status", "version", 0)]
    [InlineData("status", "zero-length", 0)]
    [InlineData("status", "empty-message", 0)]
    [InlineData("pair", "request-id", 0)]
    [InlineData("pair", "version", 0)]
    [InlineData("pair", "zero-length", 0)]
    [InlineData("pair", "empty-message", 0)]
    [InlineData("pause", "request-id", 0)]
    [InlineData("pause", "version", 0)]
    [InlineData("pause", "zero-length", 0)]
    [InlineData("pause", "empty-message", 0)]
    [InlineData("status", "empty-message", 100)]
    public async Task InvalidReceiptClearsFactsAndRecoveryNeverReplaysMutation(string operation, string invalid, int disconnectDelayMs)
    {
        string pipe = "NPEduTools.Test.school-protocol." + Guid.NewGuid().ToString("N");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var server = new NamedPipeServerStream(pipe, PipeDirection.InOut, 1,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        const string origin = "https://school.example";
        var initial = new NpepState(1, operation == "pause" ? "ACTIVE" : "UNPAIRED", "ONLINE", "隔离状态",
            Origin: origin, Server: new JsonObject { ["serverInstanceId"] = Guid.NewGuid().ToString(),
                ["deploymentEpoch"] = Guid.NewGuid().ToString() });
        var recovered = initial with { Revision = 2, State = operation == "pair" ? "PENDING" : initial.State,
            ReportingPaused = operation == "pause", Pairing = operation == "pair" ? new JsonObject { ["userCode"] = "TEST-2345" } : null };
        var requests = new List<HostRequest>();
        var consumed = Enumerable.Range(0, 3).Select(_ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)).ToArray();
        var disconnected = Enumerable.Range(0, 3).Select(_ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)).ToArray();
        var responder = Task.Run(async () =>
        {
            for (int step = 0; step < 3; step++)
            {
                await server.WaitForConnectionAsync(timeout.Token);
                var request = await Protocol.ReadAsync<HostRequest>(server, timeout.Token);
                requests.Add(request);
                if (step == 1)
                {
                    if (invalid == "zero-length") await server.WriteAsync(new byte[4], timeout.Token);
                    else if (invalid == "empty-message") await Protocol.WriteAsync<HostResponse?>(server, null, timeout.Token);
                    else await Protocol.WriteAsync(server, new HostResponse(invalid == "version" ? Protocol.Version + 1 : Protocol.Version,
                        invalid == "request-id" ? Guid.NewGuid() : request.RequestId, "Succeeded", null, "不可采信的状态", Npep: recovered), timeout.Token);
                }
                else await Protocol.WriteAsync(server, new HostResponse(Protocol.Version, request.RequestId, "Succeeded", null,
                    "隔离有效回执", Npep: step == 0 ? initial : recovered), timeout.Token);
                await consumed[step].Task.WaitAsync(timeout.Token);
                if (disconnectDelayMs > 0) await Task.Delay(disconnectDelayMs, timeout.Token);
                server.Disconnect();
                disconnected[step].TrySetResult();
            }
        }, timeout.Token);
        try
        {
            int exchange = 0;
            var session = new NpepConnectionSession(async (request, token) =>
            {
                int step = exchange++;
                try { return await HostClient.RequestAsync(pipe, request, token); }
                finally
                {
                    // Complete this exchange before reusing the fixture pipe for the next refresh.
                    consumed[step].TrySetResult();
                    await disconnected[step].Task.WaitAsync(token);
                }
            });
            Assert.True(await session.RefreshAsync());
            session.Origin = origin; session.DeviceName = "隔离测试设备"; session.UserCode = "ABCD-2345";
            session.ServerConfirmed = session.BindingConfirmed = true;
            Assert.False(await Invoke(session, operation));
            Assert.Null(session.Revision); Assert.False(session.HasPairing); Assert.False(session.CanFinish);
            Assert.False(session.CanPair); Assert.False(session.CanPause); Assert.False(session.CanUnpair);
            Assert.False(session.ServerConfirmed); Assert.False(session.BindingConfirmed);
            Assert.False(session.IsWorking); Assert.True(session.CanRefresh);
            Assert.Contains("未确认", session.Title);
            Assert.Contains(operation == "status" ? "后台暂不可用" : "可能已受理", session.Message);
            Assert.Equal(origin, session.Origin); Assert.Equal("隔离测试设备", session.DeviceName);
            Assert.Equal("ABCD-2345", session.UserCode);
            if (operation != "status") Assert.False(await Invoke(session, operation));
            Assert.True(await session.RefreshAsync());
            await responder;
            Assert.Equal(2, session.Revision); Assert.False(session.IsWorking); Assert.True(session.CanRefresh);
            Assert.DoesNotContain("HOST_UNAVAILABLE", session.TechnicalDetails);
            if (operation == "pair")
            { Assert.True(session.ShowCode); Assert.Equal("TEST-2345", session.PairingCode); Assert.False(session.CanPair); }
            if (operation == "pause")
            { Assert.Equal("恢复互联", session.PauseLabel); Assert.True(session.CanPause); }
            Assert.Equal(3, requests.Count);
            Assert.All(requests, request => Assert.Null(Protocol.Validate(request)));
            Assert.All(new[] { requests[0], requests[2] }, request =>
            { Assert.Equal("npep.status", request.Capability); Assert.Null(request.Npep); });
            Assert.Equal(operation == "status" ? "npep.status" : "npep.command", requests[1].Capability);
            Assert.Equal(operation == "status" ? null : operation, requests[1].Npep?.Action);
            if (operation != "status") Assert.Equal(1, requests[1].Npep!.Revision);
            Assert.Equal(3, requests.Select(request => request.RequestId).Distinct().Count());
        }
        finally
        {
            timeout.Cancel();
            try { await responder; }
            catch (Exception error) when (error is OperationCanceledException or IOException or ObjectDisposedException) { }
        }
    }

    private static Task<bool> Invoke(NpepConnectionSession session, string operation) => operation switch
    {
        "pair" => session.PairAsync(),
        "pause" => session.PauseOrResumeAsync(),
        _ => session.RefreshAsync()
    };
}

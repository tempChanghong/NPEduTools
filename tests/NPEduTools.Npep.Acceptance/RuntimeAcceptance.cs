using System.Text.Json.Nodes;
using NPEduTools.Contracts;
using NPEduTools.Integrations.Npep;

internal static class RuntimeAcceptance
{
    public static async Task RunAsync(string directory, string origin, JsonObject fixture, Func<string, NpepApi> api,
        Func<HttpMessageHandler> handler, string fixturePath,
        Func<string, string, JsonObject?, Task<JsonObject>> admin, Action<bool, string> check)
    {
        int connections = 0;
        NpepDevice Device() => new(directory, target => { Interlocked.Increment(ref connections); return api(target); });
        HostResponse Sample() => new(1, Guid.NewGuid(), "Succeeded", null, "isolated runtime fixture",
            ClassroomMode: new(0, "Unconfigured"), Recording: new("Idle", "test"));
        async Task Wait(NpepRuntime runtime, Func<NpepState, bool> condition)
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(45));
            while (!condition(runtime.Snapshot()))
            {
                if (!runtime.Snapshot().Busy && runtime.Snapshot().Error is { } code) throw new NpepException(code);
                await Task.Delay(100, deadline.Token);
            }
        }
        async Task Command(NpepRuntime runtime, string action)
        {
            var state = runtime.Snapshot();
            var command = new NpepCommand(action, state.Revision,
                Origin: action is "inspect" or "pair" ? origin : null,
                DeviceName: action == "pair" ? "NPEduTools runtime acceptance" : null,
                ServerInstanceId: action == "pair" ? fixture.Text("serverInstanceId") : null,
                DeploymentEpoch: action == "pair" ? fixture.Text("deploymentEpoch") : null,
                ApprovalId: action == "confirm" ? state.Approval!.Text("approvalId") : null);
            var result = runtime.Handle(new(1, Guid.NewGuid(), "npep.command", Npep: command));
            if (result.Outcome != "Accepted") throw new NpepException(result.ErrorCode ?? "RUNTIME_REJECTED");
            await Wait(runtime, s => !s.Busy);
            if (runtime.Snapshot().Error is { } error) throw new NpepException(error);
        }
        string school = "schools/" + Uri.EscapeDataString(fixture.Text("schoolId"));
        await using (var runtime = new NpepRuntime(Device, "NPEP N1 runtime acceptance", Sample))
        {
            await Task.Delay(600);
            check(connections == 0, "Unpaired runtime does not connect");
            await Command(runtime, "inspect"); await Command(runtime, "pair");
            var pair = runtime.Snapshot().Pairing!;
            var body = NpepProtocol.Body(); body["userCode"] = pair["userCode"]!.DeepClone();
            await admin(school + "/pairings/resolve", "resolvedPairingResponse", body);
            var approval = NpepProtocol.Body(); approval["screenBindingId"] = fixture["screenBindingId"]!.DeepClone(); approval["capabilities"] = new JsonArray("device.status");
            await admin(school + "/pairings/" + pair.Text("pairingId") + "/approve", "approvedPairingResponse", approval);
            await Wait(runtime, s => s.State == "APPROVED");
            check(runtime.Snapshot().LastReceivedAt is null, "Administrator approval alone does not start reporting");
            await Command(runtime, "confirm");
            await Wait(runtime, s => s.Connection == "ONLINE");
            check(runtime.Snapshot().LastReceivedAt is not null, "Background runtime reports after local confirmation");
            await Command(runtime, "pause");
            check(runtime.Snapshot().ReportingPaused, "Pause is persisted before UI completion");
        }
        int beforeRestart = connections;
        await using (var runtime = new NpepRuntime(Device, "NPEP N1 runtime acceptance", Sample))
        {
            await Task.Delay(700);
            check(runtime.Snapshot().ReportingPaused && connections == beforeRestart, "Paused runtime restart makes no network requests");
            await Command(runtime, "resume"); await Wait(runtime, s => s.Connection == "ONLINE");
            check(runtime.Snapshot().LastReceivedAt is not null, "Resume creates a valid new Host session");
        }
        await ResilienceAcceptance.RunAsync(directory, origin, fixture, handler, fixturePath, admin, check);
        await using (var runtime = new NpepRuntime(Device, "NPEP N1 runtime acceptance", Sample))
        {
            await Wait(runtime, s => s.Connection == "ONLINE");
            await Command(runtime, "pause");
            var list = await admin(school + "/devices", "deviceListResponse", null);
            var device = ((JsonArray)list["items"]!).OfType<JsonObject>().Single(x => x.Text("state") == "ACTIVE" && x.Text("screenBindingId") == fixture.Text("screenBindingId"));
            var revoke = NpepProtocol.Body(); revoke["expectedBindingRevision"] = device["bindingRevision"]!.DeepClone();
            await admin(school + "/devices/" + device.Text("deviceId") + "/revoke", "revokedDeviceResponse", revoke);
            await Command(runtime, "resume");
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            while (runtime.Snapshot().State != "SUSPENDED") await Task.Delay(100, deadline.Token);
            check(runtime.Snapshot().Connection == "STOPPED", "Revocation suspends background runtime");
            // Revoked credentials cannot prove another remote revoke; LOCAL_ONLY is the honest cleanup result.
            runtime.Handle(new(1, Guid.NewGuid(), "npep.command", Npep: new("unpair", runtime.Snapshot().Revision)));
            await Wait(runtime, s => !s.Busy);
            check(runtime.Snapshot().State == "UNPAIRED" && runtime.Snapshot().Error == "LOCAL_ONLY", "Local cleanup does not claim a new remote revocation");
        }
    }
}

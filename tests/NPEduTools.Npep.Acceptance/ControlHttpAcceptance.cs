using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using NPEduTools.Contracts;
using NPEduTools.Integrations.Npep;

// Real .NET transport + real Node/SQL. OS effects are explicitly simulated here.
internal static class ControlHttpAcceptance
{
    public static async Task<int> RunAsync(string fixturePath, string directory, bool plans = false, bool noise = false, bool schedules = false)
    {
        try
        {
            var fixture = NpepProtocol.Parse(await File.ReadAllBytesAsync(fixturePath));
            string origin = NpepApi.ValidateOrigin(fixture.Text("origin"));
            if (!new Uri(origin).IsLoopback || fixture.Text("kind") != (schedules ? "NOISE_SCHEDULE_DISPOSABLE_DATABASE" : noise ? "NOISE_DISPOSABLE_DATABASE" : plans ? "N4_DISPOSABLE_DATABASE" : "N3_DISPOSABLE_DATABASE") || Directory.Exists(directory))
                throw new NpepException("ISOLATED_FIXTURE_REQUIRED");
            using var http = new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false, UseCookies = false })
                { BaseAddress = new Uri(origin + "/api/v2/npep/"), Timeout = TimeSpan.FromSeconds(10) };
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", fixture.Text("adminToken"));
            async Task<JsonObject> Admin(string path, string version, JsonObject? body = null)
            {
                string id = body?.Text("requestId") ?? NpepProtocol.Id();
                using var request = new HttpRequestMessage(body is null ? HttpMethod.Get : HttpMethod.Post, path);
                request.Headers.Add("X-NPEP-Version", version); request.Headers.Add("X-Request-Id", id);
                if (body is not null) request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
                using var response = await http.SendAsync(request);
                var result = NpepProtocol.Parse(await response.Content.ReadAsByteArrayAsync());
                if (result.Text("requestId") != id) throw new NpepException("INVALID_RESPONSE");
                if (!response.IsSuccessStatusCode) throw new NpepException(((JsonObject)result["error"]!).Text("code"));
                return (JsonObject)result["data"]!;
            }
            string school = "schools/" + fixture.Text("schoolId"), deviceId;
            using (var device = new NpepDevice(directory))
            {
                var info = await device.InspectServerAsync(origin);
                var view = await device.BeginAsync(origin, info, "N3 isolated .NET device", "acceptance");
                var pairing = (JsonObject)view["pairing"]!;
                var approve = NpepProtocol.Body(); approve["screenBindingId"] = fixture["screenBindingId"]!.DeepClone();
                approve["capabilities"] = new JsonArray("device.status");
                await Admin(school + "/pairings/" + pairing.Text("pairingId") + "/approve", "0.1", approve);
                var approved = await device.PollApprovalAsync();
                await device.ConfirmAsync(((JsonObject)approved["approval"]!).Text("approvalId"));
                deviceId = ((JsonObject)device.View()["registration"]!).Text("deviceId");
            }
            string path = school + "/devices/" + deviceId;
            HostResponse Sample() => new(1, Guid.NewGuid(), "Succeeded", null, "test", ClassroomMode: new(0, "Daily"), Recording: new("Idle", "test"));
            if (plans) return await ExamPlanHttpAcceptance.RunAsync(directory, path, Admin, Sample);
            if (noise) return await NoiseHttpAcceptance.RunAsync(directory, origin, fixture.Text("screenToken"), path, Admin, Sample);
            if (schedules) return await NoiseScheduleHttpAcceptance.RunAsync(directory, origin, fixture.Text("screenToken"), fixture.Text("screenPin"), path, Admin, Sample);
            async Task Wait(Func<Task<bool>> condition)
            {
                using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(45));
                while (!await condition()) await Task.Delay(100, deadline.Token);
            }
            async Task<JsonObject> History() => await Admin(path + "/runtime-operations", "0.4");
            async Task<JsonObject> Operation(string id) => ((JsonArray)(await History())["items"]!).OfType<JsonObject>().Single(x => x.Text("operationId") == id);
            var effects = new Effects();
            await using (var runtime = new NpepRuntime(directory, "N3 acceptance", Sample, effects))
            {
                await Wait(() => Task.FromResult(runtime.ControlPolicy().CanEnable));
                var policy = runtime.ControlPolicy();
                if (!policy.Allowed) throw new NpepException("PAIRING_AUTHORIZATION_MISSING");
                runtime.SetControlConsent(new("consent", policy.Revision, true, policy.Scope));
                foreach (var (target, partial) in new[] { ("EXAM", false), ("EXAM", true), ("DAILY", false) })
                {
                    effects.Partial = partial;
                    JsonObject? snapshot = null;
                    await Wait(async () =>
                    {
                        snapshot = await Admin(path + "/runtime-status", "0.4");
                        return snapshot["policy"]?["enabled"]?.GetValue<bool>() == true && snapshot["status"]?["remoteExamPause"]?.GetValue<bool>() == false &&
                            snapshot["status"]?["runtimeRevision"]?.GetValue<long>() == effects.Revision;
                    });
                    var create = NpepProtocol.Body(); create["target"] = target; create["scope"] = "EXAM_MODE";
                    foreach (var f in new[] { "runtimeRevision", "modeRevision", "configurationRevision" })
                        create["expected" + char.ToUpperInvariant(f[0]) + f[1..]] = snapshot!["status"]![f]!.DeepClone();
                    foreach (var f in new[] { "consentId", "policyRevision" }) create[f] = snapshot!["policy"]![f]!.DeepClone();
                    create["controlEpoch"] = snapshot!["controlEpoch"]!.DeepClone();
                    string id = (await Admin(path + "/runtime-operations", "0.4", create)).Text("operationId");
                    if ((await Admin(path + "/runtime-operations", "0.4", create)).Text("operationId") != id) throw new NpepException("DUPLICATE_OPERATION");
                    await Wait(async () => (await Operation(id)).Text("state") == (partial ? "PARTIAL" : "SUCCEEDED"));
                    if (target == "EXAM")
                    {
                        effects.End(Guid.Parse(id));
                        await Wait(async () => (await Operation(id))["localEndedAt"] is not null);
                    }
                    else if ((await Operation(id))["evidence"]?["remoteExamPause"]?.GetValue<bool>() != false)
                        throw new NpepException("DAILY_PAUSE_NOT_RELEASED");
                    if ((await Operation(id)).Text("state") != (partial ? "PARTIAL" : "SUCCEEDED")) throw new NpepException("HISTORY_REWRITTEN");
                    Console.WriteLine($"PASS real HTTP .NET {target} {(partial ? "partial" : "success")} receipt and duplicate request");
                }
                if (effects.Executions != 3) throw new NpepException("REPEATED_EFFECTS");
            }
            var restartedEffects = new Effects();
            await using (var restarted = new NpepRuntime(directory, "N3 restart acceptance", Sample, restartedEffects))
            {
                await Wait(() => Task.FromResult(restarted.ControlPolicy().CanEnable));
                await Wait(async () => (await Admin(path + "/runtime-status", "0.4")).Text("connectivity") == "ONLINE");
                if (!restarted.ControlPolicy().Allowed || restartedEffects.Executions != 0) throw new NpepException("RESTART_REPLAYED_CONTROL");
                Console.WriteLine("PASS real HTTP .NET restart preserves pairing authority without replay");
            }
            return 0;
        }
        catch (Exception e)
        {
            Console.Error.WriteLine("FAIL N3 HTTP acceptance: " + (e is NpepException n ? n.Code : e.GetType().Name));
            return 1;
        }
    }

    private sealed class Effects : INpepRuntimeControl
    {
        private readonly Dictionary<Guid, JsonObject> _results = new();
        private readonly HashSet<Guid> _ended = new();
        public bool Partial;
        public int Executions;
        public long Revision;
        private bool _paused;
        public Task<JsonObject> ObserveAsync(CancellationToken token) => Task.FromResult(new JsonObject
        {
            ["runtimeMode"] = "OTHER", ["runtimePhase"] = _paused ? "RECOVERY_REQUIRED" : "IDLE", ["runtimeRevision"] = Revision,
            ["modeRevision"] = 0, ["configurationRevision"] = 0, ["remoteExamPause"] = _paused,
            ["recording"] = "IDLE", ["desktop"] = "INTERACTIVE", ["noticeOpen"] = false, ["operationId"] = null, ["observedAt"] = NpepRuntimeProtocol.UtcNow()
        });
        public async Task ExecuteAsync(JsonObject op, Func<bool, CancellationToken, Task> authorize, CancellationToken token)
        {
            await authorize(false, token); await authorize(true, token);
            bool daily = op.Text("target") == "DAILY";
            Executions++; Revision++; _paused = !daily || Partial;
            _results.Add(Guid.Parse(op.Text("operationId")), new JsonObject { ["state"] = Partial ? "PARTIAL" : "SUCCEEDED", ["step"] = "VERIFY",
                ["reasonCode"] = Partial ? "EXAMAWARE_NOT_READY" : null, ["evidence"] = new JsonObject {
                    ["examAware"] = Partial ? "UNKNOWN" : daily ? "EXITED" : "READY", ["classIsland"] = Partial ? "UNKNOWN" : daily ? "READY" : "EXITED", ["remoteExamPause"] = _paused,
                    ["startup"] = daily ? "DAILY_MODE_APPLIED" : "EXAM_MODE_APPLIED", ["sideEffects"] = "APPLIED", ["alreadySatisfied"] = false,
                    ["observedAt"] = NpepRuntimeProtocol.UtcNow(), ["configurationRevision"] = 0 } });
        }
        public void End(Guid id) { _ended.Add(id); _paused = false; Revision++; }
        public Task RecoverAsync(JsonObject op, CancellationToken token) => throw new InvalidOperationException("Unexpected recovery");
        public bool HasOperation(Guid id) => _results.ContainsKey(id);
        public bool LocallyEnded(Guid id) => _ended.Contains(id);
        public Task<JsonObject> ResultAsync(Guid id, CancellationToken token) => Task.FromResult(_results[id].Copy());
    }
}

using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using NPEduTools.Contracts;
using NPEduTools.Integrations.Npep;

// Actual C# polling + Node HTTP + native PostgreSQL, with explicitly simulated player effects.
internal static class ExamPlanHttpAcceptance
{
    internal static async Task<int> RunAsync(string directory, string path,
        Func<string, string, JsonObject?, Task<JsonObject>> admin, Func<HostResponse> sample)
    {
        async Task Wait(Func<Task<bool>> check)
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(40));
            while (!await check()) await Task.Delay(100, deadline.Token);
        }
        Task<JsonObject> View() => admin(path + "/exam-plans", "0.5", null);
        JsonObject? view = null;
        var effects = new Effects();
        await using (var runtime = new NpepRuntime(directory, "N4 acceptance", sample, plans: effects))
        {
            await Wait(() => Task.FromResult(runtime.PlanPolicy().CanEnable));
            var p = runtime.PlanPolicy();
            if (!p.Allowed || !runtime.ControlPolicy().Allowed) throw new NpepException("UNEXPECTED_INITIAL_CONSENT");
            runtime.SetPlanConsent(new("plan-consent", p.Revision, true, p.Scope));
            await Wait(async () => { view = await View(); return view["status"]?["enabled"]?.GetValue<bool>() == true; });
            var create = NpepProtocol.Body(); create["context"] = view!["context"]!.DeepClone();
            foreach (string key in new[] { "consentId", "policyRevision", "revision" }) create[key] = view["status"]![key]!.DeepClone();
            create["fileName"] = "隔离测试.json"; create["dataBase64"] = Convert.ToBase64String(Encoding.UTF8.GetBytes("{\"examName\":\"隔离测试\"}"));
            var op = await admin(path + "/exam-plans", "0.5", create);
            if ((await admin(path + "/exam-plans", "0.5", create)).Text("operationId") != op.Text("operationId")) throw new NpepException("DUPLICATE_OPERATION");
            await Wait(async () =>
            {
                view = await View(); op = (JsonObject)view["items"]![0]!;
                return op.Text("state") == "PREPARED" && view["status"]?["preparedId"] is not null;
            });
            if (effects.Starts != 0 || effects.Prepares != 1) throw new NpepException("UNEXPECTED_AUTOPLAY");
            NpepExamPlanProtocol.Validate("management", view!);
            var start = NpepProtocol.Body(); start["preparationId"] = op["summary"]!["preparationId"]!.DeepClone(); start["sha256"] = op["sha256"]!.DeepClone();
            await admin(path + "/exam-plans/" + op.Text("operationId") + "/start", "0.5", start);
            await admin(path + "/exam-plans/" + op.Text("operationId") + "/start", "0.5", start);
            await Wait(async () => { view = await View(); return view["items"]![0]!["state"]!.GetValue<string>() == "STARTED" && view["status"]!["player"]!["sessions"]!.AsArray().Count == 1; });
            if (effects.Starts != 1 || view!["status"]!["player"]!["sessions"]![0]!["state"]!.GetValue<string>() != "ready") throw new NpepException("REPEATED_OR_UNREADY_START");
            Console.WriteLine("PASS N4 real .NET/HTTP/PostgreSQL: byte hash, explicit prepare/start, duplicate delivery, actual session report (simulated player)");
        }
        var restartedEffects = new Effects();
        await using var restarted = new NpepRuntime(directory, "N4 restart", sample, plans: restartedEffects);
        await Wait(() => Task.FromResult(restarted.PlanPolicy().CanEnable));
        await Wait(async () => (await View())["context"]!["controlEpoch"]!.GetValue<string>() == restarted.PlanPolicy().ControlEpoch.ToString("D"));
        if (!restarted.PlanPolicy().Allowed || restartedEffects.Starts != 0 || restartedEffects.Prepares != 0) throw new NpepException("RESTART_REPLAYED_PLAN");
        Console.WriteLine("PASS N4 restart preserves pairing authority and never replays the completed presentation");
        return 0;
    }

    private sealed class Effects : INpepExamPlans
    {
        public int Prepares, Starts;
        private JsonObject? _summary;
        public JsonObject Observe() => new() { ["revision"] = 1, ["available"] = Starts == 0, ["blockReason"] = Starts == 0 ? null : "PLAYER_BUSY",
            ["preparedId"] = _summary?["preparationId"]?.DeepClone(), ["player"] = new JsonObject { ["known"] = true,
                ["sessions"] = Starts == 0 ? new JsonArray() : new JsonArray(new JsonObject { ["id"] = "fixture-player", ["state"] = "ready", ["examName"] = "隔离测试" }), ["lastSession"] = null } };
        public Task<JsonObject> ExecuteAsync(JsonObject op, bool start, Guid commandId, Action authorize, CancellationToken token)
        {
            authorize();
            if (start) { Starts++; _summary = null; }
            else
            {
                Prepares++;
                var hash = Convert.ToHexString(SHA256.HashData(Convert.FromBase64String(op.Text("dataBase64")))).ToLowerInvariant();
                _summary = new() { ["preparationId"] = NpepProtocol.Id(), ["sha256"] = hash, ["examName"] = "隔离测试", ["message"] = "",
                    ["exams"] = new JsonArray(new JsonObject { ["name"] = "语文", ["start"] = "09:00", ["end"] = "11:00", ["alertTime"] = 15 }) };
            }
            return Task.FromResult(new JsonObject { ["state"] = start ? "STARTED" : "PREPARED", ["summary"] = _summary?.DeepClone(), ["sessionId"] = start ? "fixture-player" : null, ["reasonCode"] = null });
        }
    }
}

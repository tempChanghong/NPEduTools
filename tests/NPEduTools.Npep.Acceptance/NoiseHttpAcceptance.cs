using System.Text;
using System.Text.Json.Nodes;
using NPEduTools.Contracts;
using NPEduTools.Core;
using NPEduTools.Host;
using NPEduTools.Integrations.Npep;

internal static class NoiseHttpAcceptance
{
    internal static async Task<int> RunAsync(string directory, string origin, string screenToken, string path,
        Func<string, string, JsonObject?, Task<JsonObject>> admin, Func<HostResponse> sample)
    {
        using var http = new HttpClient { BaseAddress = new Uri(origin + "/api/v2/npep/"), Timeout = TimeSpan.FromSeconds(10) };
        http.DefaultRequestHeaders.Add("X-Classworks-Screen-Token", screenToken);
        async Task<JsonObject> Screen(JsonObject? body = null)
        {
            using var request = new HttpRequestMessage(body is null ? HttpMethod.Get : HttpMethod.Post, "screen/noise" + (body is null ? "" : "/commands"));
            request.Headers.Add("X-NPEP-Version", "0.6"); request.Headers.Add("X-Request-Id", body?.Text("requestId") ?? NpepProtocol.Id());
            if (body is not null) request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
            using var response = await http.SendAsync(request);
            var result = NpepProtocol.Parse(await response.Content.ReadAsByteArrayAsync());
            if (!response.IsSuccessStatusCode) throw new NpepException(result["error"]!["code"]!.GetValue<string>());
            return (JsonObject)result["data"]!;
        }
        async Task<JsonObject> Wait(Func<JsonObject, bool> ready)
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(40));
            while (true) { var v = await Screen(); if (ready(v)) return v; await Task.Delay(100, deadline.Token); }
        }
        var capture = new SyntheticCapture();
        await using var service = new NoiseService(_ => capture, () => []);
        var s = service.Snapshot();
        service.Handle(new(Protocol.Version, Guid.NewGuid(), "noise.command", Noise: new("select", s.InstanceId, s.Revision, "synthetic")));
        var transport = new NoiseTransport(service, directory);
        await using var runtime = new NpepRuntime(directory, "noise-acceptance", sample, noise: transport);
        var view = await Wait(v => v["provider"]?.GetValue<string>() == "native" && v["online"]?.GetValue<bool>() == true);
        JsonObject Command(string action, JsonObject value) => new() { ["requestId"] = NpepProtocol.Id(), ["action"] = action,
            ["instanceId"] = value["status"]!["instanceId"]!.DeepClone(), ["revision"] = value["status"]!["revision"]!.DeepClone(),
            ["sessionId"] = value["status"]!["sessionId"]?.DeepClone(), ["durationSeconds"] = 60 };
        var start = Command("START", view); var queued = await Screen(start);
        if ((await Screen(start))["command"]!["commandId"]!.GetValue<string>() != queued["command"]!["commandId"]!.GetValue<string>()) throw new Exception("Duplicate command");
        view = await Wait(v => v["status"]?["state"]?.GetValue<string>() == "Active" && v["commands"]![0]!["receipt"] is not null);
        if (capture.Starts != 1) throw new Exception("Repeated capture");
        await Screen(Command("STOP", view));
        view = await Wait(v => v["status"]?["state"]?.GetValue<string>() == "Stopped" && v["reports"]!.AsArray().Count == 1);
        var management = await admin(path + "/noise", "0.6", null);
        if (management["reports"]!.AsArray().Count != 1 || !capture.Disposed) throw new Exception("Missing report or microphone release");
        if (view["reports"]![0]!["summary"]!["sampledSeconds"]!.GetValue<double>() <= 0) throw new Exception("No sampled data");
        Console.WriteLine("PASS NOISE: screen-auth HTTP start/stop, actual Host state machine, synthetic PCM, one capture, KV deduplication and administrator report");
        return 0;
    }
    private sealed class SyntheticCapture : INoiseCapture
    {
        private Timer? _timer;
        public int Starts; public bool Disposed;
        public string DeviceName => "Synthetic microphone (no hardware)";
        public event Action<NoiseFrame>? Frame;
        public event Action<string>? Failed { add { } remove { } }
        public void Start() { Starts++; _timer = new(_ => Frame?.Invoke(new(0.1, 0.25, 0.5, 0, false)), null, 0, 100); }
        public void Dispose() { _timer?.Dispose(); Disposed = true; }
    }
}

using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using NPEduTools.Contracts;
using NPEduTools.Host;

internal static partial class StudyHttpAcceptance
{
    public static async Task<int> RunFieldActionsAsync(string fixturePath, string runDirectory)
    {
        var access = JsonNode.Parse(await File.ReadAllTextAsync(fixturePath))!.AsObject();
        var origin = new Uri(access["origin"]!.GetValue<string>());
        Require(access["isolated"]?.GetValue<bool>() == true && origin is { Scheme: "http", Host: "127.0.0.1", UserInfo.Length: 0 }, "Isolated loopback fixture required.");
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        using var http = new HttpClient { BaseAddress = origin, Timeout = TimeSpan.FromSeconds(8) };
        var day = DateOnly.ParseExact(access["date"]!.GetValue<string>(), "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
        var start = new DateTimeOffset(day.ToDateTime(new(19, 10)), TimeSpan.Zero);
        var evidenceRoot = Path.GetFullPath(runDirectory);
        Require(Directory.Exists(evidenceRoot), "Expected an existing isolated run directory.");
        bool withReports = access["withReports"]?.GetValue<bool>() == true;
        await using var frontPeer = new FieldPeer(http, start, evidenceRoot, access["front"]!.AsObject(), deadline.Token);
        await using var rearPeer = new FieldPeer(http, start, evidenceRoot, access["rear"]!.AsObject(), deadline.Token, withReports);
        await frontPeer.StartAsync();
        await rearPeer.StartAsync();
        await rearPeer.StopLocallyAsync();
        if (withReports) await rearPeer.UploadReportsAsync(1);
        await PublishBothAsync(probeRear: true, sampleRear: false);
        await MarkAsync("host-ready");

        await AwaitSignalAsync("armed", () => PublishBothAsync(true, false));
        await AwaitSignalAsync("allow-delivery", () => PublishBothAsync(false, false));
        var command = await rearPeer.ProbeAsync(null) ?? throw new InvalidOperationException("No resume delivery for the rear screen.");
        rearPeer.Host.ApplyStudyResume(rearPeer.Configuration, command);
        await Until(() => rearPeer.Active && rearPeer.Starts == 2);
        var processed = rearPeer.Host.ObserveStudyResumeReceipt()!;
        Require(processed["outcome"]!.GetValue<string>() == "ACCEPTED", "Host did not handle the rear screen's command.");
        Require(await rearPeer.ProbeAsync(processed) is null, "Acknowledging a command must end delivery.");
        await PublishBothAsync(false, false);
        Require(!rearPeer.Execution()["signalFresh"]!.GetValue<bool>(), "Handled without a frame must not report sampling.");
        await MarkAsync("host-handled");

        await AwaitSignalAsync("allow-sample", () => PublishBothAsync(false, false));
        await PublishBothAsync(false, true);
        Require(rearPeer.Execution()["signalFresh"]!.GetValue<bool>(), "Actual Host did not expose the fresh synthetic frame.");
        await MarkAsync("host-sampled");
        await AwaitSignalAsync("allow-local-stop", () => PublishBothAsync(false, true));

        await rearPeer.StopLocallyAsync();
        if (withReports) await rearPeer.UploadReportsAsync(1);
        rearPeer.Host.ApplyStudyResume(rearPeer.Configuration, command);
        Require(rearPeer.Host.ObserveStudyResumeReceipt()!["outcome"]!.GetValue<string>() == "ACCEPTED" && rearPeer.Starts == 2 &&
            rearPeer.Execution()["reason"]!.GetValue<string>() == "WINDOW_SKIPPED", "Duplicate delivery cleared the later locally authorized stop.");
        Require(await rearPeer.ProbeAsync(processed) is null, "An old completed command was delivered again.");
        await PublishBothAsync(false, false);
        Require(frontPeer.Execution()["signalFresh"]!.GetValue<bool>(), "Rear-screen changes affected the front screen.");
        await MarkAsync("host-restopped");

        await AwaitSignalAsync("rear-revoked", () => frontPeer.PublishAsync(sample: true));
        Require(await rearPeer.CheckRegistrationAsync() == HttpStatusCode.Unauthorized, "Old rear credential was accepted.");
        Require(await frontPeer.CheckRegistrationAsync() == HttpStatusCode.OK, "Original front credential was rejected.");
        await frontPeer.PublishAsync(sample: true);
        await frontPeer.HeartbeatAsync();
        await MarkAsync("old-credential-rejected");
        Console.WriteLine("PASS actual Host and school credential: protected local stop, distinct handled and sampled facts, second stop with replay, rear-only revocation.");
        return 0;

        async Task PublishBothAsync(bool probeRear, bool sampleRear)
        {
            await frontPeer.PublishAsync(sample: true);
            await rearPeer.PublishAsync(sampleRear);
            if (probeRear) Require(await rearPeer.ProbeAsync(null) is null, "A before-request capability probe returned a command.");
        }
        async Task MarkAsync(string name)
        {
            using var response = await http.PostAsync($"/__field/native/{name}", null, deadline.Token);
            response.EnsureSuccessStatusCode();
        }
        async Task AwaitSignalAsync(string name, Func<Task> pulse)
        {
            var nextPulse = Environment.TickCount64 + 3000;
            while (true)
            {
                using var response = await http.GetAsync($"/__field/native/{name}", deadline.Token);
                if (response.IsSuccessStatusCode) return;
                if (Environment.TickCount64 >= nextPulse)
                {
                    await pulse();
                    nextPulse = Environment.TickCount64 + 3000;
                }
                await Task.Delay(300, deadline.Token);
            }
        }
    }

    private static void Require(bool condition, string message) { if (!condition) throw new InvalidDataException(message); }

    private sealed class FieldPeer : IAsyncDisposable
    {
        private readonly HttpClient _http;
        private readonly JsonObject _access;
        private readonly CancellationToken _ct;
        private readonly Clock _time = new();
        private readonly List<Capture> _captures = [];
        private readonly NoiseService _noise;
        private readonly NoiseTransport? _transport;
        private readonly NoiseScheduleService _scheduler;
        private readonly string _stateDirectory;
        private JsonObject _context = null!;
        private long _reports, _execution, _probes, _sample, _health, _reportSequence;
        private readonly Guid _connection = Guid.NewGuid(), _bridge = Guid.NewGuid();
        private readonly string _controlEpoch = Guid.NewGuid().ToString("D");
        public StudySettingsService Host { get; }
        public JsonObject Configuration { get; private set; } = null!;
        public bool Active => _noise.Snapshot().State == "Active";
        public int Starts { get { lock (_captures) return _captures.Count; } }

        public FieldPeer(HttpClient http, DateTimeOffset start, string root, JsonObject access, CancellationToken ct, bool withReports = false)
        {
            _http = http; _access = access; _ct = ct;
            string directory = _stateDirectory = Path.Combine(root, "field-native-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            _noise = new(_ => { var capture = new Capture(); lock (_captures) _captures.Add(capture); return capture; },
                () => [new("fixture-microphone", "Synthetic")], _time, management: new NoiseManagementStore(directory, _time));
            if (withReports) _transport = new(_noise, directory);
            var initial = _noise.Snapshot();
            _noise.Handle(new(Protocol.Version, Guid.NewGuid(), "noise.command", Noise: new("select", initial.InstanceId, initial.Revision, "fixture-microphone")));
            SchoolClockFrame ReadClock() => new(_connection, _bridge, ++_sample, 0, start.AddMilliseconds(_time.Ms), 0, "Advancing", "synthetic school time");
            _scheduler = new(_noise, ReadClock, () => false, directory, _time, background: false,
                bindReports: _transport is null ? null : _transport.Bind,
                statisticsReady: _transport is null ? null : () => _transport.Available,
                studySession: _transport is null ? null : _transport.OwnStudySession);
            Host = new(ReadClock, () => false, _time, background: false, capture: _scheduler);
        }

        public async Task StartAsync()
        {
            var session = await CallAsync("sessions", "0.1", new()
            {
                ["serverInstanceId"] = _access["serverInstanceId"]!.DeepClone(), ["deploymentEpoch"] = _access["deploymentEpoch"]!.DeepClone(),
                ["runId"] = Guid.NewGuid().ToString("D"), ["expectedStatusEpoch"] = 0
            });
            Configuration = await CallAsync("study-settings", "0.10");
            var policy = await CallAsync("school-management", "0.16");
            Require(JsonNode.DeepEquals(policy["identity"], Configuration["identity"]), "The management verifier belongs to a different device.");
            string scope = Configuration["identity"]!.ToJsonString();
            Require(_noise.BindSchoolManagement(scope) is null && _noise.ApplySchoolManagement(scope, policy["credential"]!.AsObject()) is null,
                "The real Host could not apply the class management verifier.");
            _context = new() { ["sessionId"] = session["sessionId"]!.DeepClone(), ["runId"] = session["runId"]!.DeepClone(),
                ["statusEpoch"] = session["statusEpoch"]!.DeepClone(), ["identity"] = Configuration["identity"]!.DeepClone() };
            await HeartbeatAsync();
            Host.BindStudy(scope);
            for (int i = 0; i < 4; i++) { Tick(); Host.ApplyStudy(Configuration); }
            await PublishAsync(sample: false);
            await Until(() => Active);
            await PublishAsync(sample: true);
        }

        private void Tick(int milliseconds = 500) { _time.Ms += milliseconds; _scheduler.Tick(); }

        public async Task StopLocallyAsync()
        {
            var state = _noise.Snapshot();
            var stop = new HostRequest(Protocol.Version, Guid.NewGuid(), "noise.command", Noise: new("stop", state.InstanceId, state.Revision));
            Require(_noise.Handle(stop).ErrorCode == "MANAGEMENT_REQUIRED", "Scheduled capture accepted an unauthorized local stop.");
            var grant = _noise.ManagementHandle(new(Protocol.Version, Guid.NewGuid(), "noise.management.command", NoiseManagement:
                new("authorize", _access["classSecret"]!.GetValue<string>(), Purpose: "stop", TargetRequestId: stop.RequestId,
                    InstanceId: state.InstanceId, SessionId: state.SessionId)));
            Require(grant.Outcome == "Succeeded" && grant.NoiseProtection is { ManagementSource: "School", Ticket: not null }, "The class credential did not authorize the local stop.");
            Require(_noise.Handle(stop with { NoiseAuthorization = grant.NoiseProtection!.Ticket }).Outcome == "Accepted", "Authorized local stop failed.");
            await Until(() => _noise.Snapshot().State == "Stopped");
            Tick();
            Require(Execution()["reason"]!.GetValue<string>() == "WINDOW_SKIPPED", "Native scheduler did not preserve the local stop.");
        }

        public JsonObject Execution() => Host.ObserveStudyExecution() ?? throw new InvalidOperationException("No native capture status.");

        public async Task PublishAsync(bool sample)
        {
            Tick(); Host.ApplyStudy(Configuration);
            if (sample)
            {
                lock (_captures) _captures[^1].Sample();
                Tick(150);
            }
            _ = await CallAsync("study-settings/receipt", "0.10", new()
            { ["context"] = _context.DeepClone(), ["sequence"] = ++_reports, ["status"] = Host.ObserveStudy() });
            var grant = await CallAsync("study-settings/execution", "0.10", new()
            { ["context"] = _context.DeepClone(), ["sequence"] = ++_execution, ["version"] = Configuration["version"]!.DeepClone(), ["status"] = Execution() });
            Host.ConfirmStudyExecution(Configuration, grant);
        }

        public async Task<JsonObject?> ProbeAsync(JsonObject? receipt) => (await CallAsync("study-settings/resume-exchange", "0.11", new()
        { ["context"] = _context.DeepClone(), ["sequence"] = ++_probes, ["version"] = Configuration["version"]!.DeepClone(), ["receipt"] = receipt?.DeepClone() }))["command"]?.AsObject();

        public async Task UploadReportsAsync(int count)
        {
            var transport = _transport ?? throw new InvalidOperationException("Missing isolated reporting transport.");
            await Until(() => transport.Reports().Count == count);
            var reports = transport.Reports();
            Require(reports.All(report => report?["study"] is not null), "A scheduled native report lost its study scope.");
            var context = _context.DeepClone().AsObject();
            context["controlEpoch"] = _controlEpoch;
            var reply = await CallAsync("noise-exchange", "0.6", new()
            {
                ["context"] = context, ["sequence"] = ++_reportSequence, ["status"] = transport.Observe(),
                ["reports"] = reports, ["receipts"] = transport.Receipts()
            });
            var accepted = reply["acceptedReports"]!.AsArray();
            Require(accepted.Count == count, "The real KV did not acknowledge the native report upload.");
            transport.Acknowledge(reply);
            Console.WriteLine($"PASS native report upload: {string.Join(", ", accepted.Select(value => value!.GetValue<string>()))}");
        }

        public async Task HeartbeatAsync()
        {
            var bridge = new JsonObject { ["connection"] = "UNKNOWN", ["bridgeVersion"] = null };
            _ = await CallAsync("status", "0.1", new()
            {
                ["serverInstanceId"] = _access["serverInstanceId"]!.DeepClone(), ["deploymentEpoch"] = _access["deploymentEpoch"]!.DeepClone(),
                ["sessionId"] = _context["sessionId"]!.DeepClone(), ["statusEpoch"] = _context["statusEpoch"]!.DeepClone(),
                ["sequence"] = ++_health, ["sampleAgeMs"] = 0,
                ["status"] = new JsonObject { ["appVersion"] = "fixture-field-actions", ["mode"] = "DAILY", ["modePhase"] = "IDLE",
                    ["modeRevision"] = 1, ["automaticRecording"] = "DISABLED", ["recording"] = "IDLE", ["classIsland"] = bridge,
                    ["examAware"] = bridge.DeepClone() }
            });
        }

        public async Task<HttpStatusCode> CheckRegistrationAsync()
        {
            using var request = MakeRequest("me", "0.1", null);
            using var response = await _http.SendAsync(request, _ct);
            return response.StatusCode;
        }

        private async Task<JsonObject> CallAsync(string path, string version, JsonObject? body = null)
        {
            using var request = MakeRequest(path, version, body);
            using var response = await _http.SendAsync(request, _ct);
            var envelope = JsonNode.Parse(await response.Content.ReadAsStringAsync(_ct))!.AsObject();
            Require(response.IsSuccessStatusCode, $"{path}: {(int)response.StatusCode} {envelope["error"]?["code"]}");
            return envelope["data"]!.AsObject();
        }

        private HttpRequestMessage MakeRequest(string path, string version, JsonObject? body)
        {
            string id = Guid.NewGuid().ToString("D");
            var request = new HttpRequestMessage(body is null ? HttpMethod.Get : HttpMethod.Post, "/api/v2/npep/device/" + path);
            request.Headers.Add("X-NPEP-Version", version);
            request.Headers.Add("X-Request-Id", id);
            request.Headers.Authorization = new("Bearer", _access["bearer"]!.GetValue<string>());
            if (body is not null) { body["requestId"] = id; request.Content = JsonContent.Create(body); }
            return request;
        }

        public async ValueTask DisposeAsync()
        {
            await Host.DisposeAsync();
            await _scheduler.DisposeAsync();
            await _noise.DisposeAsync();
            Directory.Delete(_stateDirectory, recursive: true);
        }
    }
}

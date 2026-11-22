using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using NPEduTools.Contracts;
using NPEduTools.Core;
using NPEduTools.Host;
using NPEduTools.Integrations.Npep;

internal static partial class Program
{
    private sealed class StudyFixtureAudio : INoiseCapture
    {
        public string DeviceName => "合成测试麦克风";
        public event Action<NoiseFrame>? Frame;
        public event Action<string>? Failed { add { } remove { } }
        public void Start() { }
        public void Dispose() { }
        public void Emit() => Frame?.Invoke(new(.1, .01, .12, 0, false));
    }

    private static async Task RunSchoolStudyPhotoAcceptance(string path)
    {
        var fixture = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        var origin = new Uri(fixture["origin"]!.GetValue<string>());
        if (origin.Scheme != "http" || origin.Host != "127.0.0.1" || origin.UserInfo.Length != 0 || fixture["isolated"]?.GetValue<bool>() != true)
            throw new ArgumentException("Use an isolated loopback photo fixture.");
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        using var http = new HttpClient { BaseAddress = origin, Timeout = TimeSpan.FromSeconds(8) };
        var deployment = fixture["deployment"]!;
        string bear = fixture["bearer"]!.GetValue<string>();
        var date = DateOnly.Parse(fixture["date"]!.GetValue<string>(), CultureInfo.InvariantCulture);
        var time = new StudyUiClock();
        var now = new DateTimeOffset(date.ToDateTime(new TimeOnly(19, 20)), TimeSpan.Zero);
        var connection = Guid.NewGuid();
        var bridge = Guid.NewGuid();
        long sequence = 0;
        SchoolClockFrame SchoolClock() => new(connection, bridge, Interlocked.Increment(ref sequence), 0,
            now.AddMilliseconds(time.Ms), 0, "Advancing", "synthetic school time");
        var audio = new StudyFixtureAudio();
        await using var noise = new NoiseService(_ => audio, () => [], time);
        var noiseState = noise.Snapshot();
        noise.Handle(new(1, Guid.NewGuid(), "noise.command", Noise: new("select", noiseState.InstanceId, noiseState.Revision, "synthetic-mic")));
        var camera = new StudyDeviceService(() => [new("synthetic-camera", "合成测试摄像头")], _output);
        var cameraState = camera.Snapshot();
        camera.Handle(new(1, Guid.NewGuid(), "study.devices.select",
            StudyCamera: new("select", cameraState.InstanceId, cameraState.Revision, "synthetic-camera")));
        byte[] jpeg = await File.ReadAllBytesAsync(fixture["image"]!.GetValue<string>(), stop.Token);
        var frame = new StudyCameraFrame(jpeg, 320, 180);
        await using var capture = new NoiseScheduleService(noise, SchoolClock, () => false, _output, time, background: false);
        await using var host = new StudySettingsService(SchoolClock, () => false, time, background: false, capture: capture);
        string? scope = null;
        int captures = 0;
        await using var photos = new StudyPhotoService(() => (scope, true, host.Snapshot()), noise, camera,
            (_, _) => { captures++; return Task.FromResult(frame); },
            new StudyPhotoJournal(Path.Combine(_output, "queue"), NpepPhotoProtection.Protect, time), time, background: false);
        var session = await Call("sessions", "0.1", new()
        {
            ["serverInstanceId"] = deployment["serverInstanceId"]!.DeepClone(),
            ["deploymentEpoch"] = deployment["deploymentEpoch"]!.DeepClone(), ["runId"] = Guid.NewGuid().ToString(), ["expectedStatusEpoch"] = 0
        });
        var configuration = await Call("study-settings", "0.10");
        Assert(configuration["scope"]!["classId"]!.GetValue<string>() == fixture["classId"]!.GetValue<string>(),
            "Photo configuration must use the self-study classroom.");
        scope = configuration["identity"]!.ToJsonString();
        host.BindStudy(scope);
        photos.BindPhotos(scope);
        var context = new JsonObject
        {
            ["sessionId"] = session["sessionId"]!.DeepClone(), ["runId"] = session["runId"]!.DeepClone(),
            ["statusEpoch"] = session["statusEpoch"]!.DeepClone(), ["identity"] = configuration["identity"]!.DeepClone()
        };
        for (int i = 0; i < 4; i++) { time.Ms += 500; host.ApplyStudy(configuration); }
        await Observe(1);
        host.ConfirmStudyExecution(configuration, await Execution(1));
        for (int i = 0; i < 40 && host.Snapshot().Execution?.SignalFresh != true; i++)
        {
            await Task.Delay(15, stop.Token);
            time.Ms += 100;
            audio.Emit();
            host.ApplyStudy(configuration);
        }
        Assert(host.Snapshot().Execution is { Owner: "Schedule", State: "Active", SignalFresh: true }, "Real Host needs an active synthetic capture signal.");
        await Observe(2);
        await Execution(2);
        var leaseRequest = photos.PhotoLeaseRequest() ?? throw new InvalidDataException("No photo lease requested for an active capture.");
        leaseRequest["context"] = context.DeepClone();
        photos.ConfirmPhotoLease(await Call("study-settings/photos/lease", "0.15", leaseRequest), TimeSpan.Zero);
        for (int i = 0; i < 42 && photos.PendingPhoto() is null; i++)
        {
            time.Ms += 100;
            audio.Emit();
            photos.Tick();
            await photos.FlushAsync(stop.Token);
            await Task.Delay(100, stop.Token);
        }
        var packet = photos.PendingPhoto() ?? throw new InvalidDataException("Real Host did not queue a photo.");
        Assert(captures == 1 && packet.Metadata["reason"]!.GetValue<string>() == "Noise", "High-line qualification should capture once.");
        var uploaded = await Call("study-settings/photos/upload", "0.15", packet: packet);
        Assert(uploaded["state"]!.GetValue<string>() == "Stored", "KV must confirm stored image.");
        photos.PhotoStored(Guid.Parse(uploaded["eventId"]!.GetValue<string>()));
        Assert(photos.PendingPhoto() is null, "Acknowledged photo leaves the Host journal.");
        var exitId = Guid.NewGuid();
        var exit = await Call("study-settings/photos/exit-events", "0.15", new()
        {
            ["context"] = context.DeepClone(),
            ["event"] = new JsonObject
            {
                ["eventId"] = exitId.ToString(), ["sessionId"] = packet.Metadata["sessionId"]!.DeepClone(),
                ["version"] = packet.Metadata["version"]!.DeepClone(), ["schoolNow"] = packet.Metadata["schoolNow"]!.DeepClone(),
                ["deviceTime"] = DateTimeOffset.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'"), ["photoEventId"] = null, ["reason"] = "Timeout"
            }
        });
        Assert(exit["state"]!.GetValue<string>() == "Stored", "Cloud exit record can exist without a photo.");
        await File.WriteAllTextAsync(Path.Combine(_output, "photo-evidence.json"), new JsonObject
        {
            ["photoId"] = uploaded["eventId"]!.DeepClone(), ["digest"] = uploaded["storedHash"]!.DeepClone(),
            ["exitId"] = exitId.ToString(), ["deviceId"] = configuration["identity"]!["deviceId"]!.DeepClone(),
            ["scope"] = configuration["scope"]!.DeepClone(), ["sessionId"] = packet.Metadata["sessionId"]!.DeepClone(),
            ["capturedAt"] = packet.Metadata["capturedAt"]!.DeepClone(), ["window"] = host.ObserveStudyExecution()!["window"]!.DeepClone()
        }.ToJsonString(), stop.Token);
        Checks.Add("Actual Host scheduling, fresh audio evidence, photo lease, protected queue, upload, KV receipt and independent exit record; camera, audio and school clock are synthetic.");

        Task<JsonObject> Observe(int index) => Call("study-settings/receipt", "0.10", new()
        {
            ["context"] = context.DeepClone(), ["sequence"] = index, ["status"] = host.ObserveStudy()
        });
        Task<JsonObject> Execution(int index) => Call("study-settings/execution", "0.10", new()
        {
            ["context"] = context.DeepClone(), ["sequence"] = index, ["version"] = configuration["version"]!.DeepClone(),
            ["status"] = host.ObserveStudyExecution()
        });
        async Task<JsonObject> Call(string route, string version, JsonObject? body = null, StudyPhotoPacket? packet = null)
        {
            string id = Guid.NewGuid().ToString("D");
            using var request = new HttpRequestMessage(body is not null || packet is not null ? HttpMethod.Post : HttpMethod.Get, "/api/v2/npep/device/" + route);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bear);
            request.Headers.Add("X-NPEP-Version", version);
            request.Headers.Add("X-Request-Id", id);
            if (body is not null) { body["requestId"] = id; request.Content = JsonContent.Create(body); }
            if (packet is not null)
            {
                request.Headers.Add("X-NPEP-Photo", Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(packet.Metadata.ToJsonString()))
                    .TrimEnd('=').Replace('+', '-').Replace('/', '_'));
                request.Content = new ByteArrayContent(packet.Bytes);
                request.Content.Headers.ContentType = new MediaTypeHeaderValue("image/jpeg");
            }
            using var response = await http.SendAsync(request, stop.Token);
            var value = await response.Content.ReadFromJsonAsync<JsonObject>(cancellationToken: stop.Token);
            if (!response.IsSuccessStatusCode || value?["requestId"]?.GetValue<string>() != id)
                throw new InvalidDataException($"Isolated device {route}: {response.StatusCode} {value?["error"]?["code"]}");
            return value!["data"]!.AsObject();
        }
    }
}

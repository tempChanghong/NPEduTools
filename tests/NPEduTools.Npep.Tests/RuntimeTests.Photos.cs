using System.Security.Cryptography;
using System.Text.Json.Nodes;
using NPEduTools.Contracts;
using NPEduTools.Integrations.Npep;

namespace NPEduTools.Npep.Tests;

public sealed partial class RuntimeTests
{
    private sealed class PhotosFixture : INpepStudyPhotos, INpepStudySettings
    {
        public volatile StudyPhotoPacket? Packet;
        public volatile int Granted, Stored;
        public volatile string? Error;
        public volatile bool Renew = true;
        public void BindStudy(string? scope) { }
        public void ApplyStudy(JsonObject configuration) { }
        public JsonObject? ObserveStudy() => null;
        public void StudyFailed(string code) { }
        public void BindPhotos(string? scope) { }
        public JsonObject? PhotoLeaseRequest() => Renew ? new() { ["version"] = new string('a', 64) } : null;
        public void ConfirmPhotoLease(JsonObject response, TimeSpan roundTrip) { Granted++; Error = null; }
        public StudyPhotoPacket? PendingPhoto() => Packet;
        public void PhotoStored(Guid eventId)
        {
            Assert.Equal(Packet!.Metadata.Text("eventId"), eventId.ToString());
            Packet = null; Stored++; Error = null;
        }
        public void PhotoFailed(string code, Guid? eventId = null)
        {
            Error = code;
            if (code == "PHOTO_NOT_ELIGIBLE") Packet = null;
        }
    }
    private static StudyPhotoPacket PhotoPacket(FakeServer server)
    {
        byte[] bytes = [255, 216, 255, 217];
        var scope = new JsonObject(); var registration = server.Registration();
        foreach (var field in new[] { "serverInstanceId", "deploymentEpoch", "deviceId", "bindingRevision", "credentialGeneration" })
            scope[field] = registration[field]!.DeepClone();
        var metadata = new JsonObject
        {
            ["eventId"] = NpepProtocol.Id(), ["leaseId"] = NpepProtocol.Id(), ["sessionId"] = NpepProtocol.Id(), ["version"] = new string('a', 64),
            ["capturedAt"] = "2026-10-11T12:00:00.000Z", ["schoolNow"] = "2026-10-11T20:00:00.000", ["reason"] = "ExitAttempt", ["unit"] = "Dbfs",
            ["level"] = null, ["high"] = -30, ["continuousSeconds"] = 3, ["calibrationId"] = null,
            ["hash"] = Convert.ToHexStringLower(SHA256.HashData(bytes)), ["width"] = 2, ["height"] = 2, ["byteCount"] = bytes.Length
        };
        return new(scope.ToJsonString(), "synthetic-camera", "synthetic-mic", metadata, bytes);
    }
    [Theory]
    [InlineData("TEMPORARILY_UNAVAILABLE", 503, true)]
    [InlineData("RATE_LIMITED", 429, true)]
    [InlineData("PHOTO_EXECUTION_NOT_FRESH", 409, true)]
    [InlineData("PHOTO_MANUAL_NOT_FRESH", 409, true)]
    [InlineData("transport", 0, true)]
    [InlineData("deadline", 0, true)]
    [InlineData("AUTH_INVALID", 401, false)]
    [InlineData("PHOTOS_DISABLED", 403, false)]
    [InlineData("POLICY_CHANGED", 409, false)]
    [InlineData("INVALID_RESPONSE", 503, false)]
    [InlineData("STUDY_PHOTOS_UNSUPPORTED", 404, false)]
    [InlineData("paused", 503, false)]
    [InlineData("cancelled", 0, false)]
    [InlineData("healthy", 200, true)]
    [InlineData("no-renewal", 200, true)]
    public async Task Permitted_queued_photo_upload_is_independent_of_transient_lease_renewal(string error, int status, bool upload)
    {
        using var directory = new TestDirectory(); var server = new FakeServer(); var photos = new PhotosFixture();
        NpepRuntime? activeRuntime = null; bool fail = true; int uploads = 0;
        using var cancellation = new CancellationTokenSource();
        async Task<HttpResponseMessage> Send(HttpRequestMessage request)
        {
            string path = request.RequestUri!.AbsolutePath;
            if (!path.Contains("/study-settings/photos/", StringComparison.Ordinal)) return await server.Send(request);
            if (path.EndsWith("/upload", StringComparison.Ordinal))
            {
                uploads++; Assert.Equal(error == "healthy" ? 1 : 0, photos.Granted);
                return new(System.Net.HttpStatusCode.OK) { Content = new StringContent(new JsonObject
                {
                    ["protocolVersion"] = "0.15", ["requestId"] = request.Headers.GetValues("X-Request-Id").Single(), ["serverTime"] = "2026-10-11T12:00:00.000Z",
                    ["data"] = new JsonObject { ["eventId"] = photos.Packet!.Metadata["eventId"]!.DeepClone(), ["state"] = "Stored", ["storedHash"] = photos.Packet.Metadata["hash"]!.DeepClone() }
                }.ToJsonString()) };
            }
            if (fail && error != "healthy")
            {
                if (error == "transport") throw new HttpRequestException("Synthetic lease transport loss");
                if (error == "deadline") throw new OperationCanceledException("Synthetic lease request deadline");
                if (error == "cancelled") { cancellation.Cancel(); throw new OperationCanceledException(cancellation.Token); }
                if (error == "paused") Assert.Equal("Accepted", activeRuntime!.Handle(new(1, Guid.NewGuid(), "npep.command", Npep: new("pause", activeRuntime.Snapshot().Revision))).Outcome);
                var response = Fixtures.Reply(request, new(), error == "paused" ? "TEMPORARILY_UNAVAILABLE" : error, status);
                var envelope = JsonNode.Parse(await response.Content.ReadAsStringAsync())!.AsObject(); envelope["protocolVersion"] = "0.15";
                response.Content = new StringContent(envelope.ToJsonString()); return response;
            }
            return new(System.Net.HttpStatusCode.OK) { Content = new StringContent(new JsonObject
            {
                ["protocolVersion"] = "0.15", ["requestId"] = request.Headers.GetValues("X-Request-Id").Single(), ["serverTime"] = "2026-10-11T12:00:00.000Z",
                ["data"] = new JsonObject { ["leaseId"] = NpepProtocol.Id(), ["version"] = new string('a', 64), ["sessionId"] = NpepProtocol.Id(), ["leaseSeconds"] = 60,
                    ["createdAt"] = "2026-10-11T12:00:00.000Z", ["expiresAt"] = "2026-10-11T12:01:00.000Z", ["maxBytes"] = 262144, ["maxEdge"] = 1280 }
            }.ToJsonString()) };
        }
        await using var runtime = new NpepRuntime(() => new(directory.Path, origin => new(origin, new Handler(Send))), "photo-recovery", Sample);
        activeRuntime = runtime;
        await Command(runtime, "inspect"); await Command(runtime, "pair", server); await Command(runtime, "poll"); await Command(runtime, "confirm", server);
        await Until(() => runtime.Snapshot().Connection == "ONLINE"); photos.Packet = PhotoPacket(server);
        photos.Renew = error != "no-renewal";
        var code = await runtime.StudyPhotoCycleAsync(photos, cancellation.Token);
        Assert.Equal(upload ? 1 : 0, uploads); Assert.Equal(upload ? 1 : 0, photos.Stored);
        Assert.Equal(error == "healthy" ? 1 : 0, photos.Granted);
        Assert.Equal(error is "healthy" or "no-renewal" ? null : error is "transport" or "deadline" or "cancelled" ? "PHOTO_OFFLINE" : error == "paused" ? "SESSION_SUPERSEDED" : error, code);
        Assert.Equal(error == "paused" ? null : code, photos.Error);
        if (upload)
        {
            Assert.Null(photos.Packet); fail = false; photos.Renew = true;
            Assert.Null(await runtime.StudyPhotoCycleAsync(photos, CancellationToken.None));
            Assert.Null(photos.Error);
            Assert.Equal(error == "healthy" ? 2 : 1, photos.Granted); Assert.Equal(1, uploads); // No duplicate upload or fabricated lease.
        }
        else Assert.NotNull(photos.Packet);
    }
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task Queued_upload_without_renewal_is_fenced_before_network_when_binding_or_pause_changes(bool pause)
    {
        using var directory = new TestDirectory(); var server = new FakeServer(); int uploads = 0;
        await using var runtime = new NpepRuntime(() => new(directory.Path, origin => new(origin, new Handler(async request =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/upload", StringComparison.Ordinal)) uploads++;
            return await server.Send(request);
        }))), "photo-fences", Sample);
        await Command(runtime, "inspect"); await Command(runtime, "pair", server); await Command(runtime, "poll"); await Command(runtime, "confirm", server);
        await Until(() => runtime.Snapshot().Connection == "ONLINE");
        var photos = new PhotosFixture { Renew = false, Packet = PhotoPacket(server) };
        if (pause) await Command(runtime, "pause");
        else
        {
            var wrongScope = JsonNode.Parse(photos.Packet.Scope)!.AsObject(); wrongScope["deviceId"] = NpepProtocol.Id();
            photos.Packet = photos.Packet with { Scope = wrongScope.ToJsonString() };
        }
        Assert.NotNull(await runtime.StudyPhotoCycleAsync(photos, CancellationToken.None));
        Assert.Equal(0, uploads); Assert.Equal(0, photos.Stored); Assert.NotNull(photos.Packet);
    }

    [Theory]
    [InlineData("lease", 200)] [InlineData("lease", 401)]
    [InlineData("upload", 200)] [InlineData("upload", 401)]
    public async Task Slow_photo_exchange_leaves_heartbeat_free_and_fences_old_pause_results(string slow, int status)
    {
        using var directory = new TestDirectory();
        var server = new FakeServer();
        using (var paired = new NpepDevice(directory.Path, server.Api)) await server.ActivateAsync(paired);
        var photos = new PhotosFixture { Packet = PhotoPacket(server), Renew = slow == "lease" };
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        NpepDevice? device = null;
        async Task<HttpResponseMessage> Send(HttpRequestMessage request)
        {
            if (!request.RequestUri!.AbsolutePath.EndsWith("/photos/" + slow, StringComparison.Ordinal)) return await server.Send(request);
            entered.TrySetResult();
            await release.Task;
            return await PhotoCycleReply(request, photos.Packet!, status);
        }
        await using var runtime = new NpepRuntime(() => device = new(directory.Path, origin => new(origin, new Handler(Send))),
            "photo-slow", Sample);
        await Until(() => runtime.Snapshot().Connection == "ONLINE");
        var cycle = runtime.StudyPhotoCycleAsync(photos, default);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var heartbeat = await device!.ReportAsync(NpepStatusReader.Map(Sample(), "photo-slow"), 0).WaitAsync(TimeSpan.FromSeconds(2));
            Assert.Equal(2, heartbeat.Number("acceptedSequence"));
            Assert.True((await Command(runtime, "pause")).ReportingPaused);
            Assert.False((await Command(runtime, "resume")).ReportingPaused);
            release.TrySetResult();
            Assert.Equal("SESSION_SUPERSEDED", await cycle);
            Assert.Equal("ACTIVE", device.View().Text("state"));
            Assert.Equal(0, photos.Granted);
            Assert.Equal(0, photos.Stored);
            Assert.NotNull(photos.Packet);
        }
        finally { release.TrySetResult(); await cycle; }
    }

    [Fact]
    public async Task Photo_pause_keeps_the_packet_and_shared_retry_after_delays_both_exchanges()
    {
        using var directory = new TestDirectory();
        var server = new FakeServer();
        using (var paired = new NpepDevice(directory.Path, server.Api)) await server.ActivateAsync(paired);
        var photos = new PhotosFixture { Renew = false };
        var time = new ReceiptTime();
        var packet = PhotoPacket(server);
        var attempts = new System.Collections.Concurrent.ConcurrentQueue<string>();
        async Task<HttpResponseMessage> Send(HttpRequestMessage request)
        {
            if (await ExitPolicyReply(request, server) is { } policy) return policy;
            string path = request.RequestUri!.AbsolutePath;
            if (!path.Contains("/study-settings/photos/", StringComparison.Ordinal)) return await server.Send(request);
            bool lease = path.EndsWith("/lease", StringComparison.Ordinal);
            attempts.Enqueue(lease ? "lease" : "upload");
            var reply = await PhotoCycleReply(request, packet, attempts.Count == 1 ? 429 : 200);
            if (attempts.Count == 1) reply.Headers.RetryAfter = new(TimeSpan.FromSeconds(12));
            return reply;
        }
        await using var runtime = new NpepRuntime(() => new(directory.Path, origin => new(origin, new Handler(Send))),
            "photo-backoff", Sample, study: photos, receiptTime: time);
        await Until(() => runtime.Snapshot().Connection == "ONLINE");
        Assert.True((await Command(runtime, "pause")).ReportingPaused);
        photos.Packet = packet;
        photos.Renew = true;
        await Task.Delay(3300);
        Assert.Empty(attempts);
        Assert.Same(packet, photos.Packet);
        Assert.False((await Command(runtime, "resume")).ReportingPaused);
        await Until(() => photos.Error == "RATE_LIMITED", TimeSpan.FromSeconds(10));
        time.Advance(TimeSpan.FromSeconds(8));
        await Task.Delay(3300);
        Assert.Equal(new[] { "lease" }, attempts);
        Assert.Same(packet, photos.Packet);
        time.Advance(TimeSpan.FromSeconds(4));
        await Until(() => photos.Stored == 1);
        Assert.Equal(new[] { "lease", "lease", "upload" }, attempts);
    }

    private static async Task<HttpResponseMessage> PhotoCycleReply(HttpRequestMessage request, StudyPhotoPacket packet, int status)
    {
        var info = request.RequestUri!.AbsolutePath.EndsWith("/lease", StringComparison.Ordinal)
            ? new JsonObject { ["leaseId"] = NpepProtocol.Id(), ["version"] = packet.Metadata.Text("version"), ["sessionId"] = packet.Metadata.Text("sessionId"),
                ["leaseSeconds"] = 60, ["createdAt"] = "2026-11-27T11:00:00.000Z", ["expiresAt"] = "2026-11-27T11:01:00.000Z",
                ["maxBytes"] = 262144, ["maxEdge"] = 1280 }
            : new JsonObject { ["eventId"] = packet.Metadata.Text("eventId"), ["state"] = "Stored", ["storedHash"] = packet.Metadata.Text("hash") };
        var reply = Fixtures.Reply(request, info, status == 429 ? "RATE_LIMITED" : status == 401 ? "AUTH_INVALID" : null, status);
        using var content = reply.Content;
        var envelope = JsonNode.Parse(await content.ReadAsStringAsync())!.AsObject();
        envelope["protocolVersion"] = "0.15";
        reply.Content = new StringContent(envelope.ToJsonString());
        return reply;
    }
}

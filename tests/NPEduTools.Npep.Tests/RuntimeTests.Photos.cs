using System.Security.Cryptography;
using System.Text.Json.Nodes;
using NPEduTools.Contracts;
using NPEduTools.Integrations.Npep;

namespace NPEduTools.Npep.Tests;

public sealed partial class RuntimeTests
{
    private sealed class PhotosFixture : INpepStudyPhotos
    {
        public StudyPhotoPacket? Packet;
        public int Granted, Stored;
        public string? Error;
        public bool Renew = true;
        public void BindPhotos(string? scope) { }
        public JsonObject? PhotoLeaseRequest() => Renew ? new() { ["version"] = new string('a', 64) } : null;
        public void ConfirmPhotoLease(JsonObject response, TimeSpan roundTrip) { Granted++; Error = null; }
        public StudyPhotoPacket? PendingPhoto() => Packet;
        public void PhotoStored(Guid eventId)
        {
            Assert.Equal(Packet!.Metadata.Text("eventId"), eventId.ToString());
            Packet = null; Stored++; Error = null;
        }
        public void PhotoFailed(string code, Guid? eventId = null) => Error = code;
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
        Assert.Equal(code, photos.Error); // Upload success must not hide the failed capture-permission renewal.
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
}

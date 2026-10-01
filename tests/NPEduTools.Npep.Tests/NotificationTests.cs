using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using NPEduTools.Contracts;
using NPEduTools.Integrations.Npep;

namespace NPEduTools.Npep.Tests;

public sealed class NotificationTests
{
    private static readonly string Scope = new('a', 64);
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-09-25T01:00:00Z");
    private static NpepNotice Notice(string id = "notice-1", long revision = 1, string priority = "NORMAL", bool popup = true) =>
        new(id, revision, "学校通知", "纯文本正文", priority, Now.AddMinutes(-1), Now.AddHours(1), "学校", popup);
    private static NpepInbox Inbox(TestDirectory directory) { Directory.CreateDirectory(directory.Path); var inbox = new NpepInbox(directory.Path); inbox.Prepare(Scope); return inbox; }
    private static NpepInboxState View(NpepInbox inbox) => inbox.Snapshot(true, new("poll"));
    private static NpepNotificationCommand Mark(string action, NpepNotice n, string? scope = null) => new(action, scope ?? Scope, n.PublicationId, n.Revision);

    [Fact]
    public void ReceiptIdsAndManualDismissalSurviveRestartAndDoNotRepeat()
    {
        using var directory = new TestDirectory(); var inbox = Inbox(directory); var notice = Notice();
        inbox.Apply(Scope, new(Now, 10, [notice]));
        string eventId = inbox.Pending()!["events"]![0]!["eventId"]!.GetValue<string>();
        inbox.Apply(Scope, new(Now, 10, [notice]));
        Assert.Single((JsonArray)inbox.Pending()!["events"]!);
        Assert.False(inbox.Mark(Mark("dismissed", notice))); // Merely queued is not displayed.
        Assert.True(inbox.Mark(Mark("displayed", notice)));
        Assert.True(inbox.Mark(Mark("dismissed", notice)));
        Assert.Null(View(inbox).Current);
        var restarted = new NpepInbox(directory.Path); restarted.Prepare(Scope);
        Assert.False(View(restarted).CanPresent);
        Assert.Equal(eventId, restarted.Pending()!["events"]![0]!["eventId"]!.GetValue<string>());
        restarted.Apply(Scope, new(Now, 10, [notice]));
        Assert.Null(View(restarted).Current);
        Assert.DoesNotContain("纯文本正文", System.Text.Encoding.UTF8.GetString(File.ReadAllBytes(Path.Combine(directory.Path, "npep.notifications.dpapi"))));
    }

    [Fact]
    public void PopupFlagScopeExpiryAndRevisionFenceActualDisplay()
    {
        using var directory = new TestDirectory(); var inbox = Inbox(directory);
        var silent = Notice("minor", priority: "MINOR", popup: false); var normal = Notice();
        inbox.Apply(Scope, new(Now, 10, [silent, normal]));
        Assert.Equal(normal, View(inbox).Current);
        Assert.Equal(silent, inbox.Snapshot(true, Mark("get", silent)).Current);
        Assert.True(inbox.Mark(Mark("displayed", silent))); // Explicit list open is valid.
        Assert.False(inbox.Mark(Mark("dismissed", silent, new string('b', 64))));
        var updated = normal with { Revision = 2, Content = "更正正文" };
        inbox.Apply(Scope, new(Now, 10, [updated]));
        Assert.Null(inbox.Snapshot(true, Mark("get", normal)).Current);
        Assert.False(inbox.Mark(Mark("displayed", normal)));
        inbox.Apply(Scope, new(Now.AddHours(2), 10, [updated]));
        Assert.Empty(View(inbox).Items);
        Assert.Null(View(inbox).Current);
        Assert.Empty(inbox.Snapshot(false, new("poll")).Items);
        inbox.Prepare(new string('c', 64)); Assert.Null(inbox.Pending());
    }

    [Fact]
    public void PartialOrUncorrelatedReceiptResponseCannotEraseOutbox()
    {
        using var directory = new TestDirectory(); var inbox = Inbox(directory); inbox.Apply(Scope, new(Now, 10, [Notice(), Notice("notice-2")]));
        var sent = inbox.Pending()!;
        Assert.Throws<NpepException>(() => inbox.Complete(sent, new() { ["results"] = new JsonArray() }));
        Assert.Equal(2, ((JsonArray)inbox.Pending()!["events"]!).Count);
        var results = new JsonArray(((JsonArray)sent["events"]!).Select(e => (JsonNode)new JsonObject
        { ["eventId"] = e!["eventId"]!.DeepClone(), ["status"] = "DUPLICATE", ["code"] = null }).ToArray());
        inbox.Complete(sent, new() { ["results"] = results }); Assert.Null(inbox.Pending());
    }

    [Fact]
    public async Task MaximumNoticeFitsExistingLocalFrameLimit()
    {
        using var directory = new TestDirectory(); var inbox = Inbox(directory);
        var notices = Enumerable.Range(0, 20).Select(i => Notice("id" + i) with { Title = new('题', 160), Content = new('文', 8000), Source = new('校', 120) }).ToList();
        inbox.Apply(Scope, new(Now, 10, notices));
        var state = View(inbox); Assert.Equal(10, state.NextOffset);
        using var stream = new MemoryStream();
        await Protocol.WriteAsync(stream, new HostResponse(1, Guid.NewGuid(), "Succeeded", null, "", Inbox: state), default);
        Assert.True(stream.Length <= Protocol.MaxFrameBytes + 4);
    }

    private static HttpResponseMessage Reply(HttpRequestMessage request, JsonObject data, int status = 200) =>
        new((HttpStatusCode)status) { Content = new StringContent(new JsonObject
        { ["protocolVersion"] = "0.2", ["requestId"] = request.Headers.GetValues("X-Request-Id").Single(), ["serverTime"] = Now.ToString("O"),
          [status == 200 ? "data" : "error"] = data }.ToJsonString()) };
    private static JsonObject Page(string snapshot, NpepNotice notice, string? cursor = null) => new()
    { ["snapshotId"] = snapshot, ["serverTime"] = Now.ToString("O"), ["pollAfterSeconds"] = 10, ["items"] = JsonSerializer.SerializeToNode(new[] { notice }, Protocol.Json), ["nextCursor"] = cursor };

    [Fact]
    public async Task ExistingCredentialReadsAllPagesAndRejectsChangedSnapshot()
    {
        using var directory = new TestDirectory(); var server = new FakeServer(); string snapshot = Guid.NewGuid().ToString(); int pages = 0; bool invalid = false;
        using var device = new NpepDevice(directory.Path, origin => new NpepApi(origin, new Handler(request =>
        {
            if (!request.RequestUri!.AbsolutePath.EndsWith("/notifications")) return server.Send(request);
            Assert.Equal("0.2", request.Headers.GetValues("X-NPEP-Version").Single());
            Assert.StartsWith("npep1.", request.Headers.Authorization!.Parameter);
            pages++;
            return Task.FromResult(Reply(request, request.RequestUri.Query.Length == 0
                ? Page(snapshot, Notice(), "next-token")
                : Page(invalid ? Guid.NewGuid().ToString() : snapshot, Notice("second"))));
        })));
        await server.ActivateAsync(device);
        var result = await device.FetchNotificationsAsync(default);
        Assert.Equal(2, pages); Assert.Equal(2, result.Items.Count);
        invalid = true;
        await Assert.ThrowsAsync<NpepException>(() => device.FetchNotificationsAsync(default));
        Assert.Equal("ACTIVE", device.View().Text("state"));
    }

    [Fact]
    public async Task NotificationRevocationSuspendsOldCredential()
    {
        using var directory = new TestDirectory(); var server = new FakeServer();
        using var device = new NpepDevice(directory.Path, origin => new NpepApi(origin, new Handler(request =>
            request.RequestUri!.AbsolutePath.EndsWith("/notifications") ? Task.FromResult(Reply(request,
                new() { ["code"] = "AUTH_INVALID", ["message"] = "revoked", ["retryAfterSeconds"] = null }, 401)) : server.Send(request))));
        await server.ActivateAsync(device);
        await Assert.ThrowsAsync<NpepException>(() => device.FetchNotificationsAsync(default));
        Assert.Equal("SUSPENDED", device.View().Text("state"));
    }

    [Fact]
    public async Task BackgroundDeliveryRetriesSameReceiptsAndPauseResumeKeepsDismissal()
    {
        using var directory = new TestDirectory(); var server = new FakeServer();
        var delivered = new System.Collections.Concurrent.ConcurrentQueue<JsonObject>();
        int fetches = 0, uploads = 0;
        NpepApi Api(string origin) => new(origin, new Handler(async request =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/notifications"))
            {
                Interlocked.Increment(ref fetches);
                return Reply(request, Page(Guid.NewGuid().ToString(), Notice()));
            }
            if (!request.RequestUri.AbsolutePath.EndsWith("/notification-receipts")) return await server.Send(request);
            var body = NpepProtocol.Parse(await request.Content!.ReadAsByteArrayAsync());
            delivered.Enqueue(body.Copy());
            if (Interlocked.Increment(ref uploads) == 1) throw new HttpRequestException("Lost receipt response");
            return Reply(request, new() { ["results"] = new JsonArray(((JsonArray)body["events"]!).Select(e => (JsonNode)new JsonObject
                { ["eventId"] = e!["eventId"]!.DeepClone(), ["status"] = "DUPLICATE", ["code"] = null }).ToArray()) });
        }));
        using (var device = new NpepDevice(directory.Path, Api)) await server.ActivateAsync(device);
        await using var runtime = new NpepRuntime(() => new(directory.Path, Api), "test", () => new(1, Guid.NewGuid(), "Succeeded", null, ""));
        NpepInboxState ViewRuntime() => runtime.Handle(new(1, Guid.NewGuid(), "npep.notifications", Notification: new("poll"))).Inbox!;
        async Task Until(Func<bool> condition)
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            while (!condition()) await Task.Delay(30, deadline.Token);
        }
        async Task Command(string action)
        {
            Assert.Equal("Accepted", runtime.Handle(new(1, Guid.NewGuid(), "npep.command", Npep: new(action, runtime.Snapshot().Revision))).Outcome);
            await Until(() => !runtime.Snapshot().Busy);
        }
        await Until(() => Volatile.Read(ref uploads) == 1 && ViewRuntime().CanPresent);
        var first = ViewRuntime(); Assert.NotNull(first.Current); // Lost ACK does not suppress durable delivery.
        foreach (string action in new[] { "displayed", "dismissed" })
            Assert.Equal("Succeeded", runtime.Handle(new(1, Guid.NewGuid(), "npep.notifications",
                Notification: new(action, first.Scope, first.Current!.PublicationId, first.Current.Revision))).Outcome);
        await Command("pause");
        int pausedCount = Volatile.Read(ref fetches);
        await Task.Delay(650);
        Assert.Equal(pausedCount, Volatile.Read(ref fetches)); Assert.Empty(ViewRuntime().Items);
        await Command("resume");
        await Until(() => Volatile.Read(ref uploads) >= 2 && ViewRuntime().CanPresent);
        Assert.Null(ViewRuntime().Current); Assert.True(Assert.Single(ViewRuntime().Items).Dismissed);
        var attempts = delivered.ToArray();
        Assert.Equal(attempts[0]["events"]![0]!["eventId"]!.GetValue<string>(), attempts[1]["events"]![0]!["eventId"]!.GetValue<string>());
        Assert.Equal(new[] { "RECEIVED", "DISPLAYED", "DISMISSED" }, ((JsonArray)attempts[1]["events"]!).Select(e => e!["stage"]!.GetValue<string>()));
    }

    [Theory]
    [InlineData("popupEnabled")]
    [InlineData("expiresAt")]
    [InlineData("revision")]
    public async Task MissingRequiredNotificationFieldCannotReplaceStoredSnapshot(string field)
    {
        using var directory = new TestDirectory(); var server = new FakeServer();
        using var device = new NpepDevice(directory.Path, origin => new NpepApi(origin, new Handler(request =>
        {
            if (!request.RequestUri!.AbsolutePath.EndsWith("/notifications")) return server.Send(request);
            var page = Page(Guid.NewGuid().ToString(), Notice("new")); ((JsonObject)page["items"]![0]!).Remove(field);
            return Task.FromResult(Reply(request, page));
        })));
        await server.ActivateAsync(device);
        var inbox = Inbox(directory); inbox.Apply(Scope, new(Now, 10, [Notice("old")]));
        await Assert.ThrowsAsync<NpepException>(async () => inbox.Apply(Scope, await device.FetchNotificationsAsync(default)));
        Assert.Equal("old", Assert.Single(View(inbox).Items).PublicationId);
    }
}

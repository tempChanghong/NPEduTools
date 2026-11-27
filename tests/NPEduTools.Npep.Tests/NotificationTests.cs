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

    [Theory]
    [InlineData("𠮷")]
    [InlineData("👩‍🏫")]
    [InlineData("A\u0301")]
    public void InboxTitleEndsOnACompleteTextElement(string element)
    {
        using var directory = new TestDirectory();
        var inbox = Inbox(directory);
        string prefix = new('课', 63);
        var notice = Notice() with { Title = prefix + element + "下节课" };
        inbox.Apply(Scope, new(Now, 10, [notice]));
        var page = View(inbox);
        Assert.Equal(prefix + "…", Assert.Single(page.Items).Title);
        Assert.Equal(notice.Title, inbox.Snapshot(true, Mark("get", notice)).Current!.Title);
    }

    [Fact]
    public void InboxShowsAnUntitledNoticeAndPreservesShortTitles()
    {
        using var directory = new TestDirectory();
        var inbox = Inbox(directory);
        string shortTitle = new string('课', 62) + "𠮷";
        inbox.Apply(Scope, new(Now, 10, [Notice("a") with { Title = "  " }, Notice("b") with { Title = shortTitle }]));
        Assert.Equal(new[] { "学校通知", shortTitle }, View(inbox).Items.Select(row => row.Title));
    }

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
        { ["protocolVersion"] = request.Headers.GetValues("X-NPEP-Version").Single(), ["requestId"] = request.Headers.GetValues("X-Request-Id").Single(), ["serverTime"] = Now.ToString("O"),
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

    [Theory]
    [InlineData("SNAPSHOT_INVALIDATED", 409)]
    [InlineData("SNAPSHOT_EXPIRED", 410)]
    public async Task Lost_cursor_restarts_at_page_one_and_never_combines_snapshots(string code, int status)
    {
        using var directory = new TestDirectory();
        var server = new FakeServer();
        int reads = 0;
        using var device = new NpepDevice(directory.Path, origin => new NpepApi(origin, new Handler(request =>
        {
            if (!request.RequestUri!.AbsolutePath.EndsWith("/notifications")) return server.Send(request);
            reads++;
            if (reads == 2)
            {
                Assert.Contains("cursor=former-cursor", request.RequestUri.Query);
                return Task.FromResult(Reply(request, new() { ["code"] = code, ["message"] = code }, status));
            }
            Assert.Empty(request.RequestUri.Query);
            return Task.FromResult(reads == 1
                ? Reply(request, Page(Guid.NewGuid().ToString(), Notice("discarded"), "former-cursor"))
                : Reply(request, Page(Guid.NewGuid().ToString(), Notice("current"))));
        })));
        await server.ActivateAsync(device);
        var snapshot = await device.FetchNotificationsAsync(default);
        Assert.Equal(3, reads);
        Assert.Equal("current", Assert.Single(snapshot.Items).PublicationId);
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
    public async Task Slow_second_notification_page_does_not_hold_heartbeat_or_apply_partial_snapshot()
    {
        using var directory = new TestDirectory();
        var server = new FakeServer();
        var secondPage = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        string snapshot = Guid.NewGuid().ToString("D");
        using var device = new NpepDevice(directory.Path, origin => new NpepApi(origin, new Handler(async request =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/study-settings"))
            {
                var configuration = StudyProtocolTests.Configuration;
                configuration["identity"] = NpepDevice.RegistrationIdentity(server.Registration());
                return Reply(request, configuration);
            }
            if (!request.RequestUri!.AbsolutePath.EndsWith("/notifications")) return await server.Send(request);
            if (request.RequestUri.Query.Length == 0) return Reply(request, Page(snapshot, Notice("first"), "next-token"));
            secondPage.TrySetResult();
            await release.Task;
            return Reply(request, Page(snapshot, Notice("second")));
        })));
        await server.ActivateAsync(device);
        await device.OpenSessionAsync();
        var download = device.FetchNotificationsAsync(default);
        try
        {
            await secondPage.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(download.IsCompleted);
            var sample = NpepStatusReader.Map(new(1, Guid.NewGuid(), "Succeeded", null, ""), "notification-isolation");
            var receipt = await device.ReportAsync(sample, 0).WaitAsync(TimeSpan.FromSeconds(2));
            Assert.Equal(1, receipt.Number("acceptedSequence"));
            var context = await device.ControlContextAsync(Guid.Empty, default);
            context.Remove("controlEpoch");
            var study = await device.StudyAsync(context, null, default).WaitAsync(TimeSpan.FromSeconds(2));
            Assert.Equal(StudyProtocolTests.Configuration.Text("version"), study.Text("version"));
            Assert.False(download.IsCompleted);
        }
        finally { release.TrySetResult(); await download; }
        Assert.Equal(new[] { "first", "second" }, (await download).Items.Select(notice => notice.PublicationId));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task An_outstanding_notification_page_does_not_block_pause_and_late_results_are_discarded(bool refused)
    {
        using var directory = new TestDirectory();
        var server = new FakeServer();
        var waiting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        string snapshot = Guid.NewGuid().ToString("D");
        int reads = 0;
        NpepApi Api(string origin) => new(origin, new Handler(async request =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/notifications"))
            {
                switch (Interlocked.Increment(ref reads))
                {
                    case 1: return Reply(request, Page(snapshot, Notice("old-page-one"), "next-token"));
                    case 2:
                        waiting.TrySetResult();
                        await release.Task; // This in-flight response can outlive cancellation at the transport boundary.
                        return refused ? Reply(request, new() { ["code"] = "AUTH_INVALID", ["message"] = "old refusal", ["retryAfterSeconds"] = null }, 401)
                            : Reply(request, Page(snapshot, Notice("old-page-two")));
                    default: return Reply(request, Page(Guid.NewGuid().ToString("D"), Notice("fresh")));
                }
            }
            if (request.RequestUri.AbsolutePath.EndsWith("/notification-receipts"))
            {
                var body = JsonNode.Parse(await request.Content!.ReadAsStringAsync())!.AsObject();
                return Reply(request, new() { ["results"] = new JsonArray(body["events"]!.AsArray().Select(evt => (JsonNode)new JsonObject
                    { ["eventId"] = evt!["eventId"]!.DeepClone(), ["status"] = "ACCEPTED", ["code"] = null }).ToArray()) });
            }
            return await server.Send(request);
        }));
        using (var device = new NpepDevice(directory.Path, Api)) await server.ActivateAsync(device);
        await using var runtime = new NpepRuntime(() => new(directory.Path, Api), "notification-isolation", () => new(1, Guid.NewGuid(), "Succeeded", null, ""));
        NpepInboxState InboxView() => runtime.Handle(new(1, Guid.NewGuid(), "npep.notifications", Notification: new("poll"))).Inbox!;
        async Task Until(Func<bool> check)
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(4));
            while (!check()) await Task.Delay(30, deadline.Token);
        }
        async Task Command(string action)
        {
            Assert.Equal("Accepted", runtime.Handle(new(1, Guid.NewGuid(), "npep.command", Npep: new(action, runtime.Snapshot().Revision))).Outcome);
            await Until(() => !runtime.Snapshot().Busy);
        }
        try
        {
            await waiting.Task.WaitAsync(TimeSpan.FromSeconds(8));
            Assert.Empty(InboxView().Items);
            await Command("pause");
            Assert.True(runtime.Snapshot().ReportingPaused);
            Assert.Empty(InboxView().Items);
            await Command("resume");
            Assert.False(runtime.Snapshot().ReportingPaused);
            release.TrySetResult();
            await Until(() => InboxView().CanPresent && runtime.Snapshot().Connection == "ONLINE");
            Assert.Equal("fresh", Assert.Single(InboxView().Items).PublicationId);
            Assert.Equal("ACTIVE", runtime.Snapshot().State);
        }
        finally { release.TrySetResult(); }
    }

    [Fact]
    public async Task Late_receipt_ack_after_pause_keeps_its_event_until_a_current_ack_is_read()
    {
        using var directory = new TestDirectory();
        var server = new FakeServer();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var device = new NpepDevice(directory.Path, origin => new NpepApi(origin, new Handler(async request =>
        {
            if (!request.RequestUri!.AbsolutePath.EndsWith("/notification-receipts")) return await server.Send(request);
            var body = JsonNode.Parse(await request.Content!.ReadAsStringAsync())!.AsObject();
            if (entered.TrySetResult()) await release.Task;
            return Reply(request, new() { ["results"] = new JsonArray(new JsonObject
                { ["eventId"] = body["events"]![0]!["eventId"]!.DeepClone(), ["status"] = "DUPLICATE", ["code"] = null }) });
        })));
        await server.ActivateAsync(device);
        string scope = device.NotificationScope();
        var inbox = new NpepInbox(directory.Path);
        inbox.Prepare(scope);
        inbox.Apply(scope, new(Now, 10, [Notice()]));
        var sent = inbox.Pending()!;
        var late = device.SendNotificationReceiptsAsync(sent, default, scope);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(4));
            using var pauseTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            await device.SetReportingPausedAsync(true, pauseTimeout.Token);
            await device.SetReportingPausedAsync(false, pauseTimeout.Token);
            release.TrySetResult();
            Assert.Equal("SESSION_SUPERSEDED", (await Assert.ThrowsAsync<NpepException>(() => late)).Code);
            var pending = inbox.Pending()!;
            Assert.True(JsonNode.DeepEquals(sent["events"], pending["events"]));
            inbox.Complete(pending, await device.SendNotificationReceiptsAsync(pending, default, scope));
            Assert.Null(inbox.Pending());
        }
        finally { release.TrySetResult(); if (!late.IsCompleted) try { await late; } catch (NpepException) { } }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BackgroundDeliveryRetriesSameReceiptsAndPauseResumeKeepsDismissal(bool malformed)
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
            if (Interlocked.Increment(ref uploads) == 1)
            {
                if (malformed) return Reply(request, new() { ["results"] = new JsonArray() });
                throw new HttpRequestException("Lost receipt response");
            }
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
        await Until(() => Volatile.Read(ref uploads) == 1 && ViewRuntime() is { CanPresent: true, Message: var message } && message.Contains("回执暂未上传"));
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

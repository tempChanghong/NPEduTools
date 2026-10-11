using System.Diagnostics;
using System.IO.Pipes;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using NPEduTools.App;
using NPEduTools.Contracts;

internal static partial class Program
{
    private static void RunNotificationValidityChecks()
    {
        var failures = new List<Exception>();
        foreach (string invalid in new[] { "request-id", "version", "zero-length", "empty-message", "disconnected" })
        foreach (bool expired in new[] { false, true })
        {
            try { RunNotificationValidityCheck(invalid, expired); }
            catch (Exception error) { failures.Add(new InvalidOperationException($"Notice validity {invalid}/expired={expired}: {error.Message}", error)); }
        }
        if (failures.Count > 0) throw new AggregateException(failures);
    }

    private static void RunNotificationValidityCheck(string invalid, bool expired)
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        string pipe = "NPEduTools.Test.notification-validity." + Guid.NewGuid().ToString("N");
        string scope = new('a', 64);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var server = new NamedPipeServerStream(pipe, PipeDirection.InOut, 1,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        var window = IsolatedMainWindow(pipe);
        var notice = new NpepNotice("fixture-notice", 1, "合成通知", "合成旧正文", "MINOR", DateTimeOffset.Now, null, "隔离来源", true);
        // Never show this window: its actual XAML fields can be invalidated without attention, backdrop or reservation.
        var presentation = new SchoolNotificationWindow(new(notice.Title, notice.Content, notice.Source, notice.PublishAt, SchoolNotificationPriority.Minor));
        var inbox = new NpepInboxState(scope, "ONLINE", "隔离有效列表", 1,
            [new(notice.PublicationId, notice.Revision, notice.Title, notice.Priority, true, false)], Current: notice, CanPresent: true);
        var requests = new List<HostRequest>();
        Task? poll = null;
        object? Field(string name) => typeof(MainWindow).GetField(name, flags)!.GetValue(window);
        void Set(string name, object value) => typeof(MainWindow).GetField(name, flags)!.SetValue(window, value);
        bool Reading() => (bool)Field("_notificationReading")!;
        async Task<HostRequest> Read(string action)
        {
            await server.WaitForConnectionAsync(timeout.Token);
            var request = await Protocol.ReadAsync<HostRequest>(server, timeout.Token); requests.Add(request);
            Assert(request.Capability == "npep.notifications" && request.Notification?.Action == action &&
                Protocol.Validate(request) is null && !Protocol.NoiseInterruption(request), "validity fixture sent an unexpected request");
            if (action == "get") Assert(request.Notification == new NpepNotificationCommand("get", scope, notice.PublicationId, notice.Revision),
                "body verification lost the shown scope or exact notice version");
            return request;
        }
        void Reply(HostRequest request)
        {
            var reply = Protocol.WriteAsync(server, new HostResponse(Protocol.Version, request.RequestId, "Succeeded", null,
                "隔离有效核对", Inbox: inbox), timeout.Token);
            PumpUntil(() => reply.IsCompleted, "valid list/body response did not finish"); reply.GetAwaiter().GetResult();
        }
        try
        {
            Exercise(window, owner =>
            {
                try
                {
                    Set("_schoolNotification", presentation); Set("_shownNotice", notice); Set("_shownScope", scope);
                    long verifiedAt = Stopwatch.GetTimestamp() - (long)((expired ? 61 : 20) * Stopwatch.Frequency);
                    Set("_lastNotificationRead", verifiedAt);
                    var read = Read("poll");
                    poll = (Task)typeof(MainWindow).GetMethod("NotificationPollAsync", flags)!.Invoke(window, null)!;
                    PumpUntil(() => read.IsCompleted, "list verification did not arrive"); Reply(read.GetAwaiter().GetResult());
                    server.Disconnect(); read = Read("get");
                    PumpUntil(() => read.IsCompleted, "shown notice body verification did not arrive");
                    var request = read.GetAwaiter().GetResult();
                    if (invalid == "disconnected") server.Disconnect();
                    else
                    {
                        Task reply = invalid switch
                        {
                            "zero-length" => server.WriteAsync(new byte[4], timeout.Token).AsTask(),
                            "empty-message" => Protocol.WriteAsync<HostResponse?>(server, null, timeout.Token),
                            _ => Protocol.WriteAsync(server, new HostResponse(invalid == "version" ? Protocol.Version + 1 : Protocol.Version,
                                invalid == "request-id" ? Guid.NewGuid() : request.RequestId, "Succeeded", null,
                                "隔离不可采信的正文核对", Inbox: inbox), timeout.Token)
                        };
                        PumpUntil(() => reply.IsCompleted, "invalid body response did not finish"); reply.GetAwaiter().GetResult();
                    }
                    PumpUntil(() => !Reading(), "body verification failure did not settle");
                    Assert((long)Field("_lastNotificationRead")! == verifiedAt,
                        "successful list polling incorrectly renewed the unverified notice body deadline");
                    Assert(presentation.Invalidated == expired, "body verification ignored the existing 60-second grace period");
                    var body = (TextBlock)presentation.FindName("NotificationBody");
                    Assert(expired ? !body.Text.Contains(notice.Content) && body.Text.Contains("无法核对") : body.Text == notice.Content,
                        "body was not hidden after expiry or was hidden within the existing grace period");
                    Assert(!presentation.DismissedByUser && presentation.Notification.Body == notice.Content &&
                        ReferenceEquals(Field("_shownNotice"), notice), "validity failure dismissed a notice or changed its stored presentation data");
                    if (server.IsConnected) server.Disconnect(); read = Read("poll");
                    PumpUntil(() => read.IsCompleted, "validity polling did not resume"); Reply(read.GetAwaiter().GetResult());
                    if (!expired)
                    {
                        server.Disconnect(); read = Read("get");
                        PumpUntil(() => read.IsCompleted, "fresh exact-version body check did not resume"); Reply(read.GetAwaiter().GetResult());
                    }
                    PumpUntil(() => !Reading(), "recovered validity check did not settle");
                    Assert((long)Field("_lastNotificationRead")! > verifiedAt && presentation.Invalidated == expired,
                        "valid recovery did not renew its deadline or silently restored invalidated body text");
                    Assert(body.Text.Contains(notice.Content) == !expired && !presentation.IsVisible &&
                        requests.Count == (expired ? 3 : 4) && requests.Select(r => r.RequestId).Distinct().Count() == requests.Count,
                        "recovery redisplayed stale content, opened a popup or sent extra receipts");
                    Checks.Add($"Notice validity {invalid}/{(expired ? "expired" : "within grace")}: list success does not renew failed body verification; valid exact-version recovery respects invalidation without receipts");
                }
                finally { window.Close(); presentation.Shutdown(); }
            });
        }
        finally
        {
            window.Close(); presentation.Shutdown();
            if (poll is not null)
            { PumpUntil(() => poll.IsCompleted, "isolated validity poll did not stop"); poll.GetAwaiter().GetResult(); }
        }
    }
}

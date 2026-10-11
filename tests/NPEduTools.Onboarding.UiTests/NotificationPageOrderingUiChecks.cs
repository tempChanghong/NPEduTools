using System.Diagnostics;
using System.IO.Pipes;
using System.Reflection;
using System.Windows.Controls;
using NPEduTools.App;
using NPEduTools.Contracts;

internal static partial class Program
{
    private static void RunNotificationPageOrderingChecks()
    {
        var failures = new List<Exception>();
        foreach (string response in new[] { "page", "empty-page", "missing-inbox", "version" })
        foreach (int previousClicks in new[] { 1, 2 })
        {
            try { RunNotificationPageOrderingCheck(response, previousClicks); }
            catch (Exception error) { failures.Add(new InvalidOperationException($"Page ordering {response}/previous={previousClicks}: {error.Message}", error)); }
        }
        if (failures.Count > 0) throw new AggregateException(failures);
    }

    private static void RunNotificationPageOrderingCheck(string response, int previousClicks)
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        string pipe = "NPEduTools.Test.notification-ordering." + Guid.NewGuid().ToString("N");
        string scope = new('a', 64);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        using var server = new NamedPipeServerStream(pipe, PipeDirection.InOut, 1,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        var window = IsolatedMainWindow(pipe);
        var notice = new NpepNotice("shown-notice", 1, "合成通知", "合成正文", "MINOR", DateTimeOffset.Now, null, "隔离来源", true);
        // Read actual body fields without showing a popup or acquiring its operation reservation.
        var presentation = new SchoolNotificationWindow(new(notice.Title, notice.Content, notice.Source, notice.PublishAt, SchoolNotificationPriority.Minor));
        var requests = new List<HostRequest>();
        Task? poll = null;
        object? Field(string name) => typeof(MainWindow).GetField(name, flags)!.GetValue(window);
        void Set(string name, object value) => typeof(MainWindow).GetField(name, flags)!.SetValue(window, value);
        bool Reading() => (bool)Field("_notificationReading")!;
        var items = (ItemsControl)window.FindName("NotificationInboxItems");
        var message = (TextBlock)window.FindName("NotificationInboxMessage");
        NpepInboxState Page(int count, int offset) => new(scope, "ONLINE", "隔离分页列表", count,
            Enumerable.Range(0, count).Skip(offset).Take(10).Select(i =>
                new NpepNoticeSummary("notice-" + i, 1, "合成通知 " + i, "MINOR", false, false)).ToArray(),
            NextOffset: offset + 10 < count ? offset + 10 : null);
        async Task<HostRequest> Read(string action = "poll")
        {
            await server.WaitForConnectionAsync(timeout.Token);
            var request = await Protocol.ReadAsync<HostRequest>(server, timeout.Token); requests.Add(request);
            Assert(request.Capability == "npep.notifications" && request.Notification?.Action == action && Protocol.Validate(request) is null,
                "page ordering fixture sent an unexpected request");
            return request;
        }
        void Reply(HostRequest request, NpepInboxState? state, bool wrongVersion = false)
        {
            var write = Protocol.WriteAsync(server, new HostResponse(wrongVersion ? Protocol.Version + 1 : Protocol.Version,
                request.RequestId, "Succeeded", null, "隔离分页回执", Inbox: state), timeout.Token);
            PumpUntil(() => write.IsCompleted, "page response did not finish"); write.GetAwaiter().GetResult();
        }
        try
        {
            Exercise(window, owner =>
            {
                try
                {
                    Set("_inboxOffset", 20);
                    var read = Read();
                    poll = (Task)typeof(MainWindow).GetMethod("NotificationPollAsync", flags)!.Invoke(window, null)!;
                    PumpUntil(() => read.IsCompleted, "initial third-page request did not arrive");
                    Reply(read.GetAwaiter().GetResult(), Page(35, 20));
                    PumpUntil(() => !Reading(), "initial third-page response did not settle");
                    Assert(items.Items.Count == 10 && Button(owner, "NotificationPrevious").IsEnabled, "initial third page did not render");
                    server.Disconnect(); read = Read();
                    PumpUntil(() => read.IsCompleted, "pending third-page request did not arrive");
                    var request = read.GetAwaiter().GetResult();
                    Assert(request.Notification!.Offset == 20, "fixture did not hold a third-page response");
                    for (int i = 0; i < previousClicks; i++) Click(owner, "NotificationPrevious");
                    int expectedOffset = 20 - previousClicks * 10;
                    Assert((int)Field("_inboxOffset")! == expectedOffset, "previous-page events did not select the requested page");
                    Set("_schoolNotification", presentation); Set("_shownNotice", notice); Set("_shownScope", scope);
                    long verifiedAt = Stopwatch.GetTimestamp() - 61 * Stopwatch.Frequency;
                    Set("_lastNotificationRead", verifiedAt);
                    bool valid = response is "page" or "empty-page";
                    Reply(request, response == "missing-inbox" ? null : Page(response == "empty-page" ? 0 : 35, 20), response == "version");
                    if (valid)
                    {
                        server.Disconnect(); read = Read("get");
                        PumpUntil(() => read.IsCompleted, "superseded list reply skipped shown-body verification");
                        var bodyRequest = read.GetAwaiter().GetResult();
                        Assert(bodyRequest.Notification == new NpepNotificationCommand("get", scope, notice.PublicationId, notice.Revision),
                            "shown-body verification lost exact identity during navigation");
                        Reply(bodyRequest, new(scope, "ONLINE", "隔离正文核对", 1, [], Current: notice, CanPresent: true));
                    }
                    PumpUntil(() => !Reading(), "superseded response did not settle");
                    Assert((int)Field("_inboxOffset")! == expectedOffset, "a superseded reply reset the newly selected page");
                    Assert(items.Items.Count == 0 && Field("_inboxScope") is null && Field("_inboxNext") is null &&
                        !Button(owner, "NotificationNext").IsEnabled && Button(owner, "NotificationPrevious").IsEnabled == (expectedOffset > 0),
                        "a superseded reply replaced the selected page with stale rows or navigation");
                    Assert(message.Text.Contains("正在读取"), "a superseded reply overwrote the new page loading status");
                    Assert(presentation.Invalidated == !valid && !presentation.IsVisible && !presentation.DismissedByUser,
                        "navigation bypassed body expiry or opened/dismissed a popup");
                    Assert(valid ? (long)Field("_lastNotificationRead")! > verifiedAt : (long)Field("_lastNotificationRead")! == verifiedAt,
                        "body verification deadline did not follow the actual verification result");
                    if (server.IsConnected) server.Disconnect(); read = Read();
                    PumpUntil(() => read.IsCompleted, "newly selected page request did not arrive"); request = read.GetAwaiter().GetResult();
                    Assert(request.Notification!.Offset == expectedOffset, "next poll ignored the user's selected page");
                    Reply(request, Page(35, expectedOffset));
                    if (valid)
                    {
                        server.Disconnect(); read = Read("get");
                        PumpUntil(() => read.IsCompleted, "fresh-page body verification did not arrive");
                        Reply(read.GetAwaiter().GetResult(), new(scope, "ONLINE", "隔离正文核对", 1, [], Current: notice, CanPresent: true));
                    }
                    PumpUntil(() => !Reading(), "fresh selected page did not settle");
                    Assert(items.Items.Count == 10 && ((NpepNoticeSummary)items.Items[0]).PublicationId == "notice-" + expectedOffset &&
                        Field("_inboxScope") as string == scope && Button(owner, "NotificationNext").IsEnabled &&
                        Button(owner, "NotificationPrevious").IsEnabled == (expectedOffset > 0), "fresh response did not render the selected page");
                    Assert(requests.Count == (valid ? 5 : 3) && requests.Select(r => r.RequestId).Distinct().Count() == requests.Count,
                        "navigation repeated requests or sent receipts");
                    Checks.Add($"Page ordering {response}/previous={previousClicks}: late replies preserve page selection and loading state; shown-body verification remains active; the next poll restores the selected rows");
                }
                finally { window.Close(); presentation.Shutdown(); }
            });
        }
        finally
        {
            window.Close(); presentation.Shutdown();
            if (poll is not null) { PumpUntil(() => poll.IsCompleted, "page ordering poll did not stop"); poll.GetAwaiter().GetResult(); }
        }
    }
}

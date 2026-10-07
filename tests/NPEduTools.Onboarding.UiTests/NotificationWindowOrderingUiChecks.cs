using System.Diagnostics;
using System.IO.Pipes;
using System.Reflection;
using System.Windows.Controls;
using NPEduTools.App;
using NPEduTools.Contracts;

internal static partial class Program
{
    private static void RunNotificationWindowOrderingChecks()
    {
        var failures = new List<Exception>();
        foreach (string replacement in new[] { "same-notice", "new-notice" })
        foreach (string response in new[] { "verified", "withdrawn", "version", "disconnected" })
        {
            try { RunNotificationWindowOrderingCheck(replacement, response); }
            catch (Exception error) { failures.Add(new InvalidOperationException($"Notice window {replacement}/{response}: {error.Message}", error)); }
        }
        foreach (string response in new[] { "verified", "withdrawn" })
        {
            try { RunNotificationWindowOrderingCheck("unchanged", response); }
            catch (Exception error) { failures.Add(new InvalidOperationException($"Notice window unchanged/{response}: {error.Message}", error)); }
        }
        if (failures.Count > 0) throw new AggregateException(failures);
    }

    private static void RunNotificationWindowOrderingCheck(string replacement, string response)
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        string pipe = "NPEduTools.Test.notification-window." + Guid.NewGuid().ToString("N");
        string oldScope = new('a', 64), freshScope = replacement == "new-notice" ? new('b', 64) : oldScope;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var server = new NamedPipeServerStream(pipe, PipeDirection.InOut, 1,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        var owner = IsolatedMainWindow(pipe);
        var oldNotice = new NpepNotice("old-notice", 1, "隔离旧通知", "隔离旧正文", "MINOR", DateTimeOffset.Now, null, "隔离来源", true);
        var freshNotice = replacement == "new-notice" ? oldNotice with { PublicationId = "fresh-notice", Revision = 2, Content = "隔离新正文" } : oldNotice;
        // Neither presentation is shown: exercise real invalidation fields without attention or reservations.
        var oldWindow = new SchoolNotificationWindow(new(oldNotice.Title, oldNotice.Content, oldNotice.Source, oldNotice.PublishAt, SchoolNotificationPriority.Minor));
        var freshWindow = new SchoolNotificationWindow(new(freshNotice.Title, freshNotice.Content, freshNotice.Source, freshNotice.PublishAt, SchoolNotificationPriority.Minor));
        Task? poll = null;
        var requests = new List<HostRequest>();
        object? Field(string name) => typeof(MainWindow).GetField(name, flags)!.GetValue(owner);
        void Set(string name, object value) => typeof(MainWindow).GetField(name, flags)!.SetValue(owner, value);
        bool Reading() => (bool)Field("_notificationReading")!;
        var items = (ItemsControl)owner.FindName("NotificationInboxItems");
        var message = (TextBlock)owner.FindName("NotificationInboxMessage");
        NpepInboxState List(string scope, NpepNotice notice) => new(scope, "ONLINE", "隔离通知列表", 1,
            [new(notice.PublicationId, notice.Revision, notice.Title, notice.Priority, false, false)]);
        NpepInboxState Body(string scope, NpepNotice notice) => new(scope, "ONLINE", "隔离有效正文", 1, [], Current: notice, CanPresent: true);
        async Task<HostRequest> Read(string action, string? scope = null, NpepNotice? notice = null)
        {
            await server.WaitForConnectionAsync(timeout.Token);
            var request = await Protocol.ReadAsync<HostRequest>(server, timeout.Token); requests.Add(request);
            Assert(request.Capability == "npep.notifications" && request.Notification?.Action == action && Protocol.Validate(request) is null,
                "window ordering fixture sent an unexpected request");
            if (action == "get") Assert(request.Notification == new NpepNotificationCommand("get", scope, notice!.PublicationId, notice.Revision),
                "body query did not use the targeted window's exact identity");
            return request;
        }
        void Reply(HostRequest request, NpepInboxState state, bool wrongVersion = false)
        {
            var write = Protocol.WriteAsync(server, new HostResponse(wrongVersion ? Protocol.Version + 1 : Protocol.Version,
                request.RequestId, "Succeeded", null, "隔离通知回执", Inbox: state), timeout.Token);
            PumpUntil(() => write.IsCompleted, "window ordering response did not finish"); write.GetAwaiter().GetResult();
        }
        try
        {
            Exercise(owner, window =>
            {
                try
                {
                    Set("_schoolNotification", oldWindow); Set("_shownNotice", oldNotice); Set("_shownScope", oldScope);
                    long verifiedAt = Stopwatch.GetTimestamp() - 61 * Stopwatch.Frequency;
                    Set("_lastNotificationRead", verifiedAt);
                    var read = Read("poll");
                    poll = (Task)typeof(MainWindow).GetMethod("NotificationPollAsync", flags)!.Invoke(owner, null)!;
                    PumpUntil(() => read.IsCompleted, "initial notification list query did not arrive"); Reply(read.GetAwaiter().GetResult(), List(oldScope, oldNotice));
                    server.Disconnect(); read = Read("get", oldScope, oldNotice);
                    PumpUntil(() => read.IsCompleted, "old window body query did not arrive"); var request = read.GetAwaiter().GetResult();
                    bool replaced = replacement != "unchanged";
                    if (replaced)
                    {
                        oldWindow.Shutdown();
                        Set("_schoolNotification", freshWindow); Set("_shownNotice", freshNotice); Set("_shownScope", freshScope);
                    }
                    string currentMessage = message.Text;
                    var currentItems = items.ItemsSource;
                    if (response == "disconnected") server.Disconnect();
                    else Reply(request, response == "withdrawn" ? List(oldScope, oldNotice) : Body(oldScope, oldNotice), response == "version");
                    PumpUntil(() => !Reading(), "old window verification did not settle");
                    var target = replaced ? freshWindow : oldWindow;
                    Assert(target.Invalidated == (!replaced && response == "withdrawn"), "an old window's verification invalidated the replacement window");
                    Assert(replaced ? (long)Field("_lastNotificationRead")! == verifiedAt : (long)Field("_lastNotificationRead")! > verifiedAt,
                        "an old window's reply renewed the replacement window's verification deadline");
                    Assert(message.Text == currentMessage && ReferenceEquals(items.ItemsSource, currentItems) && Field("_inboxScope") as string == oldScope,
                        "an obsolete body failure cleared the current list or replaced its message");
                    Assert(ReferenceEquals(Field("_schoolNotification"), target) && ReferenceEquals(Field("_shownNotice"), replaced ? freshNotice : oldNotice) &&
                        Field("_shownScope") as string == (replaced ? freshScope : oldScope), "old verification changed the current presentation identity");
                    if (server.IsConnected) server.Disconnect(); read = Read("poll");
                    PumpUntil(() => read.IsCompleted, "notification polling did not resume"); Reply(read.GetAwaiter().GetResult(), List(freshScope, freshNotice));
                    bool checkBody = replaced || response == "verified";
                    if (checkBody)
                    {
                        server.Disconnect(); read = Read("get", freshScope, freshNotice);
                        PumpUntil(() => read.IsCompleted, "current window body verification did not resume"); Reply(read.GetAwaiter().GetResult(), Body(freshScope, freshNotice));
                    }
                    PumpUntil(() => !Reading(), "fresh window body response did not settle");
                    Assert((long)Field("_lastNotificationRead")! > verifiedAt && target.Invalidated == (!replaced && response == "withdrawn"),
                        "fresh body verification did not recover or restored an invalidated notice");
                    Assert(!oldWindow.IsVisible && !freshWindow.IsVisible && !target.DismissedByUser && requests.Count == (checkBody ? 4 : 3) &&
                        requests.Select(r => r.RequestId).Distinct().Count() == requests.Count, "fixture showed a popup, dismissed it or sent extra receipts");
                    Checks.Add($"Notice window {replacement}/{response}: old body replies affect only their target window; replacement freshness requires its own verification; current valid/withdrawn replies still apply without receipts");
                }
                finally { owner.Close(); oldWindow.Shutdown(); freshWindow.Shutdown(); }
            });
        }
        finally
        {
            owner.Close(); oldWindow.Shutdown(); freshWindow.Shutdown();
            if (poll is not null) { PumpUntil(() => poll.IsCompleted, "notification window poll did not stop"); poll.GetAwaiter().GetResult(); }
        }
    }
}

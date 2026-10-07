using System.IO.Pipes;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using NPEduTools.App;
using NPEduTools.Contracts;

internal static partial class Program
{
    private static void RunNotificationPaginationChecks()
    {
        var failures = new List<Exception>();
        foreach (int total in new[] { 0, 3, 20, 25 })
        {
            try { RunNotificationPaginationCheck(total); }
            catch (Exception error) { failures.Add(new InvalidOperationException($"Notification page total={total}: {error.Message}", error)); }
        }
        if (failures.Count > 0) throw new AggregateException(failures);
    }

    private static void RunNotificationPaginationCheck(int total)
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        string pipe = "NPEduTools.Test.notification-page." + Guid.NewGuid().ToString("N");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var server = new NamedPipeServerStream(pipe, PipeDirection.InOut, 1,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        var window = IsolatedMainWindow(pipe);
        window.Width = 960; window.Height = 780;
        Task? poll = null;
        var requests = new List<HostRequest>();
        int Offset() => (int)typeof(MainWindow).GetField("_inboxOffset", flags)!.GetValue(window)!;
        bool Reading() => (bool)typeof(MainWindow).GetField("_notificationReading", flags)!.GetValue(window)!;
        NpepInboxState Page(int count, int offset) => new(new string('a', 64), "ONLINE", "隔离有效列表", count,
            Enumerable.Range(0, count).Skip(offset).Take(10).Select(i =>
                new NpepNoticeSummary("notice-" + i, 1, "合成通知 " + i, "MINOR", false, false)).ToArray(),
            NextOffset: offset + 10 < count ? offset + 10 : null);
        async Task<HostRequest> Read()
        {
            await server.WaitForConnectionAsync(timeout.Token);
            var request = await Protocol.ReadAsync<HostRequest>(server, timeout.Token); requests.Add(request);
            Assert(request.Capability == "npep.notifications" && request.Notification is { Action: "poll" } &&
                Protocol.Validate(request) is null, "pagination fixture sent an unexpected command");
            return request;
        }
        void Reply(HostRequest request, NpepInboxState page)
        {
            var reply = Protocol.WriteAsync(server, new HostResponse(Protocol.Version, request.RequestId,
                "Succeeded", null, "隔离分页回执", Inbox: page), timeout.Token);
            PumpUntil(() => reply.IsCompleted && !Reading(), "pagination reply did not settle"); reply.GetAwaiter().GetResult();
        }
        try
        {
            Exercise(window, owner =>
            {
                try
                {
                    ((FrameworkElement)owner.FindName("HomePage")).Visibility = Visibility.Collapsed;
                    ((FrameworkElement)owner.FindName("SettingsPage")).Visibility = Visibility.Visible;
                    var panel = (StackPanel)owner.FindName("NpepSettingsPanel"); panel.Visibility = Visibility.Visible;
                    foreach (FrameworkElement child in panel.Children.Cast<FrameworkElement>().Take(3)) child.Visibility = Visibility.Collapsed;
                    ((FrameworkElement)owner.FindName("GeneralSettingsPanel")).Visibility = Visibility.Collapsed;
                    typeof(MainWindow).GetField("_inboxOffset", flags)!.SetValue(window, 10);
                    var read = Read();
                    poll = (Task)typeof(MainWindow).GetMethod("NotificationPollAsync", flags)!.Invoke(window, null)!;
                    PumpUntil(() => read.IsCompleted, "initial page did not arrive");
                    var request = read.GetAwaiter().GetResult(); Assert(request.Notification!.Offset == 10, "unexpected initial page");
                    Reply(request, Page(35, 10));
                    Click(owner, "NotificationNext"); Assert(Offset() == 20, "next-page event did not navigate");
                    server.Disconnect(); read = Read();
                    PumpUntil(() => read.IsCompleted, "next-page query did not arrive"); request = read.GetAwaiter().GetResult();
                    Assert(request.Notification!.Offset == 20, "unexpected next-page request"); Reply(request, Page(total, 20));
                    int expectedOffset = total <= 20 ? 0 : 20;
                    Assert(Offset() == expectedOffset, "shrunk list did not reset an out-of-range page");
                    Assert(Button(owner, "NotificationPrevious").IsEnabled == (expectedOffset > 0) && !Button(owner, "NotificationNext").IsEnabled,
                        "pagination controls disagree with the reset page offset");
                    Assert(((ItemsControl)owner.FindName("NotificationInboxItems")).Items.Count == Math.Max(0, total - 20),
                        "a page outside the new list retained old rows");
                    if (total == 0) Snapshot(owner, "notification-empty-page-reset.png");
                    server.Disconnect(); read = Read();
                    PumpUntil(() => read.IsCompleted, "pagination poll did not resume"); request = read.GetAwaiter().GetResult();
                    Assert(request.Notification!.Offset == expectedOffset, "subsequent query ignored the corrected page offset");
                    Reply(request, Page(total, expectedOffset));
                    Assert(((ItemsControl)owner.FindName("NotificationInboxItems")).Items.Count == Math.Min(10, Math.Max(0, total - expectedOffset)) &&
                        Button(owner, "NotificationPrevious").IsEnabled == (expectedOffset > 0) &&
                        Button(owner, "NotificationNext").IsEnabled == (expectedOffset + 10 < total),
                        "fresh page did not restore the correct rows and navigation");
                    if (total == 20) Snapshot(owner, "notification-first-page-recovered.png");
                    Assert(requests.Count == 3 && requests.Select(r => r.RequestId).Distinct().Count() == 3,
                        "pagination repeated a request or sent a receipt");
                    Checks.Add($"Notification total={total}: out-of-range reset and navigation agree immediately; the next poll uses the corrected offset; valid later pages remain accessible");
                }
                finally { window.Close(); }
            });
        }
        finally
        {
            window.Close();
            if (poll is not null) { PumpUntil(() => poll.IsCompleted, "pagination poll did not stop"); poll.GetAwaiter().GetResult(); }
        }
    }
}

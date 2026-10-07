using System.IO.Pipes;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using NPEduTools.App;
using NPEduTools.Contracts;

internal static partial class Program
{
    private static void RunNotificationInboxChecks()
    {
        var previous = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Application.Current.Dispatcher));
        try
        {
            var failures = new List<Exception>();
            foreach (string invalid in new[] { "request-id", "version", "zero-length", "empty-message", "disconnected", "missing-inbox" })
            foreach (bool empty in new[] { false, true })
            {
                try { RunNotificationInboxCheck(invalid, empty); }
                catch (Exception error) { failures.Add(new InvalidOperationException($"Inbox {invalid}/empty={empty}: {error.Message}", error)); }
            }
            if (failures.Count > 0) throw new AggregateException(failures);
        }
        finally { SynchronizationContext.SetSynchronizationContext(previous); }
    }

    private static void RunNotificationInboxCheck(string invalid, bool empty)
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        string pipe = "NPEduTools.Test.notification-inbox." + Guid.NewGuid().ToString("N");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var server = new NamedPipeServerStream(pipe, PipeDirection.InOut, 1,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        var window = IsolatedMainWindow(pipe);
        window.Width = 960; window.Height = 780;
        var requests = new List<HostRequest>();
        var initial = new NpepInboxState(new string('a', 64), "ONLINE", "隔离已核实列表", 25,
            [new("old-notice", 1, "隔离旧通知", "NORMAL", false, false)], NextOffset: 20);
        var fresh = new NpepInboxState(new string('b', 64), "ONLINE", "隔离最新列表", empty ? 0 : 12,
            empty ? [] : [new("fresh-notice", 2, "隔离新通知", "MINOR", false, false)], NextOffset: empty ? null : 10);
        Task? poll = null;
        bool Reading() => (bool)typeof(MainWindow).GetField("_notificationReading", flags)!.GetValue(window)!;
        object? Field(string name) => typeof(MainWindow).GetField(name, flags)!.GetValue(window);
        string Message() => ((TextBlock)window.FindName("NotificationInboxMessage")).Text;
        var items = (ItemsControl)window.FindName("NotificationInboxItems");
        async Task<HostRequest> Read()
        {
            await server.WaitForConnectionAsync(timeout.Token);
            var request = await Protocol.ReadAsync<HostRequest>(server, timeout.Token);
            requests.Add(request);
            Assert(request.Capability == "npep.notifications" && request.Notification is { Action: "poll" } &&
                Protocol.Validate(request) is null && !Protocol.NoiseInterruption(request), "fixture sent a popup, receipt or mutation request");
            return request;
        }
        void Reply(HostRequest request, NpepInboxState state)
        {
            var response = Protocol.WriteAsync(server, new HostResponse(Protocol.Version, request.RequestId,
                "Succeeded", null, "隔离有效回执", Inbox: state), timeout.Token);
            PumpUntil(() => response.IsCompleted && !Reading(), "valid inbox did not settle"); response.GetAwaiter().GetResult();
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
                    // Keep only the real notification card in this fixture.
                    foreach (FrameworkElement child in panel.Children.Cast<FrameworkElement>().Take(3)) child.Visibility = Visibility.Collapsed;
                    ((FrameworkElement)owner.FindName("GeneralSettingsPanel")).Visibility = Visibility.Collapsed;
                    typeof(MainWindow).GetField("_inboxOffset", flags)!.SetValue(window, 10);
                    var read = Read();
                    poll = (Task)typeof(MainWindow).GetMethod("NotificationPollAsync", flags)!.Invoke(window, null)!;
                    PumpUntil(() => read.IsCompleted, "initial inbox query did not arrive");
                    var request = read.GetAwaiter().GetResult();
                    Assert(request.Notification!.Offset == 10, "initial page offset was not preserved");
                    Reply(request, initial);
                    Assert(items.Items.Count == 1 && Button(owner, "NotificationNext").IsEnabled && Button(owner, "NotificationPrevious").IsEnabled,
                        "known paginated inbox did not render");
                    Click(owner, "NotificationNext");
                    Assert((int)Field("_inboxOffset")! == 20, "next-page event did not update the offset");
                    server.Disconnect(); read = Read();
                    PumpUntil(() => read.IsCompleted, "next page query did not arrive"); request = read.GetAwaiter().GetResult();
                    Assert(request.Notification!.Offset == 20, "next page query targeted the wrong offset");
                    if (invalid == "disconnected") server.Disconnect();
                    else
                    {
                        Task response = invalid switch
                        {
                            "zero-length" => server.WriteAsync(new byte[4], timeout.Token).AsTask(),
                            "empty-message" => Protocol.WriteAsync<HostResponse?>(server, null, timeout.Token),
                            _ => Protocol.WriteAsync(server, new HostResponse(invalid == "version" ? Protocol.Version + 1 : Protocol.Version,
                                invalid == "request-id" ? Guid.NewGuid() : request.RequestId, "Succeeded", null,
                                "隔离异常回执", Inbox: invalid == "missing-inbox" ? null : fresh), timeout.Token)
                        };
                        PumpUntil(() => response.IsCompleted, "invalid inbox reply did not finish"); response.GetAwaiter().GetResult();
                    }
                    PumpUntil(() => !Reading(), "failed inbox read did not release its gate");
                    Assert(items.Items.Count == 0 && Field("_inboxScope") is null && Field("_inboxNext") is null,
                        "failed inbox query retained stale rows, scope or next-page marker");
                    Assert((int)Field("_inboxOffset")! == 0 && !Button(owner, "NotificationNext").IsEnabled && !Button(owner, "NotificationPrevious").IsEnabled,
                        "failed inbox retained actionable pagination");
                    Assert(Message().Contains(invalid == "missing-inbox" ? "更新" : "暂不可用") && !Message().Contains("当前有效通知"),
                        "unavailable inbox was presented as a current or empty snapshot");
                    if (invalid == "request-id" && !empty) Snapshot(owner, "notification-inbox-unconfirmed.png");
                    if (server.IsConnected) server.Disconnect();
                    read = Read();
                    PumpUntil(() => read.IsCompleted, "the same inbox poll did not recover"); request = read.GetAwaiter().GetResult();
                    Assert(request.Notification!.Offset == 0, "recovery did not reread the first page");
                    Reply(request, fresh);
                    Assert(items.Items.Count == (empty ? 0 : 1) && Field("_inboxScope") as string == fresh.Scope &&
                        Message().Contains($"当前有效通知 {fresh.Total} 条") && !Button(owner, "NotificationPrevious").IsEnabled &&
                        Button(owner, "NotificationNext").IsEnabled == !empty, "fresh or genuinely empty inbox did not restore the correct list and paging");
                    if (!empty) Assert(((NpepNoticeSummary)items.Items[0]).PublicationId == "fresh-notice", "old rows survived fresh scope recovery");
                    Assert(Field("_schoolNotification") is null && Field("_shownNotice") is null && requests.Count == 3 &&
                        requests.Select(r => r.RequestId).Distinct().Count() == 3, "fixture opened a popup or resent a receipt");
                    if (invalid == "request-id" && !empty) Snapshot(owner, "notification-inbox-recovered.png");
                    Checks.Add($"Inbox {invalid}/{(empty ? "empty" : "fresh scope")}: unknown clears rows and paging; the existing poll rereads page zero without opening a popup or marking receipts");
                }
                finally { window.Close(); }
            });
        }
        finally
        {
            window.Close();
            if (poll is not null)
            { PumpUntil(() => poll.IsCompleted, "isolated inbox poll did not stop"); poll.GetAwaiter().GetResult(); }
        }
    }
}

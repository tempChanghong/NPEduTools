using System.IO.Pipes;
using System.Reflection;
using System.Windows.Controls;
using NPEduTools.App;
using NPEduTools.Contracts;

internal static partial class Program
{
    private static void RunNotificationOpenOrderingChecks()
    {
        var failures = new List<Exception>();
        foreach (string change in new[] { "page", "scope", "unchanged" })
        foreach (string response in new[] { "not-presentable", "version", "disconnected" })
        {
            try { RunNotificationOpenOrderingCheck(change, response); }
            catch (Exception error) { failures.Add(new InvalidOperationException($"Notice open {change}/{response}: {error.Message}", error)); }
        }
        if (failures.Count > 0) throw new AggregateException(failures);
    }

    private static void RunNotificationOpenOrderingCheck(string change, string response)
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        string pipe = "NPEduTools.Test.notification-open." + Guid.NewGuid().ToString("N");
        string oldScope = new('a', 64), newScope = new('b', 64);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var manualServer = new NamedPipeServerStream(pipe, PipeDirection.InOut, 2,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        using var pollServer = new NamedPipeServerStream(pipe, PipeDirection.InOut, 2,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        var window = IsolatedMainWindow(pipe);
        Task? poll = null, opening = null;
        var notice = new NpepNoticeSummary("old-notice", 1, "隔离旧通知", "MINOR", false, false);
        var items = (ItemsControl)window.FindName("NotificationInboxItems");
        var message = (TextBlock)window.FindName("NotificationInboxMessage");
        object? Field(string name) => typeof(MainWindow).GetField(name, flags)!.GetValue(window);
        void Set(string name, object value) => typeof(MainWindow).GetField(name, flags)!.SetValue(window, value);
        async Task<HostRequest> Read(NamedPipeServerStream server)
        {
            await server.WaitForConnectionAsync(timeout.Token);
            return await Protocol.ReadAsync<HostRequest>(server, timeout.Token);
        }
        void Write(NamedPipeServerStream server, HostRequest request, NpepInboxState? state, bool wrongVersion = false)
        {
            var write = Protocol.WriteAsync(server, new HostResponse(wrongVersion ? Protocol.Version + 1 : Protocol.Version,
                request.RequestId, "Succeeded", null, "隔离正文回执", Inbox: state), timeout.Token);
            PumpUntil(() => write.IsCompleted, "manual notice response did not finish"); write.GetAwaiter().GetResult();
        }
        try
        {
            Exercise(window, owner =>
            {
                try
                {
                    Set("_inboxScope", oldScope); Set("_inboxOffset", 10); Set("_inboxNext", 20);
                    items.ItemsSource = new[] { notice }; message.Text = "隔离原列表";
                    Button(owner, "NotificationPrevious").IsEnabled = Button(owner, "NotificationNext").IsEnabled = true;
                    var read = Read(manualServer);
                    opening = (Task)typeof(MainWindow).GetMethod("OpenSchoolNotificationAsync", flags)!.Invoke(window, [new Button { Tag = notice }])!;
                    PumpUntil(() => read.IsCompleted, "manual exact-version get did not arrive");
                    var request = read.GetAwaiter().GetResult();
                    Assert(request.Notification == new NpepNotificationCommand("get", oldScope, notice.PublicationId, notice.Revision) &&
                        Protocol.Validate(request) is null, "manual query did not retain the clicked notice's scope and version");
                    if (change == "page") Click(owner, "NotificationPrevious");
                    if (change == "scope")
                    {
                        var reading = Read(pollServer);
                        poll = (Task)typeof(MainWindow).GetMethod("NotificationPollAsync", flags)!.Invoke(window, null)!;
                        PumpUntil(() => reading.IsCompleted, "parallel fresh-scope poll did not arrive");
                        var pollRequest = reading.GetAwaiter().GetResult();
                        Assert(pollRequest.Notification is { Action: "poll", Offset: 10 } && Protocol.Validate(pollRequest) is null,
                            "parallel request was not a read-only poll");
                        Write(pollServer, pollRequest, new(newScope, "ONLINE", "隔离新归属列表", 12,
                            [new("fresh-notice", 2, "隔离新通知", "MINOR", false, false)]));
                        PumpUntil(() => !(bool)Field("_notificationReading")!, "fresh-scope reply did not settle");
                    }
                    string currentMessage = message.Text;
                    var currentItems = items.ItemsSource;
                    int currentOffset = (int)Field("_inboxOffset")!;
                    string? currentScope = Field("_inboxScope") as string;
                    if (response == "disconnected") manualServer.Disconnect();
                    else Write(manualServer, request, new(oldScope, "ONLINE", "隔离无法打开的旧通知", 0, []), response == "version");
                    PumpUntil(() => opening.IsCompleted, "manual open did not settle"); opening.GetAwaiter().GetResult();
                    Assert(change == "unchanged"
                        ? message.Text.Contains(response == "not-presentable" ? "通知已变化" : "无法打开")
                        : message.Text == currentMessage, "an obsolete manual open overwrote the latest list/loading message");
                    Assert(ReferenceEquals(items.ItemsSource, currentItems) && (int)Field("_inboxOffset")! == currentOffset &&
                        Field("_inboxScope") as string == currentScope, "manual failure changed current rows, page or scope");
                    Assert(Field("_schoolNotification") is null && Field("_shownNotice") is null && !(bool)Field("_notificationOpening")!,
                        "non-presentable reply opened a popup or retained its opening gate");
                    Assert(!opening.IsFaulted && !opening.IsCanceled, "manual failure escaped the event handler");
                    Checks.Add($"Notice open {change}/{response}: exact clicked identity is queried; obsolete results leave the latest page intact; current failures remain visible without opening a popup");
                }
                finally { window.Close(); }
            });
        }
        finally
        {
            window.Close();
            if (opening is not null) { PumpUntil(() => opening.IsCompleted, "manual open did not stop"); opening.GetAwaiter().GetResult(); }
            if (poll is not null) { PumpUntil(() => poll.IsCompleted, "parallel notice poll did not stop"); poll.GetAwaiter().GetResult(); }
        }
    }
}

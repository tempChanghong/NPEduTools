using System.IO.Pipes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using NPEduTools.App;
using NPEduTools.Contracts;

internal static partial class Program
{
    private static void RunNotificationEntryOrdering(bool previewFirst, bool stale, bool leaveInbox = false)
    {
        string pipe = "NPEduTools.Test.notice-entry." + Guid.NewGuid().ToString("N");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var main = IsolatedMainWindow(pipe);
        var notice = new NpepNotice("round4-notice", 9, "下一节课请到科学实验室", "先在门口集合，清点人数后入场。", "NORMAL",
            DateTimeOffset.Now, null, "学校教务处", true);
        var summary = new NpepNoticeSummary(notice.PublicationId, notice.Revision, notice.Title, notice.Priority, true, false);
        var inbox = new NpepInboxState(NoticeScope, "ONLINE", "通知已同步。", 1, [summary], Current: notice, CanPresent: true);
        Task? poll = null;
        NoticeExchange Listen() => new(pipe, timeout.Token);
        NpepNotificationCommand ForNotice(string action) => new(action, NoticeScope, notice.PublicationId, notice.Revision);
        SchoolNotificationWindow? Preview() => NoticeField<SchoolNotificationWindow>(main, "_notificationPreview");
        SchoolNotificationWindow? Delivery() => NoticeField<SchoolNotificationWindow>(main, "_schoolNotification");
        bool Opening() => NoticeField<bool>(main, "_notificationOpening");
        void CheckPreviewAlone()
        {
            Assert(Preview() is { IsVisible: true } preview && preview.Backdrops.Count == DesktopScreenCount() &&
                Delivery() is null && VisibleNotices() == 1, "preview must be the only notification and own its mask group");
        }
        void ClosePreview()
        {
            var preview = Preview()!;
            Click(preview, "DismissButton");
            PumpUntil(() => !preview.IsVisible && Preview() is null && preview.Backdrops.Count == 0,
                "preview close did not release its window and mask group");
        }

        try
        {
            Exercise(main, _ =>
            {
                try
                {
                    ShowNotificationSettings(main);
                    ApplyNoticePage(main, inbox);
                    var message = (TextBlock)main.FindName("NotificationPreviewMessage");
                    using var initial = Listen();
                    if (previewFirst)
                    {
                        Click(main, "NotificationPreviewButton");
                        CheckPreviewAlone();
                        poll = (Task)typeof(MainWindow).GetMethod("NotificationPollAsync", NoticeMembers)!.Invoke(main, null)!;
                        initial.Expect(new("poll"));
                        initial.Reply(inbox);
                        using var continuing = Listen();
                        continuing.Expect(new("poll"));
                        CheckPreviewAlone();
                        ClosePreview();
                        Assert(VisibleNotices() == 0, "closing preview left a notification visible before fresh verification");
                        using var final = Listen();
                        continuing.Reply(inbox);
                        final.Expect(ForNotice("get"));
                        using var displayed = Listen();
                        final.Reply(inbox);
                        displayed.Expect(ForNotice("displayed"));
                        Assert(Delivery() is { IsVisible: true } delivery && delivery.Backdrops.Count == DesktopScreenCount() &&
                            Preview() is null && VisibleNotices() == 1, "queued delivery did not become the sole verified notification");
                        displayed.Reply(inbox);
                        Checks.Add("Preview first: real Preview button, polling continues with no preview receipts, mask group releases, automatic candidate rechecks and sends DISPLAYED");
                        return;
                    }

                    NoticeRow(main, 0).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                    initial.Expect(ForNotice("get"));
                    if (leaveInbox)
                    {
                        Click(main, "GeneralSettingsTab");
                        ShowNotificationSettings(main);
                        initial.Reply(inbox);
                        PumpUntil(() => !Opening(), "leaving the inbox did not release presentation");
                        Assert(VisibleNotices() == 0 && Delivery() is null, "returning to settings revived a previously abandoned detail");
                        Checks.Add("Leaving and reopening the inbox while checking a clicked notice discards the late reply and opens no window");
                        return;
                    }
                    NoticeRow(main, 0).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                    Click(main, "NotificationPreviewButton");
                    Assert(Opening() && VisibleNotices() == 0 && Preview() is null && message.Text.Contains("正在打开学校通知"),
                        "preview took over presentation while the manual GET was held");

                    if (stale)
                    {
                        initial.Reply(inbox with { Current = notice with { Revision = notice.Revision + 1 } });
                        PumpUntil(() => !Opening(), "stale version did not release presentation");
                        Assert(VisibleNotices() == 0 && message.Text.Length == 0, "stale opened detail or retained its wait hint");
                        Click(main, "NotificationPreviewButton");
                        CheckPreviewAlone(); ClosePreview();
                        Checks.Add("Manual open first: stale final revision never renders and releases presentation for the real Preview button");
                        return;
                    }

                    using var receipt = Listen();
                    initial.Reply(inbox);
                    receipt.Expect(ForNotice("displayed"));
                    var shown = Delivery()!;
                    typeof(MainWindow).GetMethod("InvalidateUnverifiedNotification", NoticeMembers)!.Invoke(main, null);
                    Assert(!shown.Invalidated, "a newly verified manual notice inherited an expired freshness deadline");
                    Assert(shown.IsVisible && shown.Backdrops.Count == DesktopScreenCount() && VisibleNotices() == 1 &&
                        Preview() is null && message.Text.Length == 0, "manual verification did not show only the real detail");
                    Click(main, "NotificationPreviewButton");
                    Assert(Delivery() == shown && VisibleNotices() == 1, "a second click opened preview on top of the live detail");

                    using var firstClose = Listen();
                    Click(shown, "DismissButton");
                    receipt.Reply(inbox);
                    firstClose.Expect(ForNotice("dismissed"));
                    firstClose.Reply(null, "Failed");
                    PumpUntil(() => Equals(Button(shown, "DismissButton").Content, "重试关闭"), "failed close did not show retry");
                    Assert(shown.IsVisible && shown.Backdrops.Count == DesktopScreenCount(), "failed close removed the window or its masks");
                    using var retry = Listen();
                    Click(shown, "DismissButton");
                    retry.Expect(ForNotice("dismissed"));
                    retry.Reply(inbox);
                    PumpUntil(() => !shown.IsVisible && shown.DismissedByUser && shown.Backdrops.Count == 0 && VisibleNotices() == 0,
                        "successful retry did not release the real detail");
                    Checks.Add("Manual open first: real inbox row reserves during exact-version GET, Preview explains contention, one window/mask group, DISPLAYED coalesces with close, failed DISMISSED retries");
                }
                finally { main.Close(); }
            });
        }
        finally
        {
            main.Close();
            if (poll is not null)
            {
                PumpUntil(() => poll.IsCompleted, "isolated notification polling did not stop");
                poll.GetAwaiter().GetResult();
            }
        }
    }

    private sealed class NoticeExchange : IDisposable
    {
        private readonly NamedPipeServerStream _server;
        private readonly CancellationToken _token;
        private readonly Task<HostRequest> _read;
        private HostRequest? _request;

        internal NoticeExchange(string pipe, CancellationToken token)
        {
            _server = new(pipe, PipeDirection.InOut, 8, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            _token = token;
            _read = Read();
        }

        private async Task<HostRequest> Read()
        {
            await _server.WaitForConnectionAsync(_token);
            return await Protocol.ReadAsync<HostRequest>(_server, _token);
        }

        internal void Expect(NpepNotificationCommand command)
        {
            PumpUntil(() => _read.IsCompleted, $"isolated request did not arrive: {command.Action}");
            _request = _read.GetAwaiter().GetResult();
            Assert(_request.Capability == "npep.notifications" && _request.Notification == command && Protocol.Validate(_request) is null,
                $"expected {command.Action}, received {_request.Notification}");
        }

        internal void Reply(NpepInboxState? state, string outcome = "Succeeded")
        {
            var response = Protocol.WriteAsync(_server, new HostResponse(Protocol.Version, _request!.RequestId,
                outcome, null, "isolated response", Inbox: state), _token);
            PumpUntil(() => response.IsCompleted, "isolated response was not written");
            response.GetAwaiter().GetResult();
        }

        public void Dispose() => _server.Dispose();
    }
}

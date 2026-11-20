using System.IO;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using NPEduTools.App;
using NPEduTools.Contracts;

internal static partial class Program
{
    private static void RunSchoolConnectionChecks()
    {
        NpepState state = new(1, "ACTIVE", "OFFLINE", "证书恢复后将自动连接。", Error: "TLS_VALIDATION_FAILED",
            Origin: "https://school.example", LastReceivedAt: "2026-10-07T01:00:00Z");
        bool unavailable = false;
        bool invalidProtocol = false;
        bool accessDenied = false;
        var requests = new List<HostRequest>();
        var session = new NpepConnectionSession((request, _) =>
        {
            requests.Add(request);
            if (unavailable) throw new IOException("isolated Host unavailable");
            if (invalidProtocol) throw new InvalidDataException("isolated invalid Host receipt");
            if (accessDenied) throw new UnauthorizedAccessException("isolated Host access denied");
            return Task.FromResult(new HostResponse(Protocol.Version, request.RequestId, "Succeeded", null, "隔离状态", Npep: state));
        });
        session.RefreshAsync().GetAwaiter().GetResult();
        var settings = new NpepConnectionControl(); settings.Bind(session);
        var onboarding = new NpepConnectionControl(); onboarding.Bind(session);
        var panels = new Grid(); panels.ColumnDefinitions.Add(new()); panels.ColumnDefinitions.Add(new());
        for (int i = 0; i < 2; i++)
        {
            var content = new StackPanel();
            content.Children.Add(new TextBlock { Text = i == 0 ? "设置页共享控件" : "OOBE 共享控件", FontWeight = FontWeights.Bold, Margin = new Thickness(0, 0, 0, 14) });
            content.Children.Add(i == 0 ? settings : onboarding);
            var scroll = new ScrollViewer { Content = content, Padding = new Thickness(18), VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
            Grid.SetColumn(scroll, i); panels.Children.Add(scroll);
        }
        var fixture = new Window { Title = "学校互联提示 · 隔离控件测试", Width = 1180, Height = 780, Background = Brushes.White, Content = panels };
        Exercise(fixture, window =>
        {
            window.UpdateLayout();
            foreach (var view in new[] { settings, onboarding })
            {
                Assert(((TextBlock)Find(view, "NpepStatusTitle")).Text.Contains("自动重试"), "retry title binding missing");
                Assert(((TextBlock)Find(view, "NpepNextAction")).Text.Contains("每次仍验证证书"), "TLS validation advice missing");
                Assert(((TextBlock)Find(view, "NpepError")).Text.Contains("证书验证失败"), "Chinese cause binding missing");
                Assert(((TextBlock)Find(view, "NpepReceipt")).Text.Contains("2026-10-07") &&
                    !((TextBlock)Find(view, "NpepMessage")).IsVisible && !((TextBlock)Find(view, "NpepPairingStage")).IsVisible,
                    "bound summary lost the last receipt or repeated normal polling/setup text");
                Assert(!((Expander)Find(view, "NpepBindingManagement")).IsExpanded, "binding controls expanded before use");
                var expander = (Expander)Find(view, "NpepTechnicalDetailsPanel");
                Assert(!expander.IsExpanded, "technical details expanded by default");
            }
            Snapshot(window, "school-retry.png");
            var bindingPanel = (Expander)Find(settings, "NpepBindingManagement");
            bindingPanel.IsExpanded = true; window.UpdateLayout();
            Assert(((TextBox)Find(bindingPanel, "NpepCurrentOrigin")).Text == state.Origin &&
                ((Button)Find(bindingPanel, "NpepChangeProvider")).IsVisible && ((Button)Find(bindingPanel, "NpepUnpair")).IsVisible,
                "bound provider and removal controls must be reachable together");
            session.RefreshAsync().GetAwaiter().GetResult(); window.UpdateLayout();
            Assert(bindingPanel.IsExpanded && !((Expander)Find(onboarding, "NpepBindingManagement")).IsExpanded,
                "polling or another view changed the teacher's disclosure");
            bindingPanel.IsExpanded = false;
            var detailPanel = (Expander)Find(settings, "NpepTechnicalDetailsPanel");
            detailPanel.IsExpanded = true; window.UpdateLayout();
            var details = (TextBox)Find(settings, "NpepTechnicalDetails");
            Assert(details.IsReadOnly && details.IsVisible && details.Text.Contains("TLS_VALIDATION_FAILED"), "raw detail not copyable/visible");
            detailPanel.IsExpanded = false;
            Checks.Add("Two real shared controls show the same retry/cause/advice; collapsed details expand into a read-only diagnostic field");

            state = state with { Connection = "PAUSED", ReportingPaused = true, Error = null };
            session.RefreshAsync().GetAwaiter().GetResult(); window.UpdateLayout();
            Assert(((TextBlock)Find(onboarding, "NpepStatusTitle")).Text.Contains("互联已暂停"), "shared pause title stale");
            Assert(((Button)Find(settings, "NpepPause")).Content.ToString() == "恢复互联", "resume button changed");
            state = state with { State = "SUSPENDED", Connection = "STOPPED", ReportingPaused = false, Error = "AUTH_INVALID" };
            session.RefreshAsync().GetAwaiter().GetResult(); window.UpdateLayout();
            Assert(((TextBlock)Find(settings, "NpepStatusTitle")).Text.Contains("已停用"), "suspension title missing");
            Assert(!((Button)Find(onboarding, "NpepPause")).IsVisible, "suspended device shows pause/resume");
            Snapshot(window, "school-suspended.png");
            unavailable = true; session.RefreshAsync().GetAwaiter().GetResult(); window.UpdateLayout();
            Assert(((TextBlock)Find(onboarding, "NpepStatusTitle")).Text.Contains("未确认"), "missing Host retains old suspension/online title");
            Assert(!((TextBox)Find(settings, "NpepTechnicalDetails")).Text.Contains("AUTH_INVALID"), "old error survived missing snapshot");
            unavailable = false; state = state with { State = "ACTIVE", Connection = "ONLINE", Error = null };
            session.RefreshAsync().GetAwaiter().GetResult(); window.UpdateLayout();
            Assert(((TextBlock)Find(settings, "NpepStatusTitle")).Text.Contains("当前在线"), "recovered online title missing");
            Assert(!((TextBlock)Find(onboarding, "NpepError")).IsVisible, "recovered error still visible");
            Assert(requests.All(r => r.Capability == "npep.status" && r.Npep is null), "rendering or refreshing sent a mutation");
            Checks.Add("Shared bindings refresh pause/suspension/Host loss/recovery; existing buttons remain consistent; reads never create commands");

            session.DeviceName = "隔离设备草稿";
            invalidProtocol = true;
            Assert(!session.RefreshAsync().GetAwaiter().GetResult(), "invalid protocol was treated as a successful refresh");
            window.UpdateLayout();
            foreach (var view in new[] { settings, onboarding })
            {
                Assert(((TextBlock)Find(view, "NpepStatusTitle")).Text.Contains("未确认"), "invalid protocol retained the online title");
                Assert(!((Button)Find(view, "NpepPause")).IsEnabled && !((Button)Find(view, "NpepUnpair")).IsEnabled,
                    "invalid protocol retained school mutation controls");
                Assert(((Button)Find(view, "NpepRefresh")).IsEnabled, "invalid protocol blocked refresh");
            }
            Assert(!session.CanFinish && !session.IsWorking && session.DeviceName == "隔离设备草稿",
                "unknown school state completes onboarding, holds the gate or loses the local draft");
            Snapshot(window, "school-protocol-unconfirmed.png");
            invalidProtocol = false; state = state with { Revision = 2, ReportingPaused = true };
            Assert(session.RefreshAsync().GetAwaiter().GetResult(), "fresh school state did not recover"); window.UpdateLayout();
            foreach (var view in new[] { settings, onboarding })
                Assert(((TextBlock)Find(view, "NpepStatusTitle")).Text.Contains("互联已暂停") &&
                    ((Button)Find(view, "NpepPause")).Content.ToString() == "恢复互联" &&
                    ((Button)Find(view, "NpepPause")).IsEnabled, "valid recovery did not refresh both shared controls");
            Assert(requests.All(r => r.Capability == "npep.status" && r.Npep is null), "protocol recovery sent a mutation");
            Checks.Add("Invalid protocol clears both shared online views and completion eligibility; read-only refresh restores the latest paused state without losing the device draft");

            var previousContext = SynchronizationContext.Current;
            var eventContext = new QuickEventContext();
            Exception? eventError = null;
            void OnError(object sender, DispatcherUnhandledExceptionEventArgs args)
            { eventError = args.Exception; args.Handled = true; }
            Application.Current.DispatcherUnhandledException += OnError;
            SynchronizationContext.SetSynchronizationContext(eventContext);
            try
            {
                accessDenied = true;
                ((Button)Find(settings, "NpepRefresh")).RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
                PumpUntil(() => eventContext.Active == 0, "school refresh event did not settle after access denial");
                var idle = Application.Current.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle).Task;
                PumpUntil(() => idle.IsCompleted, "school refresh exception dispatch did not settle");
                Assert(eventError is null, "school refresh event raised an unhandled " + eventError?.GetType().Name);
                window.UpdateLayout();
                foreach (var view in new[] { settings, onboarding })
                {
                    Assert(((TextBlock)Find(view, "NpepStatusTitle")).Text.Contains("未确认"), "access denial retained shared connected status");
                    Assert(!((Button)Find(view, "NpepPause")).IsEnabled && !((Button)Find(view, "NpepUnpair")).IsEnabled &&
                        ((Button)Find(view, "NpepRefresh")).IsEnabled, "access denial retained mutation controls or blocked recovery");
                }
                Assert(!session.CanFinish && !session.IsWorking && session.DeviceName == "隔离设备草稿",
                    "access denial allowed onboarding completion, held the request gate or discarded the draft");
                accessDenied = false; state = state with { Revision = 3, Connection = "ONLINE", ReportingPaused = false };
                Assert(session.RefreshAsync().GetAwaiter().GetResult(), "read-only refresh after access denial failed"); window.UpdateLayout();
                foreach (var view in new[] { settings, onboarding })
                    Assert(((TextBlock)Find(view, "NpepStatusTitle")).Text.Contains("当前在线") &&
                        ((Button)Find(view, "NpepPause")).IsEnabled, "fresh status failed to restore both shared views");
                Assert(requests.All(r => r.Capability == "npep.status" && r.Npep is null), "access recovery replayed a school mutation");
                Checks.Add("Access-denied real refresh event does not crash; both shared views become unknown and recover on read-only status without losing the draft or replaying commands");
            }
            finally
            {
                accessDenied = false;
                var idle = Application.Current.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle).Task;
                PumpUntil(() => idle.IsCompleted, "school access cleanup did not settle");
                Application.Current.DispatcherUnhandledException -= OnError;
                SynchronizationContext.SetSynchronizationContext(previousContext);
            }
            window.Close();
        });

        // Also verify the actual wizard at its minimum supported size, with the same isolated session.
        state = state with { State = "ACTIVE", Connection = "OFFLINE", ReportingPaused = false, Error = "TLS_VALIDATION_FAILED" };
        session.RefreshAsync().GetAwaiter().GetResult();
        var actionsType = typeof(OnboardingWindow).Assembly.GetType("NPEduTools.App.OnboardingActions")!;
        Action nothing = () => { };
        var actions = Activator.CreateInstance(actionsType, Enumerable.Repeat<object>(nothing, 9).ToArray())!;
        var constructor = typeof(OnboardingWindow).GetConstructors(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic).Single();
        var wizard = (OnboardingWindow)constructor.Invoke(["NPEduTools.Test.connection-ui." + Guid.NewGuid().ToString("N"),
            new OnboardingState(Features: OnboardingFeatures.School, Step: "school"), (Action<OnboardingState>)(_ => { }),
            session, actions, (Action<bool>)(_ => { }), null]);
        wizard.Width = 720; wizard.Height = 600;
        Exercise(wizard, window =>
        {
            window.UpdateLayout();
            var school = (NpepConnectionControl)window.FindName("SchoolConnection");
            Assert(ReferenceEquals(school.DataContext, session), "actual OOBE uses another connection session");
            Assert(((TextBlock)Find(school, "NpepStatusTitle")).Text.Contains("自动重试"), "actual OOBE loses retry title");
            Assert(!((TextBlock)window.FindName("StepSubtitle")).IsVisible, "bound wizard still directs teacher to enter a new service address");
            CheckSchoolFirstScreen(school, (ScrollViewer)window.FindName("PageScroll"));
            Assert(Button(window, "NextButton").IsEnabled && Button(window, "NextButton").ActualHeight > 0,
                "minimum OOBE layout or offline pairing blocks the existing next button");
            Snapshot(window, "oobe-school-minimum.png");
            window.Close();
        });
        Assert(requests.All(r => r.Capability == "npep.status" && r.Npep is null), "OOBE rendering mutated school state");
        Checks.Add("Actual OOBE school step uses the shared session at 720x600; existing next action remains available for a completed offline pairing");

        var main = PrivateWorkspace("NPEduTools.Test.school-summary." + Guid.NewGuid().ToString("N"));
        typeof(MainWindow).GetField("_schoolConnection", WorkspaceFlags)!.SetValue(main, session);
        WorkspaceCall(main, "InitializeNpepConnection");
        Exercise(main, window =>
        {
            Click(window, "HomeSchoolConnection", true);
            window.Width = 900; window.Height = 650;
            window.UpdateLayout();
            var view = (NpepConnectionControl)window.FindName("SettingsSchoolConnection");
            var scroll = (ScrollViewer)window.FindName("SettingsScroll");
            CheckSchoolFirstScreen(view, scroll);
            Snapshot(window, "school-settings-summary.png");
            state = state with { Error = null };
            session.RefreshAsync().GetAwaiter().GetResult(); window.UpdateLayout();
            CheckSchoolFirstScreen(view, scroll, expectError: false);
            Assert(((TextBlock)Find(view, "NpepStatusTitle")).Text == "等待学校新回执" &&
                ((TextBlock)Find(view, "NpepNextAction")).Text.Contains("无需操作"), "expiry without a network failure looked like a connection fault");
            Snapshot(window, "school-settings-waiting.png");
            state = state with { State = "UNPAIRED", Connection = "WAITING", Origin = null, Error = null, LastReceivedAt = null };
            session.RefreshAsync().GetAwaiter().GetResult(); window.UpdateLayout();
            Assert(((TextBox)Find(view, "NpepOrigin")).IsVisible && ((TextBlock)Find(view, "NpepPairingStage")).IsVisible &&
                ((Button)Find(view, "NpepInspect")).IsVisible, "pairing form remained folded after leaving the binding");
            state = state with { State = "APPROVED", Approval = new JsonObject {
                ["approvalId"] = Guid.NewGuid().ToString(), ["schoolName"] = "测试学校", ["screenBindingName"] = "测试大屏" } };
            session.RefreshAsync().GetAwaiter().GetResult(); window.UpdateLayout();
            var consent = (CheckBox)Find(view, "NpepBindingConfirmed");
            var confirm = (Button)Find(view, "NpepConfirm");
            Assert(((TextBlock)Find(view, "NpepBinding")).Text.Contains("测试学校") && consent.IsVisible && !confirm.IsEnabled,
                "approved pairing hid the ownership review or allowed unconfirmed binding");
            consent.IsChecked = true;
            Assert(confirm.IsEnabled, "reviewing the current binding did not enable the existing confirmation action");
            state = state with { State = "ACTIVE", Connection = "WAITING" };
            session.RefreshAsync().GetAwaiter().GetResult(); window.UpdateLayout();
            Assert(!((Expander)Find(view, "NpepBindingManagement")).IsExpanded && ((TextBlock)Find(view, "NpepReceipt")).Text.Contains("暂无"),
                "newly bound device exposed setup actions or fabricated a receipt");
            window.Close();
        });
        Assert(requests.All(r => r.Capability == "npep.status" && r.Npep is null), "opening disclosures or changing pages issued a command");
        Checks.Add("Settings home entry shows status, receipt and guidance before scrolling; pairing and ownership consent stay inline until bound");
    }

    private static void CheckSchoolFirstScreen(NpepConnectionControl view, ScrollViewer scroll, bool expectError = true)
    {
        Assert(scroll.VerticalOffset == 0, "school connection did not open at the top");
        var viewport = (FrameworkElement)scroll.Template.FindName("PART_ScrollContentPresenter", scroll);
        foreach (var id in new[] { "NpepStatusTitle", "NpepReceipt", "NpepError", "NpepNextAction" })
        {
            var element = (TextBlock)Find(view, id);
            if (id == "NpepError" && !expectError)
            {
                Assert(!element.IsVisible, "the previous connection error remained visible");
                continue;
            }
            var bounds = element.TransformToAncestor(viewport).TransformBounds(new Rect(element.RenderSize));
            Assert(element.IsVisible && !string.IsNullOrWhiteSpace(element.Text) && bounds.Top >= -1 &&
                bounds.Bottom <= viewport.ActualHeight + 1 && bounds.Left >= -1 && bounds.Right <= viewport.ActualWidth + 1,
                id + " was not readable in the first viewport");
        }
    }
}

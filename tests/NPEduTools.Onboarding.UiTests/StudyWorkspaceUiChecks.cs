using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using NPEduTools.App;
using NPEduTools.Contracts;
using NPEduTools.Core;
using NPEduTools.Host;

internal static partial class Program
{
    private static void RunStudyWorkspaceChecks()
    {
        RunWorkspaceReportThresholdChecks();
        RunStudyReturnChecks();
        var previous = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Application.Current.Dispatcher));
        string folder = Path.Combine(_output, "workspace-private-fixture");
        const string secret = "fixture-workspace-admin";
        var management = new NoiseManagementStore(folder);
        Assert(management.Configure(null, secret, false) is null, "fixture credential setup failed");
        var backend = new MeterUiBackend { Value = 60 };
        var meter = new SoundMeterService(() => backend, value => value == secret ? null : "MANAGEMENT_SECRET_INVALID");
        int formalCapture = 0;
        UsbUiCapture? referenceCapture = null;
        var noise = new NoiseService(_ => { formalCapture++; throw new InvalidOperationException("No physical/formal capture"); },
            () => [new("synthetic-mic", "隔离模拟麦克风")], directory: folder, management: management,
            referenceCreate: _ => referenceCapture = new(-30), referenceMeterOpen: meter.OpenReferenceAsync);
        var snapshot = noise.Snapshot();
        noise.Handle(new(1, Guid.NewGuid(), "noise.command", Noise: new("select", snapshot.InstanceId, snapshot.Revision, "synthetic-mic")));
        var manual = new ManualStudyService(noise, () => (null, false, StudyFixture()), () => false, folder, background: false);
        var devices = new StudyDeviceService(() => [], folder);
        string pipe = "NPEduTools.Test.workspace." + Guid.NewGuid().ToString("N");
        string hostPipe = pipe + ".host";
        using var stop = new CancellationTokenSource();
        var server = Task.Run(() => new PipeServer(hostPipe, new DeviceReader(), _ => { }, noise: noise,
            studyDevices: devices, soundMeter: meter, manualStudy: manual).RunAsync(stop.Token));
        int dropReservationReply = 0, offline = 0, offlineReads = 0, startRequests = 0;
        var transport = Task.Run(() => WorkspaceProxy(pipe, async request =>
        {
            if (Volatile.Read(ref offline) != 0 &&
                (NoiseCalibrationStatusContract.BaseCapability(request.Capability) ?? request.Capability) is "noise.status" or "manual.study.status")
            {
                Interlocked.Increment(ref offlineReads);
                throw new IOException("Fixture transport disconnected.");
            }
            bool starts = request.Capability == "manual.study.command" && request.ManualStudy?.Action == "start";
            if (starts) Interlocked.Increment(ref startRequests);
            var response = await HostClient.RequestAsync(hostPipe, request, stop.Token);
            if (starts && response.Outcome == "Succeeded" && Interlocked.Exchange(ref dropReservationReply, 0) != 0)
            {
                Volatile.Write(ref offline, 1);
                throw new IOException("Fixture lost the original successful reply.");
            }
            return response;
        }, stop.Token, supportsCalibrationStatus: true, supportsSchoolMicrophonePolicy: true));
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var main = (MainWindow)typeof(MainWindow).GetConstructors(flags).Single().Invoke([pipe, null, false]);
        void Invoke(string method, params object[] values) => typeof(MainWindow).GetMethod(method, flags)!.Invoke(main, values);
        T Field<T>(string name) where T : class => (T)typeof(MainWindow).GetField(name, flags)!.GetValue(main)!;
        T Element<T>(string name) where T : FrameworkElement => (T)main.FindName(name);
        try
        {
            Exercise(main, owner =>
            {
                Assert(Find(owner, "StudyTab") is Button && Find(owner, "HomeStudyPrimary") is Button, "unified entry missing");
                var fold = (Button)Find(owner, "FoldWorkspaceSidebar");
                fold.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent)); main.UpdateLayout();
                Assert(((ColumnDefinition)main.FindName("WorkspaceNavColumn")).Width.Value == 64 &&
                    ((Button)Find(owner, "StudyTab")).ToolTip?.ToString() == "自习", "compact navigation lost its label or did not fold");
                fold.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent)); main.UpdateLayout();
                Assert(((ColumnDefinition)main.FindName("WorkspaceNavColumn")).Width.Value == 164, "navigation did not restore");
                typeof(MainWindow).GetField("_workspaceSidebarChoice", flags)!.SetValue(main, null);
                Snapshot(owner, "workspace-home.png");
                Invoke("ShowStudyWorkspace", "Manual");
                var panel = Field<NoiseWindow>("_noiseWindow");
                PumpUntil(() => ((Button)panel.FindName("StartButton")).IsEnabled, "embedded manual panel did not receive real Host state");
                Assert(((TextBlock)panel.FindName("CalibrationStatus")).Text == "未校准", "real Host calibration summary did not reach the manual settings panel");
                Assert(!panel.IsVisible && Element<FrameworkElement>("StudyWorkspace").IsVisible, "manual opened another native window");
                Assert(((FrameworkElement)panel.FindName("StartFields")).Visibility == Visibility.Collapsed &&
                    ((FrameworkElement)panel.FindName("EndFields")).Visibility == Visibility.Collapsed, "optional dates crowd immediate workflow");
                Assert(!Element<FrameworkElement>("WorkspaceReportTools").IsVisible, "manual workspace has a second action bar");
                var microphone = (ComboBox)panel.FindName("Microphone");
                var devicesGroup = (Expander)panel.FindName("ManualDeviceSettings");
                microphone.SelectedIndex = -1;
                Assert(Button(panel, "ManualChooseDevice").IsVisible && !Button(panel, "StartButton").IsEnabled, "missing microphone has no inline action");
                Click(panel, "ManualChooseDevice");
                Assert(devicesGroup.IsExpanded && microphone.IsKeyboardFocusWithin, "choose microphone did not open and focus the picker");
                microphone.SelectedIndex = 0;
                devicesGroup.IsExpanded = false;
                RunManualDateBoundaryChecks(panel, manual);
                Assert(formalCapture == 0, "rejected date started capture");
                SelectManualTimeOptions(panel, reserve: false, timedEnd: false);
                Assert(((TextBlock)panel.FindName("MessageText")).Text == "", "immediate start retained the previous draft's invalid date error");
                ((ScrollViewer)panel.FindName("ManualPageScroll")).ScrollToTop();
                Snapshot(owner, "workspace-manual-immediate.png");
                SelectManualTimeOptions(panel, reserve: true, timedEnd: true);
                Assert(((FrameworkElement)panel.FindName("StartFields")).IsVisible && ((Button)panel.FindName("StartButton")).Content.ToString() == "保存预约", "future start did not unfold or change primary action");
                var future = DateTime.Now.AddDays(1);
                ((DatePicker)panel.FindName("ManualStartDate")).SelectedDate = future.Date;
                ((TextBox)panel.FindName("ManualStartTime")).Text = "23:45";
                ((DatePicker)panel.FindName("ManualEndDate")).SelectedDate = future.AddDays(1).Date;
                ((TextBox)panel.FindName("ManualEndTime")).Text = "00:15";
                var idleReturn = Descendants<Expander>(WorkspacePanel.View(panel)!).Single(e => Equals(e.Header, "临时使用电脑后返回"));
                var scroll = (ScrollViewer)panel.FindName("ManualPageScroll");
                void Resize(int width, int height)
                {
                    main.MinWidth = main.MinHeight = 0;
                    main.Width = width;
                    main.Height = height;
                    scroll.ScrollToTop();
                    main.UpdateLayout();
                    PumpUntil(() => Math.Abs(main.ActualWidth - width) < 1 && Math.Abs(main.ActualHeight - height) < 1,
                        "manual workspace window did not resize");
                }
                void InViewport(FrameworkElement control)
                {
                    Rect bounds = control.TransformToAncestor(scroll).TransformBounds(new Rect(control.RenderSize));
                    Assert(control.IsVisible && bounds.Width > 0 && bounds.Height > 0 && bounds.Left >= -1 && bounds.Top >= -1 &&
                        bounds.Right <= scroll.ViewportWidth + 1 && bounds.Bottom <= scroll.ViewportHeight + 1,
                        $"{control.Name} is clipped in the manual workspace at {main.Width}x{main.Height}: {bounds.Bottom:0}/{scroll.ViewportHeight:0}");
                }
                foreach (var (width, height) in new[] { (1040, 760), (840, 600) })
                {
                    Resize(width, height);
                    Snapshot(owner, $"manual-reservation-draft-{width}-{height}.png");
                    var side = (FrameworkElement)panel.FindName("ManualSidePanel");
                    Assert(Grid.GetColumn(side) == (width == 1040 ? 1 : 0),
                        $"manual workspace layout did not reflow at {width} with a {scroll.ActualWidth:0}-DIP content viewport");
                    foreach (string name in new[] { "ManualStartDate", "ManualStartTime", "ManualEndDate", "ManualEndTime", "LevelText" })
                        InViewport((FrameworkElement)panel.FindName(name));
                    InViewport(idleReturn);
                    Assert(Window.GetWindow(scroll) == main && !panel.IsVisible &&
                        new[] { "MinimizeWindow", "MaximizeRestoreWindow", "CloseWindow" }.All(id => Find(main, id) is Button { IsVisible: true }),
                        "handling a manual reservation changed the native window or lost a main-window caption action");
                    var primary = Button(panel, "StartButton");
                    var primaryBounds = primary.TransformToAncestor(main).TransformBounds(new Rect(primary.RenderSize));
                    Assert(primary.IsVisible && primary.IsEnabled && primaryBounds.Left >= 0 &&
                        primaryBounds.Right <= main.ActualWidth && primaryBounds.Bottom < main.ActualHeight - 4,
                        "reservation cannot be saved from the first screen");
                }

                int previousStarts = Volatile.Read(ref startRequests);
                Volatile.Write(ref dropReservationReply, 1);
                Click(panel, "StartButton");
                PumpUntil(() => Volatile.Read(ref offlineReads) >= 2 && !Button(panel, "StartButton").IsEnabled,
                    "manual panel did not encounter repeated transport loss after the Host committed the reservation");
                string uncertain = ((TextBlock)panel.FindName("MessageText")).Text;
                Assert(uncertain.Contains("待确认") && manual.Status().State == "Waiting",
                    "a failed status poll erased the unconfirmed original reservation result");
                var original = manual.Status();
                Snapshot(owner, "manual-reservation-unconfirmed-840-600.png");
                Invoke("ShowStudyWorkspace", "School");
                Invoke("ShowStudyWorkspace", "Manual");
                Assert(((TextBox)panel.FindName("ManualStartTime")).Text == "23:45" &&
                    ((TextBox)panel.FindName("ManualEndTime")).Text == "00:15" && !Button(panel, "StartButton").IsEnabled &&
                    ((TextBlock)panel.FindName("MessageText")).Text.Contains("待确认"),
                    "returning while disconnected lost the cross-midnight draft or original unconfirmed state");
                Volatile.Write(ref offline, 0);
                PumpUntil(() => manual.Status().State == "Waiting" && ((Button)panel.FindName("StopButton")).IsEnabled, "future manual reservation not accepted");
                Assert(Volatile.Read(ref startRequests) == previousStarts + 1 && manual.Status() == original &&
                    ((TextBlock)panel.FindName("MessageText")).Text == "",
                    "recovery replayed the same reservation or left it unconfirmed after reading its original status");
                Assert(((FrameworkElement)panel.FindName("ManualConfiguration")).Visibility == Visibility.Collapsed &&
                    ((Button)panel.FindName("StopButton")).Content.ToString() == "取消预约", "waiting session still shows a duplicate start form");
                string booking = ((TextBlock)panel.FindName("ManualStatus")).Text;
                Assert(booking.Contains($"{future:yyyy-MM-dd} 23:45") && booking.Contains($"{future.AddDays(1):yyyy-MM-dd} 00:15") &&
                    booking.Contains("计划结束") && booking.Contains("尚未开始采集"), "cross-day reservation mislabels the scheduled period or capture status");
                Snapshot(owner, "workspace-manual-reservation.png");
                Snapshot(owner, "manual-reservation-confirmed-840-600.png");
                Resize(1040, 760);
                Assert(Grid.GetColumn((FrameworkElement)panel.FindName("ManualSidePanel")) == 0,
                    "the confirmed reservation left an empty form column in the wide window");
                InViewport((TextBlock)panel.FindName("ManualStatus"));
                InViewport((TextBlock)panel.FindName("LevelText"));
                Snapshot(owner, "manual-reservation-confirmed-1040-760.png");
                Resize(840, 600);
                Checks.Add("Manual study at 1040x760/840x600: time fields, signal and temporary-use entry fit the first viewport inside the main window. A real cross-midnight reservation survives a dropped reply, repeated failed reads and navigation, with exactly one start write.");
                devicesGroup.IsExpanded = true;
                Click(panel, "ManualDeviceLink");
                var schoolPanel = Field<StudyMonitoringWindow>("_studyMonitoringWindow");
                main.UpdateLayout();
                Assert(Element<FrameworkElement>("WorkspaceDevices").IsVisible &&
                    ReferenceEquals(Element<ContentControl>("WorkspaceDevicesHost").Content, schoolPanel.FindName("Devices")),
                    "device link did not open the shared picker in the main workspace");
                Click(main, "WorkspaceDevicesBack");
                Assert(Element<FrameworkElement>("ManualWorkspaceHost").IsVisible,
                    "device page did not return to the manual reservation view");
                Invoke("ShowStudyWorkspace", "School");
                PumpUntil(() => Button(schoolPanel, "SchoolSeatingButton").IsVisible, "school seating action is missing");
                Assert(!Button(schoolPanel, "SchoolManualLink").IsVisible && !Element<FrameworkElement>("WorkspaceReportTools").IsVisible,
                    "school workspace still has duplicate navigation");
                Assert(manual.Status().State == "Waiting" && formalCapture == 0, "switching tabs stopped/resumed capture");
                Invoke("HomeNavClicked", main, new RoutedEventArgs());
                Assert(!Element<FrameworkElement>("StudyWorkspace").IsVisible && Element<FrameworkElement>("HomePage").IsVisible, "home left a workspace overlay");
                Invoke("ShowStudyWorkspace", "Manual");
                Assert(ReferenceEquals(panel, Field<NoiseWindow>("_noiseWindow")), "tab switch duplicated the manual controller");
                PumpUntil(() => ((Button)panel.FindName("StopButton")).IsEnabled,"returned manual state was not freshly confirmed");
                ((Button)panel.FindName("StopButton")).RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
                PumpUntil(() => manual.Status().State == "Cancelled" && ((Button)panel.FindName("StartButton")).IsEnabled, "bound manual cancel did not restore idle form");
                Assert(((TextBlock)panel.FindName("ManualHeading")).Text == "自习预约已取消" &&
                    !((TextBlock)panel.FindName("ManualStatus")).Text.Contains("CancelledBeforeStart") && !Button(panel, "ManualReportButton").IsVisible,
                    "cancelled reservation exposed an internal reason or suggested it generated a report");
                Checks.Add("Actual WPF/private Host: one study entry, progressive manual form, real future reservation/cancel, tab switching preserves session and controller; zero formal capture");

                foreach (var size in new[] { new Size(1040, 760), new Size(840, 580), new Size(760, 520) })
                {
                    main.MinWidth = main.MinHeight = 0; main.Width = size.Width; main.Height = size.Height; main.UpdateLayout();
                    var action = (Button)panel.FindName("StartButton");
                    var point = action.TranslatePoint(new Point(), main);
                    Assert(action.IsVisible && point.X >= 0 && point.X + action.ActualWidth <= main.ActualWidth &&
                        point.Y + action.ActualHeight < main.ActualHeight - 4, "manual primary action clipped in compact layout");
                    Snapshot(owner, $"workspace-manual-{size.Width}-{size.Height}.png");
                }
                devicesGroup.IsExpanded = true;
                Click(panel, "ManualCalibrationLink");
                var connected = Field<SoundMeterWindow>("_workspaceCalibrationController");
                PumpUntil(() => ((TextBlock)connected.FindName("ManagementLabel")).Text.Contains("本机"), "embedded access status not loaded");
                Assert(backend.Opens == 0 && backend.Enumerations == 0, "opening embedded calibration touched hardware");
                ((PasswordBox)connected.FindName("ManagementSecret")).Password = "wrong-password";
                ((Button)connected.FindName("ListDevices")).RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
                PumpUntil(() => ((TextBlock)connected.FindName("Message")).Text.Contains("验证未通过"), "bad password not reported");
                Assert(backend.Enumerations == 0, "rejected password enumerated devices");
                ((PasswordBox)connected.FindName("ManagementSecret")).Password = secret;
                ((Button)connected.FindName("ListDevices")).RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
                PumpUntil(() => ((ComboBox)connected.FindName("Devices")).Items.Count == 1, "verified catalog did not arrive");
                ((ComboBox)connected.FindName("Devices")).SelectedIndex = 0;
                Assert(((FrameworkElement)connected.FindName("CredentialsPanel")).Visibility == Visibility.Collapsed &&
                    ((Button)connected.FindName("ResetVerification")).IsVisible, "verified credentials crowd the connection step");
                Snapshot(owner, "workspace-calibration-connected.png");
                ((Button)connected.FindName("OpenReference")).RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
                var reference = Field<NoiseReferenceWindow>("_workspaceCalibrationController");
                PumpUntil(() => ((Button)reference.FindName("BeginDraft")).IsEnabled, "reference did not receive live Host state");
                Assert(!reference.IsVisible && ((PasswordBox)reference.FindName("ManagementSecret")).Password == secret &&
                    ((PasswordBox)connected.FindName("ManagementSecret")).Password.Length == 0, "same-page transition lost credentials or retained closed-panel secret");
                Assert(((FrameworkElement)reference.FindName("ReferenceCredentials")).Visibility == Visibility.Collapsed,
                    "same-page verified reference repeated the password form");
                typeof(NoiseReferenceWindow).GetMethod("ShowAccess", flags)!.Invoke(reference, [noise.Protection() with { CredentialRevision = 99 }]);
                Assert(((FrameworkElement)reference.FindName("ReferenceCredentials")).IsVisible &&
                    ((PasswordBox)reference.FindName("ManagementSecret")).Password.Length == 0, "changed credential revision retained a verified password");
                ((PasswordBox)reference.FindName("ManagementSecret")).Password = secret;
                ((ComboBox)reference.FindName("WorkflowChoice")).SelectedValue = "Stable";
                ((TextBox)reference.FindName("Stability")).Text = "1";
                ((TextBox)reference.FindName("CheckTolerance")).Text = "1";
                ((Button)reference.FindName("BeginDraft")).RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
                PumpUntil(() => ((Button)reference.FindName("StartSample")).IsEnabled, "reference draft did not advance");
                main.UpdateLayout(); Snapshot(owner, "workspace-calibration-ready.png");
                var start = (Button)reference.FindName("StartSample"); var location = start.TranslatePoint(new Point(), main);
                Assert(start.IsVisible && location.Y + start.ActualHeight < main.ActualHeight - 4, "sampling primary action not pinned in compact layout");
                start.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
                PumpUntil(() => backend.Opens == 1 && ((Button)reference.FindName("CancelSample")).IsEnabled, "embedded owned sample not confirmed");
                Invoke("HomeNavClicked", main, new RoutedEventArgs());
                PumpUntil(() => backend.Disposals == 1 && referenceCapture?.Disposed == true, "leaving calibration did not release owned synthetic sensors");
                Assert(formalCapture == 0 && manual.Status().State == "Cancelled", "calibration navigation mutated formal monitoring");
                Checks.Add("Actual embedded USB flow: denied/verified credentials, same-page reference transition, compact pinned actions, owned sample cancellation and secret cleanup; synthetic HID/microphone only");
                Invoke("ShowStudyWorkspace", "Reports");
                var schoolReport = Element<ContentControl>("WorkspaceSchoolReport");
                var manualReport = Element<ContentControl>("WorkspaceManualReport");
                PumpUntil(() => schoolReport.Content is StudyWorkspaceReport { State: WorkspaceReportState.Failed } &&
                    manualReport.Content is StudyWorkspaceReport { State: WorkspaceReportState.Empty }, "unsupported and empty reports were not distinguished");
                main.UpdateLayout();
                Assert(!Descendants<Expander>(schoolReport).Any(e => e.IsVisible) && !Descendants<Expander>(manualReport).Any(e => e.IsVisible),
                    "an unavailable report shows sampling details");
                Invoke("SettingsNavClicked", main, new RoutedEventArgs());
                Assert(!Element<FrameworkElement>("StudyWorkspace").IsVisible && !Element<FrameworkElement>("WorkspaceCalibration").IsVisible, "settings leaves an overlapping workspace");
                Checks.Add("Reports/settings navigation: unknown/unsupported report stays explicit, no upload claim and no overlapping page");
                main.Close();
            });
        }
        finally
        {
            main.Close(); stop.Cancel(); try { Task.WhenAll(server, transport).GetAwaiter().GetResult(); } catch (OperationCanceledException) { }
            manual.DisposeAsync().AsTask().GetAwaiter().GetResult(); noise.DisposeAsync().AsTask().GetAwaiter().GetResult();
            SynchronizationContext.SetSynchronizationContext(previous);
        }
    }
}

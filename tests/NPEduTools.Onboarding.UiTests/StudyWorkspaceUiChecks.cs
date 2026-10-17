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
        using var stop = new CancellationTokenSource();
        var server = Task.Run(() => new PipeServer(pipe, new DeviceReader(), _ => { }, noise: noise,
            studyDevices: devices, soundMeter: meter, manualStudy: manual).RunAsync(stop.Token));
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
                RunManualDateBoundaryChecks(panel, manual);
                Assert(formalCapture == 0, "rejected date started capture");
                Check(panel, "UseStart").IsChecked = Check(panel, "UseEnd").IsChecked = false;
                Snapshot(owner, "workspace-manual-immediate.png");
                ((CheckBox)panel.FindName("UseStart")).IsChecked = true;
                Assert(((FrameworkElement)panel.FindName("StartFields")).IsVisible && ((Button)panel.FindName("StartButton")).Content.ToString() == "保存预约", "future start did not unfold or change primary action");
                var future = DateTime.Now.AddDays(1);
                ((DatePicker)panel.FindName("ManualStartDate")).SelectedDate = future.Date;
                ((TextBox)panel.FindName("ManualStartTime")).Text = "19:15";
                ((Button)panel.FindName("StartButton")).RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
                PumpUntil(() => manual.Status().State == "Waiting" && ((Button)panel.FindName("StopButton")).IsEnabled, "future manual reservation not accepted");
                Assert(((FrameworkElement)panel.FindName("ManualConfiguration")).Visibility == Visibility.Collapsed &&
                    ((Button)panel.FindName("StopButton")).Content.ToString() == "取消预约", "waiting session still shows a duplicate start form");
                Snapshot(owner, "workspace-manual-reservation.png");
                Invoke("ShowStudyWorkspace", "School");
                Assert(manual.Status().State == "Waiting" && formalCapture == 0, "switching tabs stopped/resumed capture");
                Invoke("HomeNavClicked", main, new RoutedEventArgs());
                Assert(!Element<FrameworkElement>("StudyWorkspace").IsVisible && Element<FrameworkElement>("HomePage").IsVisible, "home left a workspace overlay");
                Invoke("ShowStudyWorkspace", "Manual");
                Assert(ReferenceEquals(panel, Field<NoiseWindow>("_noiseWindow")), "tab switch duplicated the manual controller");
                PumpUntil(() => ((Button)panel.FindName("StopButton")).IsEnabled,"returned manual state was not freshly confirmed");
                ((Button)panel.FindName("StopButton")).RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
                PumpUntil(() => manual.Status().State == "Cancelled" && ((Button)panel.FindName("StartButton")).IsEnabled, "bound manual cancel did not restore idle form");
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
                Invoke("WorkspaceCalibrationClicked", main, new RoutedEventArgs());
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
                PumpUntil(() => !Element<TextBlock>("WorkspaceSchoolReport").Text.Contains("正在读取"), "report refresh stalled");
                Assert(Element<TextBlock>("WorkspaceSchoolReport").Text.Contains("后台") && !Element<TextBlock>("WorkspaceSchoolReport").Text.Contains("已上传"), "unsupported report claims successful upload");
                var reportData = new StudyReportData(StudyFixture().Window!, 600, 590, 590d / 600, .1, 100, 2,
                    new("dBFS", 590, -55, -30, 580, 30, 10, []), new("dB(A)", 580, 60, 70, 570, 20, 5, []), true, []);
                var reportResponse = new HostResponse(1, Guid.NewGuid(), "Succeeded", null, "fixture",
                    StudyReport: new(Guid.NewGuid(), "fixture", true, "Ended", 0, reportData));
                var reportText = (string)typeof(MainWindow).GetMethod("ReportText", flags | BindingFlags.Static)!.Invoke(null, [reportResponse])!;
                Assert(reportText.Contains("dBFS") && reportText.Contains("估算 dB(A)") && reportText.Contains("覆盖率") &&
                    reportText.Contains("提醒 2 次") && reportText.Contains("ClassIsland") && !reportText.Contains("已上传"), "report mixed units, dropped coverage or claimed upload");
                Invoke("SettingsNavClicked", main, new RoutedEventArgs());
                Assert(!Element<FrameworkElement>("StudyWorkspace").IsVisible && !Element<FrameworkElement>("WorkspaceCalibration").IsVisible, "settings leaves an overlapping workspace");
                Checks.Add("Reports/settings navigation: unknown/unsupported report stays explicit, no upload claim and no overlapping page");
                main.Close();
            });
        }
        finally
        {
            main.Close(); stop.Cancel(); try { server.GetAwaiter().GetResult(); } catch (OperationCanceledException) { }
            manual.DisposeAsync().AsTask().GetAwaiter().GetResult(); noise.DisposeAsync().AsTask().GetAwaiter().GetResult();
            SynchronizationContext.SetSynchronizationContext(previous);
        }
    }
}

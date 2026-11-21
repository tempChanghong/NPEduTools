using System.IO;
using System.IO.Pipes;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using NPEduTools.App;
using NPEduTools.Contracts;

internal static partial class Program
{
    private static void RunHomeWorkspaceChecks()
    {
        RunShortcutKeyboardChecks();
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        string pipe = "NPEduTools.Test.home-workspace." + Guid.NewGuid().ToString("N");
        var main = (MainWindow)typeof(MainWindow).GetConstructors(flags).Single().Invoke([pipe, null, false]);
        void Invoke(string method, params object?[] args) => typeof(MainWindow).GetMethod(method, flags)!.Invoke(main, args);
        T Element<T>(string name) where T : FrameworkElement => (T)main.FindName(name);
        Invoke("InitializeRecording");
        var client = typeof(MainWindow).GetField("_recording", flags)!.GetValue(main)!;
        client.GetType().GetMethod("Detach")!.Invoke(client, null);
        void Recording(RecordingState state, AutomaticRecordingState automatic) => client.GetType().GetMethod("Apply", flags)!.Invoke(client,
            [new HostResponse(Protocol.Version, Guid.NewGuid(), "Succeeded", null, "isolated snapshot", Recording: state, Automatic: automatic)]);
        var at = new DateTimeOffset(2026, 10, 13, 19, 15, 0, TimeSpan.Zero);
        var automatic = new AutomaticRecordingState(false, Guid.Empty, "测试未启用", null, []);
        var idle = new RecordingState("Idle", "测试准备录制");
        var school = new StudyHostState("fixture", "APPLIED", "NOT_INTEGRATED", "MANUAL", "Idle", "OUTSIDE_WINDOW", true,
            at.AddHours(-1), null, new(at, at.AddMinutes(135)), null);
        var manual = new ManualStudyState(Guid.NewGuid(), 0, null, "Idle", null, null, null, false, 600);
        try
        {
            Exercise(main, owner =>
            {
                Invoke("UpdateStudyHome", school); Invoke("UpdateWorkspaceManual", manual); Recording(idle, automatic);
                Assert(Element<TextBlock>("WorkspaceTaskTitle").Text.Contains("没有自习") && Element<TextBlock>("RecordingHomeTitle").Text.Contains("没有录课"),
                    "confirmed idle tasks are not distinguished");
                Assert(Element<TextBlock>("StudyHomeNext").Text.Contains("ClassIsland 时间") && Element<TextBlock>("HomeRecordingNext").Text.Contains("未开启"),
                    "idle homepage confuses saved plans with enabled recording");
                Snapshot(main, "home-idle.png");
                var activeSchool = school with { Phase = "Active", SchoolNow = at.AddMinutes(30), Window = school.Next, Next = new(at.AddDays(1), at.AddDays(1).AddMinutes(135)), Execution = new("Schedule", "Active", Guid.NewGuid(), "fixture", true, null, null, true) };
                Invoke("UpdateStudyHome", activeSchool);
                var recording = new RecordingState("Recording", "测试正在录制", Seconds: 123, Control: new("Manual", Guid.NewGuid(), "", 0, 0));
                var upcoming = automatic with { Enabled = true, NextState = "Ready", Next = new("数学", at.AddMinutes(50), at.AddMinutes(95)) };
                Recording(recording, upcoming);
                Assert(Element<TextBlock>("WorkspaceTaskTitle").Text.Contains("学校自习") && Element<TextBlock>("StudyHomeStatus").Text.Contains("新鲜采样") &&
                    Element<TextBlock>("RecordingHomeTitle").Text.Contains("手动录课进行中"), "recording refresh overwrote simultaneous study context");
                Assert(Element<TextBlock>("HomeRecordingNext").Text.Contains("数学") && Element<TextBlock>("HomeRecordingNext").Text.Contains("20:05") &&
                    Element<TextBlock>("HomeRecordingNext").Text.Contains("ClassIsland 时间"), "structured future task lost wall time or clock source");
                Recording(recording with { Phase = "Paused" }, upcoming);
                Assert(Element<TextBlock>("RecordingHomeTitle").Text.Contains("暂停") && Element<TextBlock>("StudyHomeStatus").Text.Contains("新鲜采样"), "pause loses independent task context");
                Recording(recording, upcoming);
                main.UpdateLayout(); var before = Element<Button>("WorkspacePrimary").TranslatePoint(new Point(), main);
                Recording(recording with { Seconds = 124 }, upcoming); main.UpdateLayout();
                Assert(Element<Button>("WorkspacePrimary").TranslatePoint(new Point(), main) == before, "recording clock refresh moved study control");
                Snapshot(main, "home-two-active-tasks.png");
                foreach (var size in new[] { new Size(1280, 700), new Size(1024, 540), new Size(760, 520) })
                {
                    main.MinWidth = main.MinHeight = 0; main.Width = size.Width; main.Height = size.Height;
                    ((FrameworkElement)main.Content).LayoutTransform = new ScaleTransform(.8, .8);
                    Element<ScrollViewer>("HomePage").ScrollToTop(); main.UpdateLayout();
                    foreach (string name in new[] { "WorkspacePrimary", "RecordingHomeButton" })
                    {
                        var button = Element<Button>(name);
                        Reachable(main, button);
                        var target = button.TransformToAncestor(main).TransformBounds(new Rect(button.RenderSize));
                        Assert(target.Width >= 44 && target.Height >= 44, $"{name} has a {target.Size} DIP hit area at {size}");
                        var center = button.TranslatePoint(new Point(button.ActualWidth / 2, button.ActualHeight / 2), main);
                        Assert(main.InputHitTest(center) is DependencyObject hit && (ReferenceEquals(hit, button) || button.IsAncestorOf(hit)),
                            $"{name} centre is not reachable from the first screen at {size}");
                    }
                    Reachable(main, Element<Button>("HomeHomeworkButton"));
                    Reachable(main, (Button)Find(main, "HomeSchoolConnection"));
                    Reachable(main, Element<TextBlock>("StudyHomeNext")); Reachable(main, Element<TextBlock>("HomeRecordingNext"));
                    Snapshot(main, $"home-first-screen-{size.Width}-{size.Height}.png");
                    CheckTeacherDpi(main, size);
                }
                CheckTeacherThemeAndFont(main);
                Checks.Add("Actual homepage: independent study and recording, source-specific next items, 44-DIP task targets and upcoming summaries on the first screen at all three logical sizes");
                Recording(recording, upcoming with { NextState = "ClockPending" });
                Assert(!Element<TextBlock>("HomeRecordingNext").Text.Contains("数学"), "clock loss retained stale future recording time");
                client.GetType().GetMethod("MarkUnavailable", flags)!.Invoke(client, [null, null]);
                Assert(Element<TextBlock>("RecordingHomeTitle").Text.Contains("待确认") && !Element<TextBlock>("HomeRecordingNext").Text.Contains("数学"),
                    "disconnection claims idle/live state or keeps cached upcoming plan");
                Recording(recording, upcoming);
                Invoke("UpdateStudyHome", school with { Mode = "Exam", ClockSource = "Windows" });
                Assert(Element<TextBlock>("StudyHomeNext").Text.Contains("Windows 时间") && Element<TextBlock>("HomeRecordingNext").Text.Contains("ClassIsland 时间"),
                    "different clock sources were conflated");
                Invoke("UpdateStudyHome", activeSchool with { ClockReady = false });
                Assert(Element<TextBlock>("WorkspaceTaskTitle").Text.Contains("待确认") && !Element<TextBlock>("WorkspaceTaskTitle").Text.Contains("没有"),
                    "uncertain school execution was hidden as no study task");
                Invoke("UpdateStudyHome", activeSchool with { PolicyState = "PENDING" });
                Assert(Element<TextBlock>("WorkspaceTaskTitle").Text.Contains("待确认") && Element<TextBlock>("StudyHomeStatus").Text.Contains("尚未应用"), "pending policy hid a potentially active school task or claimed applied capture");
                Invoke("UpdateStudyHome", school);
                Invoke("UpdateWorkspaceManual", manual with { State = "Waiting", StartUtc = at.ToUniversalTime() });
                Assert(Element<TextBlock>("StudyHomeStatus").Text.Contains("预约") && Element<TextBlock>("StudyHomeStatus").Text.Contains("Windows 时间"),
                    "manual reservation displayed unrelated school-idle status");
                Recording(recording with { Phase = "Saved", OutputFile = Path.Combine(_output, "synthetic.mp4") }, upcoming);
                Click(main, "RecordingHomeButton");
                Assert(Element<FrameworkElement>("RecordingPlanHost").IsVisible && Element<Button>("RecordingRecordsTab").Tag?.ToString() == "active",
                    "completed recording primary action goes back into setup");
                Invoke("HomeNavClicked", main, new RoutedEventArgs());
                Assert(Element<TextBlock>("RecordingHomeTitle").Text.Contains("已保存"), "return home lost recent result");
                Recording(recording with { Phase = "Failed", Message = "音源中断", RecoveryDirectory = _output }, upcoming);
                Click(main, "RecordingHomeButton");
                Assert(Element<Button>("RecordingRecordsTab").Tag?.ToString() == "active", "failed recording did not lead to records");
                Invoke("HomeNavClicked", main, new RoutedEventArgs());
                Checks.Add("Homepage authority and routing: clock/pipe loss withdraws cached time; fresh snapshot recovers; Windows study source remains separate; manual reservation is explicit; saved result opens records without starting capture");

                var link = new NpepState(2, "ACTIVE", "ONLINE", "学校互联状态。", LastReceivedAt: "2026-10-13T19:15:00.000Z");
                var homework = new StudyHomeworkState("READY", "snapshot", 1, "2026-10-13", "fixture");
                var hidden = activeSchool with { Homework = homework, Display = new(Guid.NewGuid(), true, false, 120) };
                HostResponse Status(StudyHostState study, NpepState? connection = null) => new(Protocol.Version,
                    Guid.NewGuid(), "Succeeded", null, "isolated snapshot", Study: study, Npep: connection ?? link);
                var homeworkButton = Element<Button>("HomeHomeworkButton");
                Assert(!homeworkButton.IsEnabled, "homework action is enabled without authoritative display state");
                Invoke("ObserveHomeSchool", Status(hidden with { ProjectionPriority = true }));
                Assert(!homeworkButton.IsEnabled, "homework action appeared during an exam projection");
                Invoke("ObserveHomeSchool", Status(hidden, link with { ReportingPaused = true }));
                Assert(!homeworkButton.IsEnabled, "homework action appeared after the school link was paused");
                Invoke("ObserveHomeSchool", Status(hidden));
                Assert(homeworkButton.IsEnabled, "fresh hidden school display with homework is not actionable");
                Assert(Element<TextBlock>("HomeSchoolStatus").Text.Contains("当前在线"), "home school row ignored its live receipt");
                VerifyHomeworkCommand("study.show", hidden);

                var manualHidden = hidden with { Display = hidden.Display! with { InstanceId = Guid.NewGuid() }, Manual = manual with { State = "Active" } };
                var manualDisplay = typeof(MainWindow).GetField("_homeManualDisplay", flags)!.GetValue(main)!;
                Invoke("ObserveHomeHomework", manualDisplay, Status(manualHidden));
                Assert(!homeworkButton.IsEnabled, "manual homework displaced an active school session");
                Invoke("ObserveHomeSchool", Status(school));
                Assert(homeworkButton.IsEnabled, "fresh hidden manual display with homework is not actionable");
                VerifyHomeworkCommand("manual.study.show", manualHidden);
                Invoke("ObserveHomeSchool", (object?)null);
                Assert(!homeworkButton.IsEnabled && Element<TextBlock>("HomeSchoolStatus").Text == "连接状态待确认", "the home row kept an obsolete online receipt");
                Checks.Add("Homepage homework action sends the exact validated school/manual display recovery command, follows school and projection priority, and withdraws stale availability");

                Invoke("UpdateStudyHome", school); Invoke("UpdateWorkspaceManual", manual);
                Click(main, "WorkspacePrimary");
                Assert(Element<FrameworkElement>("ManualWorkspaceHost").IsVisible, "home study action did not open the existing manual study entry");
                Click(main, "HomeNav");
                var unpaired = new NpepConnectionSession((request, _) => Task.FromResult(new HostResponse(Protocol.Version,
                    request.RequestId, "Succeeded", null, "尚未配对", Npep: new(1, "UNPAIRED", "WAITING", "请填写学校服务地址。"))));
                typeof(MainWindow).GetField("_schoolConnection", flags)!.SetValue(main, unpaired);
                Invoke("InitializeNpepConnection");
                Click(main, "HomeSchoolConnection", true); main.UpdateLayout();
                Assert(Element<TextBlock>("SettingsHeading").Text == "学校互联" && Element<TextBlock>("PageBreadcrumb").Text == "学校互联" &&
                    !Element<FrameworkElement>("SettingsCategoryTabs").IsVisible && Element<FrameworkElement>("SettingsSchoolConnection").IsVisible &&
                    Equals(Element<Button>("NpepSettingsTab").Tag, "active") && Element<Button>("SettingsNav").Tag is null,
                    "home school entry opened the generic settings shell or selected the wrong navigation item");
                var remoteExam = Element<Expander>("RemoteExamSettings");
                Assert(!remoteExam.IsExpanded && !Element<Button>("RemoteExamCheckButton").IsVisible,
                    "remote exam policy details took space on the initial school page");
                Snapshot(main, "home-school-direct.png");
                remoteExam.IsExpanded = true; main.UpdateLayout();
                Assert(Element<Button>("RemoteExamCheckButton").IsVisible, "remote exam setup can no longer be reached from school connection");
                remoteExam.IsExpanded = false;
                Click(main, "SettingsNav"); main.UpdateLayout();
                Assert(Element<TextBlock>("SettingsHeading").Text == "设备与连接" && Element<FrameworkElement>("SettingsCategoryTabs").IsVisible &&
                    Element<FrameworkElement>("GeneralSettingsPanel").IsVisible && Element<Button>("NpepSettingsTab").Tag is null,
                    "device settings no longer provides a separate destination");
                Click(main, "NpepSettingsTab"); main.UpdateLayout();
                Assert(Element<FrameworkElement>("SettingsSchoolConnection").IsVisible && !Element<FrameworkElement>("SettingsCategoryTabs").IsVisible,
                    "sidebar and home school entries diverged");
                Click(main, "HomeNav");
                Checks.Add("Home and sidebar open the same dedicated school heading with no general settings tabs; the device settings and manual study routes remain separate");

                Invoke("ShowClassroomModeState", (object?)null);
                Assert(Element<Expander>("HomeClassroomModeDetails").IsExpanded, "mode warning was hidden inside closed details");
                Element<Expander>("HomeClassroomModeDetails").IsExpanded = false;
                Invoke("ShowClassroomModeState", (object?)null);
                Assert(!Element<Expander>("HomeClassroomModeDetails").IsExpanded, "unchanged status forcibly reopened dismissed details");
                main.Close();

                void VerifyHomeworkCommand(string capability, StudyHostState expected)
                {
                    using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                    using var peer = new NamedPipeServerStream(pipe, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
                        PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                    async Task<HostRequest> ReadAsync()
                    {
                        await peer.WaitForConnectionAsync(deadline.Token);
                        var request = await Protocol.ReadAsync<HostRequest>(peer, deadline.Token);
                        await Protocol.WriteAsync(peer, new HostResponse(Protocol.Version, request.RequestId, "Succeeded", null, "恢复已受理"), deadline.Token);
                        return request;
                    }
                    var read = ReadAsync();
                    Click(main, "HomeHomeworkButton");
                    PumpUntil(() => read.IsCompleted && !(bool)typeof(MainWindow).GetField("_homeHomeworkBusy", flags)!.GetValue(main)!, "homework recovery command did not settle");
                    var received = read.GetAwaiter().GetResult();
                    Assert(received.Capability == capability && received.StudyHide == new StudyHideCommand(expected.Display!.InstanceId, expected.Version!, expected.Window!),
                        "homework recovery did not use the current display identity and time window");
                    Assert(!homeworkButton.IsEnabled, "accepted recovery left the old button enabled without fresh status");
                }
            });
        }
        finally
        {
            main.Close(); client.GetType().GetMethod("Detach")!.Invoke(client, null);
            string preferences = StartupPreferencesStore.PathFor(pipe);
            foreach (string suffix in new[] { ".recording-preview.json", ".recording-plans.json" })
            {
                string path = preferences.Replace(".startup.json", suffix, StringComparison.Ordinal);
                if (File.Exists(path)) File.Delete(path);
            }
        }
    }
}

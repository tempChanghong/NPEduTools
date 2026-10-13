using System.IO;
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
                    Reachable(main, Element<Button>("WorkspacePrimary")); Reachable(main, Element<Button>("RecordingHomeButton"));
                    Reachable(main, Element<TextBlock>("StudyHomeNext")); Reachable(main, Element<TextBlock>("HomeRecordingNext"));
                    Snapshot(main, $"home-first-screen-{size.Width}-{size.Height}.png");
                }
                Checks.Add("Actual homepage: study and recording remain independent, source-specific next items, both task controls and upcoming summaries fit first screen at 2K/200-percent-sized and compact logical viewports");
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
                Checks.Add("Homepage authority and routing: clock/pipe loss withdraws cached time; fresh snapshot recovers; Windows study source remains separate; manual reservation is explicit; saved result opens records without starting capture");
                Invoke("ShowClassroomModeState", (object?)null);
                Assert(Element<Expander>("HomeClassroomModeDetails").IsExpanded, "mode warning was hidden inside closed details");
                Element<Expander>("HomeClassroomModeDetails").IsExpanded = false;
                Invoke("ShowClassroomModeState", (object?)null);
                Assert(!Element<Expander>("HomeClassroomModeDetails").IsExpanded, "unchanged status forcibly reopened dismissed details");
                main.Close();
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

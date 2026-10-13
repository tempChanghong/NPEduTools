using System.Collections.Concurrent;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using NPEduTools.App;
using NPEduTools.Contracts;

internal static partial class Program
{
    private static void RunConnectionWorkspaceChecks()
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
        var previous = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Application.Current.Dispatcher));
        try
        {
            var exam = new ExamAwareWindow("NPEduTools.Test.connection-layout." + Guid.NewGuid().ToString("N"));
            Exercise(exam, window => {
                ShowExamAware(exam, ReadyExamAware()); window.UpdateLayout();
                var config = (Expander)window.FindName("ConnectionConfiguration");
                Assert(!config.IsExpanded, "configured connected ExamAware opens setup before daily actions");
                Reachable(window, (FrameworkElement)Find(window, "ExamAwareStart"));
                Snapshot(window, "examaware-ready.png");
                Click(window, "ExamAwareConfigure", true);
                ((TextBox)window.FindName("Executable")).Text = "edited-fixture.exe";
                ShowExamAware(exam, ReadyExamAware() with { Revision = 8 }); window.UpdateLayout();
                Assert(config.IsExpanded && ((TextBox)window.FindName("Executable")).Text == "edited-fixture.exe", "polling folded ExamAware configuration or replaced the draft");
                ShowExamAware(exam, null);
                Assert(!((WrapPanel)window.FindName("RuntimeActions")).IsEnabled && !Button(window, "QuitButton").IsEnabled,
                    "new ExamAware actions bypass unknown-state gating");
                Assert(config.IsExpanded && ((TextBlock)window.FindName("NextActionText")).Text.Contains("等待后台"), "disconnect hid repair or retained ready guidance");
                ((ScrollViewer)window.Content).ScrollToTop(); Snapshot(window, "examaware-unknown.png");
                exam.Shutdown();
            });
            var sec = new SecRandomWindow("NPEduTools.Test.connection-layout." + Guid.NewGuid().ToString("N"));
            var renderSec = typeof(SecRandomWindow).GetMethod("Render", flags)!;
            var ready = new SecRandomState("fixture.exe", 1, "Ready", DateTimeOffset.Now, null);
            Exercise(sec, window => {
                renderSec.Invoke(sec, [ready]); window.UpdateLayout();
                var config = (Expander)window.FindName("ProgramConfiguration");
                Assert(!config.IsExpanded, "configured SecRandom opens path before daily actions");
                Reachable(window, Button(window, "OpenButton")); Snapshot(window, "secrandom-ready.png");
                Click(window, "SecRandomConfigure", true);
                ((TextBox)window.FindName("Executable")).Text = "edited-fixture.exe";
                renderSec.Invoke(sec, [ready with { Revision = 2 }]);
                Assert(config.IsExpanded && ((TextBox)window.FindName("Executable")).Text == "edited-fixture.exe", "SecRandom polling folded setup or replaced path draft");
                renderSec.Invoke(sec, [ready with { Operation = new(Guid.NewGuid(), "quick-draw", "Unknown", "隔离待核实", DateTimeOffset.Now) }]);
                Assert(!Button(window, "DrawButton").IsEnabled && ((TextBlock)window.FindName("NextActionText")).Text.Contains("不要重复"), "unknown draw result allows a repeat or hides recovery guidance");
                renderSec.Invoke(sec, [null]);
                Assert(!Button(window, "OpenButton").IsEnabled && !Button(window, "DrawButton").IsEnabled, "SecRandom lost-state actions remain enabled");
                sec.Shutdown();
            });
            var hidden = new SecRandomWindow("NPEduTools.Test.guidance-layout." + Guid.NewGuid().ToString("N"));
            try
            {
                var guidance = typeof(SecRandomWindow).GetMethod("ShowGuidance", flags)!;
                guidance.Invoke(hidden, ["隔离恢复提示"]);
                Assert(hidden.IsVisible, "guidance no longer brings a hidden point-name window back");
                guidance.Invoke(hidden, ["隔离再次提示"]);
                Assert(hidden.IsVisible && ((TextBlock)hidden.FindName("Message")).Text == "隔离再次提示", "already visible guidance did not return/update normally");
                hidden.Hide(); guidance.Invoke(hidden, ["隔离隐藏后恢复"]);
                Assert(hidden.IsVisible, "hiding the point-name page prevents later recovery guidance");
            }
            finally { hidden.Shutdown(); }
            Checks.Add("Real software pages put daily actions first; configuration shortcuts remain reachable, drafts and expansion survive polling, unknown/backend-loss states gate all operations without any real launch or draw");

            var setup = SyntheticClassroomSetup((capability, _) => Task.FromResult(ClassroomSetupResponse(capability) with { ExamAware = capability == "examaware.status" ? ReadyExamAware() with { Packaged = true } : null }));
            var mode = ClassroomWindow("NPEduTools.Test.mode-layout." + Guid.NewGuid().ToString("N"), setup);
            Exercise(mode, window => {
                ShowClassroom(mode, new(3, "Daily", Message: "隔离日常环境已核实。", Actual: new("fixture-ci.exe", 1, true, "fixture-ea.exe", 1, false), CheckedAt: DateTimeOffset.Now, MatchesMode: true));
                ((FrameworkElement)window.Content).LayoutTransform = new ScaleTransform(1, 1);
                window.Width = 800; window.Height = 740; window.UpdateLayout();
                Reachable(window, Button(window, "DailyButton")); Reachable(window, Button(window, "ExamButton"));
                Assert(((TextBlock)window.FindName("ActualClassIslandText")).Text.Contains("开启") && ((TextBlock)window.FindName("ActualExamAwareText")).Text.Contains("关闭"), "actual results replaced by the target preview");
                Snapshot(window, "classroom-wide.png");
                typeof(ClassroomModeWindow).GetField("_sending", flags)!.SetValue(mode, true);
                ShowClassroom(mode, new(3, "Daily"));
                Assert(((TextBlock)window.FindName("RuntimeProgressText")).Text.Contains("尚未确认") && !Button(window, "ExamButton").IsEnabled,
                    "pending local request displays an execution result or permits another switch");
                typeof(ClassroomModeWindow).GetField("_sending", flags)!.SetValue(mode, false);
                ShowClassroom(mode, new(3, "Daily"));
                Check(window, "SwitchRunning").IsChecked = true;
                Assert(((TextBlock)window.FindName("DailyRuntimePreview")).Text.Contains("正常退出") && ((TextBlock)window.FindName("ExamRuntimePreview")).Text.Contains("准备好"), "running-switch choice does not update both previews");
                window.Width = 560; window.Height = 500; window.UpdateLayout();
                Assert(Grid.GetRow((Border)window.FindName("ExamCard")) == 1, "mode choices squeeze two columns in a narrow viewport");
                Button(window, "ExamButton").BringIntoView(); window.UpdateLayout(); Reachable(window, Button(window, "ExamButton")); Snapshot(window, "classroom-narrow.png");
                typeof(ClassroomModeWindow).GetMethod("Disconnected", flags)!.Invoke(mode, ["隔离失联"]);
                Assert(((TextBlock)window.FindName("ActualClassIslandText")).Text.Contains("尚未核实") && ((TextBlock)window.FindName("RuntimeProgressText")).Text.Contains("未知") && !Button(window, "ExamButton").IsEnabled,
                    "disconnection retains newly added actual-result facts or enables switching");
                mode.Shutdown();
            });
            Checks.Add("Mode previews reflect the immediate-switch checkbox without issuing commands; actual results stay independent, narrow choices stack and disconnection withdraws new result fields");
            RunConnectionMainLayoutCheck();
        }
        finally { SynchronizationContext.SetSynchronizationContext(previous); }
    }
    private static void RunConnectionMainLayoutCheck()
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
        string pipe = "NPEduTools.Test.connection-main." + Guid.NewGuid().ToString("N");
        string path = System.IO.Path.GetTempFileName();
        using var stop = new CancellationTokenSource();
        var requests = new ConcurrentQueue<HostRequest>();
        long sequence = 0; Guid connection = Guid.NewGuid(), bridge = Guid.NewGuid();
        var schoolElapsed = System.Diagnostics.Stopwatch.StartNew();
        var serving = Task.Run(() => WorkspaceProxy(pipe, request => {
            requests.Enqueue(request);
            return Task.FromResult(new HostResponse(Protocol.Version, request.RequestId, "Succeeded", null, "隔离只读状态",
                Launch: request.Capability == "classisland.config.get" ? new(new(1, path), null, null) : null,
                SchoolClock: request.Capability == "classisland.school-clock" ? new(connection, bridge, Interlocked.Increment(ref sequence), 1,
                    new DateTimeOffset(2026, 10, 13, 19, 15, 0, TimeSpan.Zero).Add(schoolElapsed.Elapsed), 0, "Advancing", "隔离学校时间") : null));
        }, stop.Token));
        var main = IsolatedMainWindow(pipe);
        typeof(MainWindow).GetField("_adminBusy", flags)!.SetValue(main, true); // No real administrator task inspection.
        void Invoke(string name, params object?[] args) => typeof(MainWindow).GetMethod(name, flags)!.Invoke(main, args);
        try
        {
            Exercise(main, window => {
                Invoke("ShowConnectionSettings");
                PumpUntil(() => ((TextBlock)window.FindName("ClassIslandProgramStatus")).Text.Contains("文件可访问"), "connection summary did not use the fresh fake Host configuration");
                Assert(((TextBlock)window.FindName("ClassIslandClockText")).Text.StartsWith("2026-10-13 19:15:"), "fresh valid school-clock fixture was not displayed alongside program status");
                var config = (Expander)window.FindName("ClassIslandConfiguration");
                Assert(!config.IsExpanded, "known accessible ClassIsland path does not collapse its initial configuration");
                Invoke("ShowLaunchData", new HostResponse(Protocol.Version, Guid.NewGuid(), "Succeeded", null, "隔离配置", Launch: new(new(1, path), null, null)));
                Invoke("ShowClassroomModeState", new ClassroomModeState(2, "Daily"));
                window.UpdateLayout(); Snapshot(window, "connections-ready.png");
                Invoke("ShowClassIslandPathSettings");
                PumpUntil(() => ((TextBox)window.FindName("ExecutablePathBox")).IsKeyboardFocused, "path shortcut did not reveal/focus the folded field");
                ((TextBox)window.FindName("ExecutablePathBox")).Text = "edited-fixture.exe";
                var refresh = (Task)typeof(MainWindow).GetMethod("RefreshClassIslandBridgeAsync", flags)!.Invoke(main, null)!;
                PumpUntil(() => refresh.IsCompleted, "read-only refresh did not settle"); refresh.GetAwaiter().GetResult();
                Assert(config.IsExpanded && ((TextBox)window.FindName("ExecutablePathBox")).Text == "edited-fixture.exe", "ClassIsland refresh folded the explicitly requested config or replaced draft");
                Invoke("SelectSettingsCategory", "General"); Invoke("SelectSettingsCategory", "Connections");
                Assert(((TextBox)window.FindName("ExecutablePathBox")).Text == "edited-fixture.exe", "settings tab switch lost the path draft");
                config.IsExpanded = false;
                window.MinWidth = window.MinHeight = 0; window.Width = 1280; window.Height = 680;
                ((FrameworkElement)window.Content).LayoutTransform = new ScaleTransform(.8, .8);
                ((ScrollViewer)window.FindName("SettingsScroll")).ScrollToTop(); window.UpdateLayout();
                Reachable(window, (FrameworkElement)window.FindName("ClassIslandClockText")); Snapshot(window, "connections-simulated-200.png");
                Assert(requests.All(r => r.Capability is "classisland.config.get" or "classisland.school-clock"), "view setup or navigation issued a mutation or launched software");
                window.Close();
            });
            Checks.Add("Actual settings page and fake Host: fresh ClassIsland summary, explicit path shortcut, tab changes and polling preserve drafts/expansion; school-time summary fits simulated 200% work area and all requests are read-only");
        }
        finally { main.Close(); stop.Cancel(); try { serving.GetAwaiter().GetResult(); } catch (OperationCanceledException) { } System.IO.File.Delete(path); }
    }
}

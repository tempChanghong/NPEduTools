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
                var config = (FrameworkElement)window.FindName("ConnectionSteps");
                Assert(!config.IsVisible, "configured connected ExamAware opens setup before daily actions");
                Reachable(window, (FrameworkElement)Find(window, "ExamAwareStart"));
                Snapshot(window, "examaware-ready.png");
                Click(window, "ExamAwareConfigure", true);
                ((TextBox)window.FindName("Executable")).Text = "edited-fixture.exe";
                ShowExamAware(exam, ReadyExamAware() with { Revision = 8 }); window.UpdateLayout();
                Assert(config.IsVisible && ((TextBox)window.FindName("Executable")).Text == "edited-fixture.exe", "polling folded ExamAware configuration or replaced the draft");
                ShowExamAware(exam, null);
                Assert(!((WrapPanel)window.FindName("RuntimeActions")).IsEnabled && !Button(window, "QuitButton").IsEnabled,
                    "new ExamAware actions bypass unknown-state gating");
                Assert(!config.IsVisible && ((TextBlock)window.FindName("NextActionText")).Text.Contains("等待 NPEduTools 后台"), "unknown state exposes repair commands or retains ready guidance");
                ((ScrollViewer)window.Content).ScrollToTop(); Snapshot(window, "examaware-unknown.png");
                ShowExamAware(exam, ReadyExamAware() with { Revision = 8 });
                Assert(config.IsVisible && ((TextBox)window.FindName("Executable")).Text == "edited-fixture.exe", "recovering the backend lost the original path editor or draft");
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
                Assert(!Button(window, "DrawButton").IsEnabled && ((TextBlock)window.FindName("OperationText")).Text.Contains("不会自动重抽") &&
                    Button(window, "AcknowledgeButton").IsVisible, "unknown draw result allows a repeat or hides recovery guidance");
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
            RunClassIslandFirstPathCheck();
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
        int bridgeFault = 0;
        var schoolElapsed = System.Diagnostics.Stopwatch.StartNew();
        var serving = Task.Run(() => WorkspaceProxy(pipe, request => {
            requests.Enqueue(request);
            return Task.FromResult(new HostResponse(Protocol.Version, request.RequestId, "Succeeded", null, "隔离只读状态",
                Launch: request.Capability == "classisland.config.get" ? new(new(1, path), null, null) : null,
                SchoolClock: request.Capability == "classisland.school-clock" && Volatile.Read(ref bridgeFault) == 0 ? new(connection, bridge, Interlocked.Increment(ref sequence), 1,
                    new DateTimeOffset(2026, 10, 13, 19, 15, 0, TimeSpan.Zero).Add(schoolElapsed.Elapsed), 0, "Advancing", "隔离学校时间") : null));
        }, stop.Token));
        var main = IsolatedMainWindow(pipe);
        typeof(MainWindow).GetField("_adminBusy", flags)!.SetValue(main, true); // No real administrator task inspection.
        void Invoke(string name, params object?[] args) => typeof(MainWindow).GetMethod(name, flags)!.Invoke(main, args);
        try
        {
            Exercise(main, window => {
                var scroll = (ScrollViewer)window.FindName("SettingsScroll");
                var course = (TextBlock)window.FindName("ClassIslandLessonSummaryText");
                var time = (TextBlock)window.FindName("ClassIslandClockText");
                var timeDetail = (TextBlock)window.FindName("ClassIslandClockDetail");
                var bridgeSummary = (TextBlock)window.FindName("ClassIslandBridgeSummaryText");
                var program = (TextBlock)window.FindName("ClassIslandProgramStatus");
                var status = (FrameworkElement)window.FindName("ClassIslandStatusPane");
                var overview = (FrameworkElement)window.FindName("ClassIslandOverviewGrid");
                var refreshButton = Button(window, "ClassIslandRefreshButton");
                var pathField = (TextBox)window.FindName("ExecutablePathBox");
                void InspectViewport(params FrameworkElement[] items)
                {
                    var viewport = (ScrollContentPresenter)scroll.Template.FindName("PART_ScrollContentPresenter", scroll);
                    foreach (var item in items)
                    {
                        var position = item.TransformToAncestor(viewport).TransformBounds(new Rect(item.RenderSize));
                        Assert(item.IsVisible && position.Left >= -1 && position.Top >= -1 &&
                            position.Right <= viewport.ActualWidth + 1 && position.Bottom <= viewport.ActualHeight + 1,
                            $"{item.Name} is outside the first viewport ({position}, viewport {viewport.ActualWidth}x{viewport.ActualHeight})");
                    }
                }
                void Capture(int width, int height, string file)
                {
                    window.Width = width; window.Height = height;
                    scroll.ScrollToTop(); window.UpdateLayout();
                    Assert(window.ActualWidth == width && window.ActualHeight == height, "fixture did not reach actual requested size");
                    Assert(Grid.GetColumn(status) == (width == 1040 ? 1 : 0) && Grid.GetRow(status) == (width == 1040 ? 0 : 1),
                        $"ClassIsland overview at {width} had content width {overview.ActualWidth} and cell {Grid.GetColumn(status)},{Grid.GetRow(status)}");
                    InspectViewport(time, timeDetail, program, bridgeSummary, course, refreshButton);
                    Snapshot(window, file);
                }
                void RefreshBridge()
                {
                    var refresh = (Task)typeof(MainWindow).GetMethod("RefreshClassIslandBridgeAsync", flags)!.Invoke(main, null)!;
                    PumpUntil(() => refresh.IsCompleted, "read-only refresh did not settle");
                    refresh.GetAwaiter().GetResult();
                }
                Invoke("ShowConnectionSettings");
                PumpUntil(() => program.Text == "文件可访问" && time.Text.StartsWith("2026-10-13 19:15:"), "separate path and school-time results did not arrive");
                Assert(course.Text == "未核实", "a school-time sample was incorrectly used as proof of the current timetable");
                RefreshBridge();
                long secondSample = schoolElapsed.ElapsedMilliseconds;
                PumpUntil(() => schoolElapsed.ElapsedMilliseconds >= secondSample + 550, "clock sample interval did not elapse");
                RefreshBridge();
                Assert(bridgeSummary.Text == "已同步" && course.Text == "未核实", "confirmed time bridge wrongly implies enabled timetable");
                var config = (Expander)window.FindName("ClassIslandConfiguration");
                Assert(!config.IsExpanded, "known accessible ClassIsland path does not collapse its initial configuration");
                Invoke("ShowLaunchData", new HostResponse(Protocol.Version, Guid.NewGuid(), "Succeeded", null, "隔离配置", Launch: new(new(1, path), null, null)));
                Invoke("ShowClassroomModeState", new ClassroomModeState(2, "Daily"));
                Capture(1040, 760, "classisland-time-and-timetable-1040-760.png");
                Capture(840, 600, "classisland-time-and-timetable-840-600.png");
                var model = (StatusViewModel)typeof(MainWindow).GetField("_model", flags)!.GetValue(main)!;
                var now = DateTimeOffset.UtcNow;
                model.Apply(new(Protocol.Version, Guid.NewGuid(), Guid.NewGuid(), 1, connection, now, "Succeeded", null, "隔离课表",
                    new(now, now, "OnClass", "语文", true, true, true, 0, new Dictionary<string, long>())));
                Assert(course.Text == "课表已启用", "live course observation was not reflected in its dedicated summary");
                Invoke("ShowClassIslandPathSettings");
                PumpUntil(() => pathField.IsKeyboardFocused, "path shortcut did not reveal/focus the folded field");
                pathField.Text = "edited-fixture.exe";
                Volatile.Write(ref bridgeFault, 1);
                RefreshBridge();
                PumpUntil(() => bridgeSummary.Text == "暂不可用", "bridge loss did not clear the prior observed clock");
                Assert(course.Text == "课表已启用" && program.Text == "文件可访问", "bridge loss erased the separately confirmed path or course status");
                model.Disconnected("隔离课程接口断开");
                Assert(course.Text == "未核实", "course summary retained old ready state after course disconnect");
                Assert(config.IsExpanded && pathField.Text == "edited-fixture.exe", "ClassIsland refresh folded the explicitly requested config or replaced draft");
                Capture(840, 600, "classisland-offline-with-path-draft-840-600.png");
                Invoke("SelectSettingsCategory", "General"); Invoke("SelectSettingsCategory", "Connections");
                typeof(MainWindow).GetField("_configurationLoaded", flags)!.SetValue(main, false);
                Invoke("ShowLaunchData", new HostResponse(Protocol.Version, Guid.NewGuid(), "Succeeded", null, "隔离重连配置",
                    Launch: new(new(2, path), null, null)));
                Assert(pathField.Text == "edited-fixture.exe" && config.IsExpanded &&
                    (long?)typeof(MainWindow).GetField("_classIslandPathDraftRevision", flags)!.GetValue(main) == 1,
                    "navigation or a reloaded Host config discarded or silently rebased the original draft");
                var bridgeBusy = typeof(MainWindow).GetField("_bridgeInspectionBusy", flags)!;
                PumpUntil(() => !(bool)bridgeBusy.GetValue(main)!, "opening software connections did not finish its read");
                Volatile.Write(ref bridgeFault, 0);
                Click(window, "ClassIslandRefreshButton");
                PumpUntil(() => time.Text.StartsWith("2026-10-13 19:15:") && !(bool)bridgeBusy.GetValue(main)!,
                    "retrying with the visible action did not restore the school-time display");
                Assert(pathField.Text == "edited-fixture.exe" && course.Text == "未核实" && Window.GetWindow(refreshButton) == window,
                    "rechecking the bridge disturbed the path draft, substituted timetable evidence, or left the main window");
                config.IsExpanded = false;
                window.MinWidth = window.MinHeight = 0; window.Width = 1280; window.Height = 680;
                ((FrameworkElement)window.Content).LayoutTransform = new ScaleTransform(.8, .8);
                scroll.ScrollToTop(); window.UpdateLayout();
                Reachable(window, (FrameworkElement)window.FindName("ClassIslandClockText")); Snapshot(window, "connections-simulated-200.png");
                Assert(requests.All(r => r.Capability is "classisland.config.get" or "classisland.school-clock"), "view setup or navigation issued a mutation or launched software");
                window.Close();
            });
            Checks.Add("Actual settings page and fake Host: fresh ClassIsland summary, explicit path shortcut, tab changes and polling preserve drafts/expansion; school-time summary fits simulated 200% work area and all requests are read-only");
            Checks.Add("Native ClassIsland overview at 1040x760 and 840x600: clock, program and course evidence stay separate; the current action fits at both widths while an interrupted, unsaved path remains intact");
        }
        finally { main.Close(); stop.Cancel(); try { serving.GetAwaiter().GetResult(); } catch (OperationCanceledException) { } System.IO.File.Delete(path); }
    }

    private static void RunClassIslandFirstPathCheck()
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
        string pipe = "NPEduTools.Test.classisland-first-path." + Guid.NewGuid().ToString("N");
        using var stop = new CancellationTokenSource();
        var requests = new ConcurrentQueue<string>();
        var server = Task.Run(() => WorkspaceProxy(pipe, request =>
        {
            requests.Enqueue(request.Capability);
            return Task.FromResult(new HostResponse(Protocol.Version, request.RequestId, "Succeeded", null, "隔离首次设置",
                Launch: request.Capability == "classisland.config.get" ? new(new(0, null), null) : null));
        }, stop.Token));
        var main = IsolatedMainWindow(pipe);
        typeof(MainWindow).GetField("_adminBusy", flags)!.SetValue(main, true);
        try
        {
            Exercise(main, window =>
            {
                window.Width = 840; window.Height = 600;
                typeof(MainWindow).GetMethod("ShowConnectionSettings", flags)!.Invoke(main, null);
                PumpUntil(() => ((TextBlock)window.FindName("ClassIslandProgramStatus")).Text == "尚未保存",
                    "missing program state did not settle");
                var path = (TextBox)window.FindName("ExecutablePathBox");
                var choose = (Button)Find(window, "BrowseExecutable");
                var save = (Button)Find(window, "SaveExecutable");
                window.UpdateLayout();
                var scroll = (ScrollViewer)window.FindName("SettingsScroll");
                var viewport = (ScrollContentPresenter)scroll.Template.FindName("PART_ScrollContentPresenter", scroll);
                foreach (var control in new FrameworkElement[] { path, choose, save })
                {
                    var position = control.TransformToAncestor(viewport).TransformBounds(new Rect(control.RenderSize));
                    Assert(control.IsVisible && position.Top >= -1 && position.Bottom <= viewport.ActualHeight + 1,
                        $"first-time ClassIsland control {control.Name} is clipped ({position}, viewport {viewport.ActualHeight})");
                }
                Assert(string.IsNullOrEmpty(path.Text) &&
                    ((TextBlock)window.FindName("ConfigurationMessage")).Text.Contains("请选择本机 ClassIsland 程序") &&
                    requests.All(r => r is "classisland.config.get" or "classisland.school-clock"),
                    "first setup filled or changed a local program path");
                Snapshot(window, "classisland-first-setup-840-600.png");
                window.Close();
            });
            Checks.Add("ClassIsland first setup at 840x600 opens the path editor with both choice and save fully visible, and submits no commands");
        }
        finally { main.Close(); stop.Cancel(); try { server.GetAwaiter().GetResult(); } catch (OperationCanceledException) { } }
    }
}

using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using NPEduTools.App;
using NPEduTools.Contracts;

internal static partial class Program
{
    private static void RunQuickWorkspaceChecks()
    {
        RunQuickPointerChecks();
        const BindingFlags flags = BindingFlags.NonPublic | BindingFlags.Instance;
        string pipe = "NPEduTools.Test.quick-workspace." + Guid.NewGuid().ToString("N");
        int studies = 0, pauses = 0;
        var quick = new QuickAccessWindow(pipe, () => { }, () => { }, () => { }, () => { }, _ => { }, () => { }, () => { });
        var main = IsolatedMainWindow(pipe);
        T Control<T>(string name) where T : FrameworkElement => (T)quick.FindName(name);
        void Invoke(string name, params object?[] args) => typeof(QuickAccessWindow).GetMethod(name, flags)!.Invoke(quick, args);
        void Field(string name, object value) => typeof(QuickAccessWindow).GetField(name, flags)!.SetValue(quick, value);
        var panel = Control<Border>("PanelSurface");
        try
        {
            quick.Show(); quick.UpdateLayout();
            quick.SetStudyAction(() => studies++);
            quick.SetRecordingActions(() => { }, (action, _, _) => { if (action == "pause") pauses++; }, () => { }, () => { });
            var active = new RecordingState("Recording", "录制进行中", Seconds: 120, Control: new("Manual", Guid.NewGuid(), "", 0, 0));
            quick.UpdateStudy("当前没有自习任务", "可开始手动自习", "下次学校自习 · 时间待确认", "开始自习", false);
            quick.UpdateRecording(active);
            quick.Update(new TouchAssistState(true, false, false, "放映触摸辅助运行中"), "PPT 辅助已开启", true, "就绪", true);
            quick.OpenPanel(); quick.UpdateLayout();
            var pptPause = Control<Button>("PauseButton");
            var pptBounds = pptPause.TransformToAncestor(panel).TransformBounds(new Rect(pptPause.RenderSize));
            Assert(pptPause.IsVisible && pptBounds.Bottom <= panel.ActualHeight - 45,
                "the PPT pause action was below the first visible classroom-tool screen");
            Assert(Control<StackPanel>("TaskStack").Children[0] == Control<Border>("RecordingCard"), "active recording is not first");
            Reachable(quick, Control<Button>("RecordingPause")); Reachable(quick, Control<Button>("RecordingStop"));
            var original = Control<Button>("RecordingPause").TranslatePoint(new Point(), quick);
            quick.UpdateStudy("学校自习进行中", "新鲜采样已确认", "", "查看学校自习", true); quick.UpdateLayout();
            Assert(Control<Button>("RecordingPause").TranslatePoint(new Point(), quick) == original, "status update moved controls under pointer");
            Click(quick, "RecordingPause"); Assert(pauses == 1, "recording action was lost");
            quick.Collapse(false); quick.OpenPanel(); quick.UpdateLayout();
            Assert(Control<StackPanel>("TaskStack").Children[0] == Control<Border>("StudyCard"), "study priority not applied after reopening");
            Reachable(quick, Control<Button>("StudyButton"));
            Snapshot(quick, "quick-two-tasks.png");
            Click(quick, "StudyButton"); Assert(studies == 1 && panel.Visibility == Visibility.Collapsed, "study navigation leaves panel open or invokes twice");
            quick.UpdateRecording(active, false);
            Assert(Control<WrapPanel>("RecordingControls").Visibility == Visibility.Collapsed && !Control<Button>("RecordingPause").IsEnabled,
                "unknown recording exposes live controls");
            Click(quick, "RecordingPause"); Assert(pauses == 1, "late queued click controls unconfirmed recording");
            quick.UpdateRecording(active with { Phase = "Saving" });
            Click(quick, "RecordingPause"); Assert(pauses == 1, "busy recording accepts a queued pause");
            quick.UpdateRecording(active); quick.UpdateRecording(active with { Phase = "Paused" });
            Assert(Control<Button>("RecordingPause").Content.ToString() == "继续", "pause/resume label does not recover");
            Checks.Add("Current tasks precede tools, open controls never move under input, study navigation collapses, recording pause/recovery remain independent");

            typeof(MainWindow).GetField("_quick", flags)!.SetValue(main, quick);
            var school = StudyFixture("Active", 10) with { Execution = new("Schedule", "Active", Guid.NewGuid(), "", true, null, null, true) };
            void MainInvoke(string method, object? state) => typeof(MainWindow).GetMethod(method, flags)!.Invoke(main, [state]);
            MainInvoke("UpdateStudyHome", school);
            Assert(Control<TextBlock>("StudyStatus").Text.Contains("新鲜采样"), "sidebar does not use homepage's verified study facts");
            MainInvoke("UpdateStudyHome", school with { ClockReady = false });
            Assert(Control<TextBlock>("StudyTitle").Text.Contains("待确认") && !Control<TextBlock>("StudyStatus").Text.Contains("新鲜采样"), "clock loss retains healthy study state");
            MainInvoke("UpdateStudyHome", null); MainInvoke("UpdateWorkspaceManual", null);
            Assert(Control<TextBlock>("StudyTitle").Text.Contains("待确认"), "disconnect claims no study task");
            quick.Update(null, "PPT 后台待确认", false, "ClassIsland 待确认", false);
            Assert(Control<Button>("RailTools").ToolTip.ToString() == "课堂工具" && Control<System.Windows.Shapes.Ellipse>("RailStatusDot").ToolTip.ToString()!.Contains("PPT"),
                "PPT status masquerades as overall health");
            Checks.Add("Main and sidebar share study presentation; clock/pipe loss withdraws live assertions; PPT dot is scoped to PPT only");

            foreach (double density in new[] { .8, .9, 1d })
            {
                quick.SetDensity(density);
                quick.Collapse(false); quick.SetCompact(false); quick.UpdateLayout();
                foreach (string name in new[] { "EdgeHandle", "RailFold", "RailTools", "RailShortcuts", "RailSecRandom", "RailSettings" })
                    CheckTouchTarget(name);
                quick.OpenPanel(); quick.UpdateLayout();
                foreach (string name in new[] { "EdgeHandle", "CollapseButton", "ToolsTab", "ShortcutsTab", "LayoutButton" })
                    CheckTouchTarget(name);
                Click(quick, "LayoutButton"); quick.UpdateLayout();
                foreach (string name in new[] { "LayoutBack", "LayoutLeft", "LayoutRight", "LayoutShape", "LayoutMove" })
                    CheckTouchTarget(name);
                if (density == .8) Snapshot(quick, "screen-edge-layout-options.png");
            }
            quick.Collapse(false);
            var handle = quick.PointToScreen(QuickHandleCenter(quick));
            Invoke("BeginHandlePress", handle); Invoke("CancelHandlePress"); Invoke("FinishHandlePress");
            Assert(panel.Visibility == Visibility.Collapsed, "cancelled capture executes click");
            Invoke("BeginHandlePress", handle); Invoke("FinishHandlePress");
            Invoke("BeginHandlePress", handle); Invoke("CancelHandlePress");
            var settled = System.Windows.Threading.Dispatcher.CurrentDispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            PumpUntil(() => settled.Task.IsCompleted, "queued tap cancellation did not settle");
            Assert(panel.Visibility == Visibility.Collapsed, "earlier queued tap survived newer cancelled gesture");
            Checks.Add("Actual transformed rail, tabs and layout buttons stay at least 44 DIP at each supported density; old or cancelled taps cannot reopen a panel");

            void CheckTouchTarget(string name)
            {
                var button = Control<Button>(name);
                var start = button.TranslatePoint(new Point(), quick);
                var end = button.TranslatePoint(new Point(button.ActualWidth, button.ActualHeight), quick);
                Assert(button.IsVisible && end.X - start.X >= 43.5 && end.Y - start.Y >= 43.5 &&
                    start.X >= 0 && start.Y >= 0 && end.X <= quick.ActualWidth + 1 && end.Y <= quick.ActualHeight + 1,
                    $"touch target is too small or clipped at the selected density: {name}, bounds={start} to {end}");
            }

            quick.SetCompact(true); quick.SetSide(System.Windows.Forms.Cursor.Position.X > System.Windows.Forms.Screen.PrimaryScreen!.Bounds.Width / 2); quick.UpdateLayout();
            Field("_handleActivity", Stopwatch.GetTimestamp() - 5 * Stopwatch.Frequency);
            PumpUntil(() => Control<Border>("Rail").Opacity == .6, "idle timer did not fade compact handle");
            Assert(Control<Border>("Rail").Opacity == .6, "idle compact handle is not translucent");
            Snapshot(quick, "quick-idle-handle.png");
            Invoke("BeginHandlePress", quick.PointToScreen(new Point(30, 30))); Assert(Control<Border>("Rail").Opacity == 1, "input starts with faded handle"); Invoke("CancelHandlePress");
            quick.SetAutoHidden(true); quick.UpdateStudy("学校自习进行中", "新鲜状态", "", "查看学校自习", true); quick.UpdateRecording(active);
            Assert(!quick.IsVisible, "background refresh restored rail over fullscreen");
            quick.SetAutoHidden(false); quick.UpdateLayout();
            Assert(Control<Button>("RailTools").Visibility == Visibility.Collapsed && Control<Border>("Rail").Opacity == 1, "full-hide lost compact mode or restored invisibly");
            quick.SetCompact(false); Field("_handleActivity", Stopwatch.GetTimestamp() - 5 * Stopwatch.Frequency); Invoke("RefreshHandleOpacity");
            Assert(Control<Border>("Rail").Opacity == 1, "full rail fades functional buttons");
            Checks.Add("Idle compact handle alone fades to 60 percent; input restores opacity; fullscreen hides completely, status updates cannot revive it, compact preference survives");

            quick.SetShortcuts(Enumerable.Range(0, 8).Select(i => new ShortcutEntry(Guid.NewGuid(), "课堂课件与教学资料 " + i, "file", "isolated.txt")).ToArray());
            quick.SetDensity(.8); quick.OpenPanel(); Click(quick, "ShortcutsTab"); quick.UpdateLayout(); Invoke("RefreshShortcutColumns");
            var grid = FindQuickGrid(Control<ItemsControl>("ShortcutItems"));
            Assert(grid.Columns == 2, "normal shortcut viewport lost two columns"); Snapshot(quick, "quick-shortcuts.png");
            Control<ItemsControl>("ShortcutItems").Width = 250; quick.UpdateLayout(); Invoke("RefreshShortcutColumns");
            Assert(grid.Columns == 1, "narrow shortcuts do not switch to one column");
            Control<ItemsControl>("ShortcutItems").Width = double.NaN;
            Click(quick, "ToolsTab"); quick.UpdateLayout();
            Reachable(quick, Control<Button>("StudyButton"));
            Control<Button>("ClassIslandButton").BringIntoView(); quick.UpdateLayout(); Reachable(quick, Control<Button>("ClassIslandButton"));
            Snapshot(quick, "quick-compact-tools.png");
            Checks.Add("Compact-density WPF rendering: first task and bottom tools reachable; shortcuts retain labels and switch between two/one columns; no Windows DPI change or hardware use");
        }
        finally
        {
            main.Close(); quick.Shutdown();
            string preference = (string)typeof(QuickAccessWindow).GetField("_settingsPath", flags)!.GetValue(quick)!;
            File.Delete(preference); File.Delete(preference + ".tmp");
        }
    }
    private static UniformGrid FindQuickGrid(DependencyObject root)
    {
        if (root is UniformGrid grid) return grid;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            try { return FindQuickGrid(child); } catch (InvalidOperationException) { }
        }
        throw new InvalidOperationException("shortcut grid not generated");
    }
}

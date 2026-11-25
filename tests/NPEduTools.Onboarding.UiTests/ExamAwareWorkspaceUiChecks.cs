using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using NPEduTools.App;
using NPEduTools.Contracts;

internal static partial class Program
{
    private static void RunEmbeddedExamAwareWorkspaceCheck()
    {
        string pipe = "NPEduTools.Test.examaware-workspace." + Guid.NewGuid().ToString("N");
        using var stop = new CancellationTokenSource();
        var saveReceived = new TaskCompletionSource<HostRequest>(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseSave = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var prepared = ReadyExamAware("月考安排");
        var state = prepared with { PreparedPlan = null };
        int saves = 0, starts = 0;
        var server = Task.Run(() => WorkspaceProxy(pipe, async request =>
        {
            if (request.Capability == "examaware.status")
                return new(1, request.RequestId, "Succeeded", null, "隔离状态", ExamAware: state);
            if (request.Capability == "examaware.config.set")
            {
                Interlocked.Increment(ref saves);
                saveReceived.TrySetResult(request);
                await releaseSave.Task.WaitAsync(stop.Token);
                state = state with { Revision = 8, ExecutablePath = request.ExecutablePath };
                return new(1, request.RequestId, "Succeeded", null, "原路径保存回执", ExamAware: state);
            }
            if (request.Capability == "examaware.start") Interlocked.Increment(ref starts);
            return new(1, request.RequestId, "Rejected", "Fixture", "隔离 Host 不执行外部启动");
        }, stop.Token));
        var main = IsolatedMainWindow(pipe);
        var quick = new QuickAccessWindow(pipe, () => { }, () => { }, () => { }, main.RestoreWindow, _ => { }, () => { }, () => { });
        typeof(MainWindow).GetField("_quick", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(main, quick);
        try
        {
            main.Show();
            PumpUntil(() => main.IsLoaded && main.IsActive, "main workspace did not open");
            RunChecks();

            void RunChecks()
            {
                nint hwnd = new WindowInteropHelper(main).Handle;
                var homeEntry = Button(main, "HomeOpenExamAware", true);
                Click(main, "HomeOpenExamAware", true);
                var panel = (ContentControl)main.FindName("ExamAwareWorkspaceHost");
                var exam = (ExamAwareWindow)((WorkspacePanel)panel.Content).Controller;
                var path = (TextBox)exam.FindName("Executable");
                PumpUntil(() => Button(exam, "StartButton").IsEnabled, "embedded ExamAware did not poll status");
                Assert(panel.IsVisible && WorkspacePanel.DialogOwner(exam) == main && new WindowInteropHelper(exam).Handle == 0 &&
                    Window.GetWindow(path) == main && new WindowInteropHelper(main).Handle == hwnd,
                    "ExamAware management created another native window or used it as a dialog owner");

                CaptureWorkspaceSizes("choose", Button(exam, "PreparePlanButton"));
                Assert(!Button(exam, "PresentPlanButton").IsVisible, "the unchecked plan offers a present button");
                state = prepared;
                PumpUntil(() => Button(exam, "PresentPlanButton").IsEnabled, "confirmed plan did not expose presentation");
                CaptureWorkspaceSizes("prepared", Button(exam, "PresentPlanButton"));

                void CaptureWorkspaceSizes(string phase, Button primary)
                {
                    var scroll = (ScrollViewer)exam.FindName("PageScroll");
                    foreach (var size in new[] { new Size(1040, 760), new Size(840, 600) })
                    {
                        main.MinWidth = Math.Min(main.MinWidth, size.Width);
                        main.MinHeight = Math.Min(main.MinHeight, size.Height);
                        main.Width = size.Width;
                        main.Height = size.Height;
                        main.UpdateLayout();
                        foreach (string id in new[] { "ExamAwareBack", "ExamAwareVersion", "ExamAwareStart", "CollapseToSideRail", "MinimizeWindow", "MaximizeRestoreWindow", "CloseWindow" })
                            Reachable(main, (FrameworkElement)Find(main, id));
                        var bounds = primary.TransformToAncestor(scroll).TransformBounds(new Rect(primary.RenderSize));
                        Assert(primary.IsEnabled && ReferenceEquals(primary.Style, exam.FindResource("TeacherPrimary")) &&
                            !ReferenceEquals(Button(exam, "StartButton").Style, primary.Style) && bounds.Top >= 0 && bounds.Bottom <= scroll.ViewportHeight,
                            "the only primary action must be fully visible without scrolling");
                        CheckCaptionHitTargets(main, hwnd);
                        Snapshot(main, $"examaware-main-{phase}-{size.Width}-{size.Height}.png");
                    }
                }

                Click(main, "ExamAwareConfigure", true);
                path.Text = "unsaved-exam-path.exe";
                Click(main, "ExamAwareBack", true);
                PumpUntil(() => !panel.IsVisible && homeEntry.IsKeyboardFocused, "return did not focus the original home entry");
                Click(main, "SettingsTab", true);
                Click(main, "ConnectionsSettingsTab", true);
                main.UpdateLayout();
                var settingsEntry = Button(main, "SettingsExamAware", true);
                settingsEntry.BringIntoView();
                Click(main, "SettingsExamAware", true);
                PumpUntil(() => Button(exam, "StartButton").IsEnabled, "return from settings did not recheck bridge state");
                Assert(ReferenceEquals(((WorkspacePanel)panel.Content).Controller, exam) && path.Text == "unsaved-exam-path.exe",
                    "another entry replaced the window controller or path draft");
                Click(main, "ExamAwareBack", true);
                PumpUntil(() => !panel.IsVisible && settingsEntry.IsKeyboardFocused, "return did not focus the original settings entry");

                Click(main, "OpenExamAware", true);
                PumpUntil(() => Button(exam, "StartButton").IsEnabled, "sidebar entry did not refresh the embedded controller");
                Click(main, "ExamAwareSave", true);
                PumpUntil(() => saveReceived.Task.IsCompleted, "path save did not reach the isolated Host");
                Assert(saveReceived.Task.Result.ExecutablePath == "unsaved-exam-path.exe" && saveReceived.Task.Result.ExpectedRevision == 7,
                    "the write used a different path or revision");
                Click(main, "HomeTab", true);
                releaseSave.TrySetResult();
                PumpUntil(() => ((TextBlock)exam.FindName("Message")).Text == "原路径保存回执", "readback was dropped while the work area was hidden");
                Assert(!panel.IsVisible && Volatile.Read(ref saves) == 1, "the late save response navigated or triggered another write");
                Click(main, "HomeOpenExamAware", true);
                PumpUntil(() => Button(exam, "StartButton").IsEnabled, "visible page did not confirm its state again");
                Assert(path.Text == "unsaved-exam-path.exe" && ((TextBlock)exam.FindName("Message")).Text == "原路径保存回执",
                    "reattaching lost the draft or the original receipt");

                CheckCaptionStateChanges(main, hwnd);
                var sidebar = (ColumnDefinition)main.FindName("WorkspaceNavColumn");
                double expandedWidth = sidebar.Width.Value;
                Click(main, "FoldWorkspaceSidebar", true);
                Assert(sidebar.Width.Value != expandedWidth && main.IsVisible && !quick.IsVisible,
                    "folding the internal navigation hid the main window or opened the desktop rail");
                Click(main, "CollapseToSideRail", true);
                PumpUntil(() => !main.IsVisible && quick.IsVisible, "collapse did not show the separate desktop rail");
                Assert(main.WindowState == WindowState.Normal, "collapsing to the rail entered the Windows minimized state");
                main.RestoreWindow();
                PumpUntil(() => panel.IsVisible && Button(exam, "StartButton").IsEnabled, "returning from the rail lost the exam workspace");

                typeof(MainWindow).GetMethod("ShowWorkspaceCalibration", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(main, null);
                var calibration = (ContentControl)main.FindName("WorkspaceCalibrationHost");
                var retained = calibration.Content;
                typeof(MainWindow).GetMethod("OpenExamAwareQuick", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(main, null);
                PumpUntil(() => panel.IsVisible, "failed quick launch did not fall back to the main workspace");
                Assert(retained is WorkspacePanel && ReferenceEquals(calibration.Content, retained) && Volatile.Read(ref starts) == 1,
                    "quick fallback cancelled the active calibration or replayed the launch");
                Click(main, "ExamAwareBack", true);
                Assert(calibration.IsVisible && ReferenceEquals(calibration.Content, retained), "return opened a different calibration controller");
                using var confirmationDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                var reference = new NoiseReferenceWindow(pipe);
                typeof(MainWindow).GetMethod("ShowWorkspaceReference", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(main, [reference]);
                var referencePanel = calibration.Content;
                var pendingSample = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var pendingSampleField = typeof(NoiseReferenceWindow).GetField("_pendingUsbStart", BindingFlags.Instance | BindingFlags.NonPublic)!;
                pendingSampleField.SetValue(reference, pendingSample);
                try
                {
                    // Inspect and decline the real native dialog without touching a physical sampler.
                    var declined = Task.Run(() => DeclineCalibrationSwitch(hwnd, confirmationDeadline.Token));
                    Click(main, "OpenExamAware", true);
                    PumpUntil(() => declined.IsCompleted, "active calibration did not ask before switching");
                    Assert(declined.GetAwaiter().GetResult() && !panel.IsVisible && calibration.IsVisible &&
                        ReferenceEquals(calibration.Content, referencePanel) && reference.HasActiveSample,
                        "default decline lost the active calibration sample or entered ExamAware");
                }
                finally
                {
                    pendingSampleField.SetValue(reference, null);
                    pendingSample.TrySetResult();
                }
                Checks.Add("ExamAware same HWND from home, settings and sidebar; original focus, path draft and in-flight receipt survive switching; quick fallback keeps calibration");
                Checks.Add("Active calibration prompts before switching; the native default decline retains the pending sample and current workspace");
                Checks.Add("ExamAware choose and present are the sole primary action for their stage and fully visible at both widths");
                Checks.Add("Title bar: native hit targets, 44-DIP controls, Windows minimize/maximize/restore retain manual size, and navigation fold is distinct from collapsing to the desktop rail");
            }
        }
        finally
        {
            main.Close();
            releaseSave.TrySetResult();
            stop.Cancel();
            try { server.GetAwaiter().GetResult(); } catch (OperationCanceledException) { }
        }
    }

    private static void CheckCaptionHitTargets(Window main, nint hwnd)
    {
        var left = Button(main, "FoldWorkspaceSidebar", true);
        var right = Button(main, "CollapseToSideRail", true);
        var caption = (FrameworkElement)main.FindName("PageBreadcrumb");
        var leftEdge = caption.PointToScreen(new(caption.ActualWidth, caption.ActualHeight / 2));
        var rightEdge = right.PointToScreen(new(0, right.ActualHeight / 2));
        Assert(rightEdge.X - leftEdge.X >= 28, "title bar has no useful drag area");
        Assert(CaptionHitAt(hwnd, new((leftEdge.X + rightEdge.X) / 2, rightEdge.Y)) == 2, "blank title bar is not a native drag target");
        foreach (var button in new[] { left, right, Button(main, "MinimizeWindow", true), Button(main, "MaximizeRestoreWindow", true), Button(main, "CloseWindow", true) })
        {
            Rect bounds = button.TransformToAncestor(main).TransformBounds(new Rect(button.RenderSize));
            Assert(bounds.Width >= 43.9 && bounds.Height >= 43.9, "title button is smaller than a 44-DIP touch target");
            Assert(CaptionHitAt(hwnd, button.PointToScreen(new(button.ActualWidth / 2, button.ActualHeight / 2))) == 1,
                AutomationProperties.GetAutomationId(button) + " is covered by native drag hit testing");
        }
    }

    private static void CheckCaptionStateChanges(Window main, nint hwnd)
    {
        main.Width = 932; main.Height = 648;
        main.UpdateLayout();
        var size = new Size(main.ActualWidth, main.ActualHeight);
        var toggle = Button(main, "MaximizeRestoreWindow", true);
        Click(main, "MaximizeRestoreWindow", true);
        PumpUntil(() => main.WindowState == WindowState.Maximized && AutomationProperties.GetName(toggle) == "还原窗口",
            "maximize did not set the native state or restore label");
        Click(main, "MaximizeRestoreWindow", true);
        PumpUntil(() => main.WindowState == WindowState.Normal && AutomationProperties.GetName(toggle) == "最大化窗口",
            "restore did not return to normal with the original label");
        main.UpdateLayout();
        Assert(Math.Abs(main.ActualWidth - size.Width) < 2 && Math.Abs(main.ActualHeight - size.Height) < 2,
            "restore replaced the manually sized normal window");
        Click(main, "MinimizeWindow", true);
        PumpUntil(() => main.WindowState == WindowState.Minimized, "the minimize button hid the window rather than minimizing it");
        Assert(main.IsVisible && new WindowInteropHelper(main).Handle == hwnd, "minimizing hid or recreated the native window");
        SystemCommands.RestoreWindow(main);
        PumpUntil(() => main.WindowState == WindowState.Normal, "system restore did not restore a minimized workspace");
    }

    private static int CaptionHitAt(nint hwnd, Point point)
    {
        int xy = ((int)point.Y & 0xffff) << 16 | ((int)point.X & 0xffff);
        return (int)SendCaptionMessage(hwnd, 0x0084, 0, xy);
    }

    [DllImport("user32.dll", EntryPoint = "SendMessageW")]
    private static extern nint SendCaptionMessage(nint hwnd, uint message, nint wParam, nint lParam);

    private static async Task<bool> DeclineCalibrationSwitch(nint owner, CancellationToken token)
    {
        const int noButton = 7;
        while (!token.IsCancellationRequested)
        {
            nint dialog = GetLastActivePopup(owner);
            nint button = dialog != 0 && dialog != owner ? GetDlgItem(dialog, noButton) : 0;
            if (button != 0)
            {
                bool isDefault = ((long)SendCaptionMessage(dialog, 0x0400, 0, 0) & 0xffff) == noButton;
                return PostDialogCommand(dialog, 0x0111, noButton, button) && isDefault;
            }
            try { await Task.Delay(20, token); }
            catch (OperationCanceledException) { break; }
        }
        return false;
    }

    [DllImport("user32.dll")]
    private static extern nint GetLastActivePopup(nint owner);

    [DllImport("user32.dll")]
    private static extern nint GetDlgItem(nint dialog, int controlId);

    [DllImport("user32.dll", EntryPoint = "PostMessageW")]
    private static extern bool PostDialogCommand(nint dialog, uint message, nint wParam, nint lParam);
}

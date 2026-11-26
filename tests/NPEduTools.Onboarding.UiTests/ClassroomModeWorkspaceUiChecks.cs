using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using NPEduTools.App;
using NPEduTools.Contracts;

internal static partial class Program
{
    private static void RunEmbeddedClassroomModeCheck()
    {
        string pipe = "NPEduTools.Test.classroom-mode-workspace." + Guid.NewGuid().ToString("N");
        using var stop = new CancellationTokenSource();
        var state = new ClassroomModeState(7, "Daily", Message: "已核实日常模式。",
            Actual: ClassroomExam().Recovery!.Startup, CheckedAt: DateTimeOffset.UtcNow, MatchesMode: true);
        int permissionDenied = 1, switches = 0, verifications = 0, setupReads = 0;
        var server = Task.Run(() => WorkspaceProxy(pipe, request =>
        {
            var current = Volatile.Read(ref state);
            switch (request.Capability)
            {
                case "classroom.status":
                    return Task.FromResult(new HostResponse(1, request.RequestId, "Succeeded", null, current.Message, ClassroomMode: current));
                case "classroom.refresh":
                    Interlocked.Increment(ref verifications);
                    Volatile.Write(ref state, current with { CheckedAt = DateTimeOffset.UtcNow, Message = "已重新核实实际设置，ClassIsland 仍在运行。" });
                    return Task.FromResult(new HostResponse(1, request.RequestId, "Accepted", null, "正在核实实际设置",
                        ClassroomMode: current with { Phase = "Checking" }));
                case "classroom.set":
                    Assert(Volatile.Read(ref permissionDenied) == 0 && request.ExpectedRevision == 7 && request.ClassroomMode is { Target: "Exam", SwitchRunning: true },
                        "the original confirmation or classroom policy was bypassed");
                    Interlocked.Increment(ref switches);
                    var incomplete = ClassroomExam();
                    Volatile.Write(ref state, incomplete with { Revision = 8, Message = "ExamAware2 已准备，ClassIsland 的弹窗阻止退出。",
                        RecentRequests = [request.RequestId], Runtime = new("Exam", incomplete.Actual!, DateTimeOffset.UtcNow) });
                    throw new System.IO.IOException("drop precisely the first switch response");
            }
            if (request.Capability.StartsWith("noise.management.status", StringComparison.Ordinal))
            {
                var policy = Volatile.Read(ref permissionDenied) == 1 ? new ClassroomActionPolicyState(new string('a', 64), "default", "default", "deny") : null;
                return Task.FromResult(new HostResponse(1, request.RequestId, "Succeeded", null, "隔离权限",
                    NoiseProtection: new(false, false, Guid.NewGuid(), null, ManagementSource: "School", ClassroomPolicy: policy)));
            }
            var setup = ClassroomSetupResponse(request.Capability) with { RequestId = request.RequestId };
            return Task.FromResult(setup);
        }, stop.Token));

        var main = IsolatedMainWindow(pipe);
        try
        {
            main.Show();
            PumpUntil(() => main.IsActive, "the isolated main window was not active");
            nint hwnd = new WindowInteropHelper(main).Handle;
            var homeEntry = Button(main, "HomeClassroomMode", true);
            Click(main, "HomeClassroomMode", true);
            Assert(main.FindName("ClassroomModeWorkspaceHost") is ContentControl { IsVisible: true, Content: WorkspacePanel }, "classroom mode opened a separate work window");
            var host = (ContentControl)main.FindName("ClassroomModeWorkspaceHost");
            var page = (ClassroomModeWindow)((WorkspacePanel)host.Content).Controller;
            typeof(ClassroomModeWindow).GetField("_setupCheck", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(page,
                SyntheticClassroomSetup((capability, _) =>
                {
                    Interlocked.Increment(ref setupReads);
                    return Task.FromResult(ClassroomSetupResponse(capability));
                }));
            Assert(new WindowInteropHelper(page).Handle == 0 && WorkspacePanel.DialogOwner(page) == main && new WindowInteropHelper(main).Handle == hwnd,
                "classroom mode did not share the original HWND and dialog owner");
            var scroll = (ScrollViewer)page.FindName("ModeScroll");
            var details = (Expander)page.FindName("SetupDetails");
            var immediate = (CheckBox)page.FindName("SwitchRunning");
            var exam = Button(page, "ExamButton");
            var refresh = Button(page, "RefreshButton");
            var retry = Button(page, "RetryButton");
            var restore = Button(page, "RestoreButton");
            var feedback = (TextBlock)page.FindName("OperationFeedbackText");
            PumpUntil(() => exam.IsEnabled && Volatile.Read(ref setupReads) > 0, "embedded mode did not complete its initial read-only check");

            Capture(1040, 760, "ready", exam);
            Capture(840, 600, "ready", exam);
            immediate.IsChecked = true;
            ConfirmExam();
            PumpUntil(() => feedback.Text.Contains("学校") && exam.IsEnabled, "classroom policy rejection was not visible on the page");
            Assert(Volatile.Read(ref switches) == 0, "a denied classroom command reached the Host");
            Capture(840, 600, "denied", exam);

            Volatile.Write(ref permissionDenied, 0);
            ConfirmExam();
            PumpUntil(() => Volatile.Read(ref switches) == 1 && refresh.IsEnabled && feedback.Text.Contains("未收到后台结果"),
                "the lost response did not leave a read-only way to verify the original switch");
            Assert(!exam.IsEnabled && !retry.IsEnabled && !restore.IsEnabled, "an unverified switch still allowed a write");
            Capture(840, 600, "unconfirmed", refresh, feedback);

            int beforeSetup = Volatile.Read(ref setupReads);
            CheckReturn("ClassroomSetupClassIsland", "ReturnToClassroomCheck");
            CheckReturn("ClassroomSetupAdmin", "ReturnToClassroomCheck");
            CheckReturn("ClassroomSetupExamAware", "ExamAwareBack");
            PumpUntil(() => Volatile.Read(ref setupReads) > beforeSetup && refresh.IsEnabled, "return did not refresh setup or preserve verification");
            Assert(immediate.IsChecked == true && Volatile.Read(ref switches) == 1 && Volatile.Read(ref verifications) == 0,
                "configuration navigation cleared the option, replayed the original switch, or sent an unsolicited refresh command");
            Click(main, "ClassroomRefresh", true);
            PumpUntil(() => refresh.IsEnabled && retry.IsEnabled && restore.IsEnabled, "verified partial result did not expose the recovery choices");
            Capture(1040, 760, "recovery", retry, feedback);
            Capture(840, 600, "recovery", retry, restore);
            CheckCaptionStateChanges(main, hwnd);
            Assert(Volatile.Read(ref switches) == 1 && Volatile.Read(ref verifications) == 1 && new WindowInteropHelper(page).Handle == 0,
                "an extra mode write or second native work window appeared");
            Click(main, "ClassroomModeBack", true);
            PumpUntil(() => !host.IsVisible && homeEntry.IsKeyboardFocused, "Back did not restore the homepage and original focus");
            Click(main, "SettingsTab", true);
            Click(main, "ConnectionsSettingsTab", true);
            main.UpdateLayout();
            var settingsEntry = Button(main, "SettingsClassroomMode", true);
            Click(main, "SettingsClassroomMode", true);
            Assert(host.IsVisible, "settings opened a separate classroom mode window");
            Click(main, "ClassroomModeBack", true);
            PumpUntil(() => !host.IsVisible && settingsEntry.IsKeyboardFocused, "Back did not restore the settings entry");
            Click(main, "OpenClassroomMode", true);
            Assert(host.IsVisible && ReferenceEquals(((WorkspacePanel)host.Content).Controller, page), "sidebar lost the original mode workspace");
            Click(main, "CloseWindow", true);
            PumpUntil(() => !main.IsVisible, "the custom close button did not act on the main window");
            Checks.Add("Classroom mode has one HWND and shared caption controls; the three configuration links return to the original check and preserve the current option");
            Checks.Add("School policy denial sends no mutation. A lost response leaves one known switch and an explicit read-only verification; verified partial failure shows retry/recovery above the fold");

            void ConfirmExam()
            {
                using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                var answer = Task.Run(() => RespondToDrawAcknowledgement(hwnd, accept: true, deadline.Token));
                Click(main, "ClassroomExam", true);
                PumpUntil(() => answer.IsCompleted, "mode confirmation did not use the original main HWND");
                Assert(answer.GetAwaiter().GetResult(), "mode confirmation must default to Cancel");
            }

            void CheckReturn(string entry, string back)
            {
                details.IsExpanded = true;
                main.UpdateLayout();
                var original = Button(main, entry, true);
                Click(main, entry, true);
                main.UpdateLayout();
                Assert(!host.IsVisible && new WindowInteropHelper(main).Handle == hwnd, "configuration did not open in the same main window");
                Click(main, back, true);
                PumpUntil(() => host.IsVisible && original.IsKeyboardFocused, "return did not focus the original configuration check: " + entry);
            }

            void Capture(int width, int height, string phase, Button primary, params FrameworkElement[] visible)
            {
                main.MinWidth = Math.Min(main.MinWidth, width);
                main.MinHeight = Math.Min(main.MinHeight, height);
                main.Width = width;
                main.Height = height;
                scroll.ScrollToTop();
                main.UpdateLayout();
                CheckCaptionHitTargets(main, hwnd);
                var buttons = new[] { Button(page, "DailyButton"), exam, refresh, retry, restore };
                Assert(buttons.Count(button => ReferenceEquals(button.Style, page.FindResource("TeacherPrimary"))) == 1 &&
                    ReferenceEquals(primary.Style, page.FindResource("TeacherPrimary")), "classroom mode did not identify one safe primary action");
                foreach (var control in visible.Prepend(primary))
                {
                    Rect bounds = control.TransformToAncestor(scroll).TransformBounds(new Rect(control.RenderSize));
                    Assert(control.IsVisible && bounds.Top >= 0 && bounds.Bottom <= scroll.ViewportHeight + 1,
                        $"{phase} control {control.Name} is clipped at {width}x{height}: {bounds.Bottom:F1}/{scroll.ViewportHeight:F1}");
                }
                Snapshot(main, $"classroom-main-{phase}-{width}-{height}.png");
            }
        }
        finally
        {
            if (main.IsVisible) main.Close();
            stop.Cancel();
            try { server.GetAwaiter().GetResult(); } catch (OperationCanceledException) { }
        }
    }
}

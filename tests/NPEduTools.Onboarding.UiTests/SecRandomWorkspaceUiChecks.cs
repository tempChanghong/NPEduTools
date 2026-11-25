using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using NPEduTools.App;
using NPEduTools.Contracts;

internal static partial class Program
{
    private static void RunEmbeddedSecRandomWorkspaceCheck()
    {
        string pipe = "NPEduTools.Test.secrandom-workspace." + Guid.NewGuid().ToString("N");
        using var stop = new CancellationTokenSource();
        var ready = new SecRandomState("SecRandomLauncher.exe", 7, "Ready", DateTimeOffset.Now,
            new(Guid.NewGuid(), "quick-draw", "Succeeded", "抽取完成", DateTimeOffset.Now, Winner: new("A-08", "周同学", "")));
        SecRandomState state = ready;
        Guid original = Guid.NewGuid();
        var pending = ready with { Connection = "Unavailable", Operation = new(original, "quick-draw", "Unknown", "等待已结束", DateTimeOffset.Now) };
        int draws = 0;
        var server = Task.Run(() => WorkspaceProxy(pipe, request =>
        {
            if (request.SecRandom?.Action == "quick-draw") Interlocked.Increment(ref draws);
            if (request.SecRandom?.Action == "acknowledge")
            {
                Assert(request.SecRandom.AcknowledgeOperation == original, "main window acknowledged a different draw");
                Volatile.Write(ref state, ready with { Operation = pending.Operation! with { State = "Acknowledged" } });
            }
            return Task.FromResult(new HostResponse(Protocol.Version, request.RequestId, "Succeeded", null, "隔离状态", SecRandom: Volatile.Read(ref state)));
        }, stop.Token));
        var main = IsolatedMainWindow(pipe);
        try
        {
            main.Show();
            PumpUntil(() => main.IsLoaded && main.IsActive, "main window did not become active");
            nint hwnd = new WindowInteropHelper(main).Handle;
            var homeEntry = Button(main, "HomeOpenSecRandom", true);
            Click(main, "HomeOpenSecRandom", true);
            Assert(main.FindName("SecRandomWorkspaceHost") is ContentControl { IsVisible: true, Content: WorkspacePanel },
                "classroom roll call opened a separate window instead of the main workspace");
            var panel = (ContentControl)main.FindName("SecRandomWorkspaceHost");
            var controller = (SecRandomWindow)((WorkspacePanel)panel.Content).Controller;
            var program = (TextBox)controller.FindName("Executable");
            var scroll = (ScrollViewer)controller.FindName("PageScroll");
            var floating = (Expander)controller.FindName("FloatConfiguration");
            var draw = Button(controller, "DrawButton");
            var acknowledge = Button(controller, "AcknowledgeButton");
            PumpUntil(() => draw.IsEnabled, "embedded classroom roll call did not read its status");
            Assert(WorkspacePanel.DialogOwner(controller) == main && Window.GetWindow(program) == main &&
                new WindowInteropHelper(controller).Handle == 0 && new WindowInteropHelper(main).Handle == hwnd,
                "the point-name page or file dialogs do not belong to the original native main window");

            Resize(1040, 760);
            Assert(floating.IsExpanded, "wide workspace did not show both floating-window actions");
            CheckCaptionHitTargets(main, hwnd);
            Snapshot(main, "secrandom-main-ready-1040-760.png");
            Resize(840, 600);
            Assert(!floating.IsExpanded, $"compact main workspace did not fold floating-window actions (window {main.ActualWidth}, content {scroll.ActualWidth})");
            CheckCaptionHitTargets(main, hwnd);
            Snapshot(main, "secrandom-main-ready-840-600.png");
            floating.IsExpanded = true;
            Resize(1040, 760);
            Resize(840, 600);
            Assert(floating.IsExpanded, "resizing the embedded page reset the user’s floating-window choice");
            floating.IsExpanded = false;

            Click(main, "SecRandomConfigure", true);
            program.Text = "unsaved-program.exe";
            Click(main, "SecRandomBack", true);
            PumpUntil(() => !panel.IsVisible && homeEntry.IsKeyboardFocused, "Back did not return keyboard focus to the original home entry");
            Click(main, "SettingsTab", true);
            Click(main, "ConnectionsSettingsTab", true);
            main.UpdateLayout();
            var settings = Button(main, "SettingsSecRandom", true);
            settings.BringIntoView();
            Click(main, "SettingsSecRandom", true);
            PumpUntil(() => draw.IsEnabled, "settings entry did not reopen the workspace");
            Assert(ReferenceEquals(((WorkspacePanel)panel.Content).Controller, controller) && program.Text == "unsaved-program.exe",
                "opening from settings replaced the controller or discarded the path draft");
            Click(main, "SecRandomBack", true);
            PumpUntil(() => !panel.IsVisible && settings.IsKeyboardFocused, "Back did not restore the settings entry and focus");

            Click(main, "OpenSecRandom", true);
            Click(main, "OpenExamAware", true);
            main.UpdateLayout();
            Assert(!panel.IsVisible, "ExamAware did not close the classroom roll-call workspace");
            Click(main, "ExamAwareBack", true);
            Assert(panel.IsVisible && program.Text == "unsaved-program.exe", "returning from ExamAware discarded the classroom roll-call workspace");
            CheckCaptionStateChanges(main, hwnd);
            Resize(840, 600);
            Volatile.Write(ref state, pending);
            PumpUntil(() => acknowledge.IsVisible && acknowledge.IsEnabled, "the original draw could not be reconciled from the main window");
            Assert(!draw.IsEnabled && Volatile.Read(ref draws) == 0, "receiving an uncertain result reissued a draw");
            var result = (TextBlock)controller.FindName("OperationText");
            Assert(scroll.VerticalOffset == 0 && result.Text.Contains("结果待核实"), "pending reconciliation was not displayed at the top");
            foreach (var element in new FrameworkElement[] { result, acknowledge })
            {
                Rect bounds = element.TransformToAncestor(scroll).TransformBounds(new Rect(element.RenderSize));
                Assert(bounds.Top >= 0 && bounds.Bottom <= scroll.ViewportHeight + 1, "pending reconciliation is clipped in the compact main window");
            }
            Snapshot(main, "secrandom-main-uncertain-840-600.png");
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var confirm = Task.Run(() => RespondToDrawAcknowledgement(hwnd, accept: true, deadline.Token));
            Click(main, "SecRandomAcknowledge", true);
            Assert(confirm.GetAwaiter().GetResult(), "the main HWND did not own the reconciliation dialog");
            PumpUntil(() => draw.IsEnabled && !acknowledge.IsVisible, "the original draw did not clear after explicit reconciliation");

            Click(main, "HomeTab", true);
            Volatile.Write(ref state, pending);
            var quick = (Task<string>)typeof(MainWindow).GetMethod("QuickDrawSecRandomAsync", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(main, null)!;
            PumpUntil(() => quick.IsCompleted && panel.IsVisible && acknowledge.IsEnabled, "side-rail guidance did not reopen the main workspace");
            Assert(new WindowInteropHelper(controller).Handle == 0 && Volatile.Read(ref draws) == 0,
                "side-rail guidance opened a separate window or resent the original draw");

            bool closed = false;
            main.Closed += (_, _) => closed = true;
            Click(main, "CloseWindow", true);
            PumpUntil(() => closed, "close did not use the original main window command");
            Checks.Add("Classroom roll call stays inside the same main HWND from home, settings, sidebar and quick-draw guidance; shared caption controls and original draft remain usable");
            Checks.Add("Main-window wide and compact layout retains the three sections; compact reconciliation is fully visible and uses the main HWND, original draw ID and no replay");

            void Resize(double width, double height)
            {
                main.MinWidth = Math.Min(main.MinWidth, width);
                main.MinHeight = Math.Min(main.MinHeight, height);
                main.Width = width;
                main.Height = height;
                scroll.ScrollToTop();
                PumpUntil(() =>
                {
                    main.UpdateLayout();
                    return Math.Abs(main.ActualWidth - width) < 2 && Math.Abs(main.ActualHeight - height) < 2;
                }, "the main window did not reach the requested size");
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

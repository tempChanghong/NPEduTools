using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Threading;
using NPEduTools.App;
using NPEduTools.Contracts;

internal static partial class Program
{
    private static void RunRecordingQuickNavigationChecks()
    {
        var previous = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Application.Current.Dispatcher));
        try
        {
            CheckHiddenWorkspaceOwner();
            foreach (string scenario in new[] { "cold-hidden", "shown-hidden", "minimized", "repeated" })
                RecordingQuickNavigation(scenario);
        }
        finally { SynchronizationContext.SetSynchronizationContext(previous); }
    }

    private static void CheckHiddenWorkspaceOwner()
    {
        string pipe = "NPEduTools.Test.hidden-workspace-owner." + Guid.NewGuid().ToString("N");
        var owner = PrivateWorkspace(pipe);
        var body = new Grid();
        var controller = new Window { Content = body };
        int sources = 0, visible = 0;
        owner.SourceInitialized += (_, _) => sources++;
        owner.IsVisibleChanged += (_, _) => { if (owner.IsVisible) visible++; };
        try
        {
            Assert(new WindowInteropHelper(owner).Handle == 0, "cold owner fixture already had a native window");
            var panelType = typeof(MainWindow).Assembly.GetType("NPEduTools.App.WorkspacePanel")!;
            var panel = (ContentControl)Activator.CreateInstance(panelType, WorkspaceFlags, null, [controller, owner, null], null)!;
            Assert(!owner.IsVisible && !controller.IsVisible && visible == 0 && sources == 1,
                "embedding displayed quiet-login windows or initialized the owner repeatedly");
            Assert(new WindowInteropHelper(owner).Handle != 0 && controller.Owner == owner && ReferenceEquals(panel.Content, body),
                "hidden owner was not initialized or the embedded body/owner was lost");
            var dialogOwner = panelType.GetMethod("DialogOwner", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!
                .Invoke(null, [controller]);
            Assert(ReferenceEquals(dialogOwner, owner), "embedded dialogs lost their real owner");
            Checks.Add("Shared workspace: a never-shown owner obtains one native source without being displayed; embedded content and dialog ownership remain intact; no Host or sensors");
        }
        finally { controller.Close(); owner.Close(); }
    }

    private static void RecordingQuickNavigation(string scenario)
    {
        string pipe = "NPEduTools.Test.quick-plan." + Guid.NewGuid().ToString("N");
        string preferences = StartupPreferencesStore.PathFor(pipe);
        var recording = new RecordingState("Recording", "Synthetic existing recording", Control: new("Automatic", Guid.NewGuid(), "fixture", 0, 0));
        var automatic = new AutomaticRecordingState(true, Guid.NewGuid(), "Synthetic existing automatic owner", null, []);
        using var stop = new CancellationTokenSource();
        var elapsed = Stopwatch.StartNew();
        Guid connection = Guid.NewGuid(), bridge = Guid.NewGuid();
        int sequence = 0, mutations = 0;
        var server = Task.Run(() => WorkspaceProxy(pipe, request =>
        {
            if (request.Recording is not null || request.Automatic?.Action is not (null or "lease")) Interlocked.Increment(ref mutations);
            var now = new DateTimeOffset(2026, 10, 22, 8, 10, 0, TimeSpan.Zero).Add(elapsed.Elapsed);
            SchoolClockFrame? clock = request.Capability == "classisland.school-clock"
                ? new(connection, bridge, Interlocked.Increment(ref sequence), 0, now, 0, "Advancing", "Synthetic school clock") : null;
            return Task.FromResult(new HostResponse(1, request.RequestId, "Succeeded", null, "Synthetic read-only receipt",
                Recording: recording, Automatic: automatic, SchoolClock: clock));
        }, stop.Token));
        var main = PrivateWorkspace(pipe);
        var quick = new QuickAccessWindow(pipe, () => { }, () => { }, () => { }, () => { }, _ => { }, () => { }, () => { });
        var failures = new List<Exception>();
        void Unhandled(object sender, DispatcherUnhandledExceptionEventArgs args) { failures.Add(args.Exception); args.Handled = true; }
        Application.Current.DispatcherUnhandledException += Unhandled;
        try
        {
            WorkspaceCall(main, "InitializeRecording");
            typeof(MainWindow).GetField("_quick", WorkspaceFlags)!.SetValue(main, quick);
            quick.SetRecordingActions(() => { }, () => { }, () => { }, () => WorkspaceCall(main, "ShowRecordingPlan"), () => { });
            if (scenario != "cold-hidden") { main.Show(); main.UpdateLayout(); main.Hide(); }
            if (scenario == "minimized") { main.Show(); main.WindowState = WindowState.Minimized; }
            quick.OpenPanel(); quick.UpdateLayout();
            Click(quick, "QuickRecordingPlan", automation: true);
            main.UpdateLayout();
            Assert(main.IsVisible && main.WindowState == WindowState.Normal, scenario + ": quick navigation did not restore the main window");
            var plan = WorkspaceField<AutoRecordingWindow>(main, "_autoRecordingWindow");
            PumpUntil(() => ((TextBlock)plan.FindName("SourceStatus")).Text.Contains("日程暂不可用"), scenario + ": embedded plan did not settle its school-clock read");
            if (scenario == "repeated")
            {
                for (int i = 0; i < 4; i++)
                {
                    WorkspaceCall(main, "ShowRecordingWorkspace", "Records"); main.Hide();
                    quick.OpenPanel(); Click(quick, "QuickRecordingPlan", automation: true); main.UpdateLayout();
                    Assert(ReferenceEquals(plan, WorkspaceField<AutoRecordingWindow>(main, "_autoRecordingWindow")), "quick navigation duplicated the plan controller");
                }
            }
            var drained = Application.Current.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            PumpUntil(() => drained.Task.IsCompleted, "quick navigation callbacks did not settle");
            Assert(failures.Count == 0, scenario + ": unhandled UI exception: " + string.Join("\n", failures));
            Assert(mutations == 0, "quick navigation sent a recording mutation");
            Checks.Add("Quick recording plan/" + scenario + ": actual shortcut button restores embedded plan, no unhandled dispatcher error or recording mutation; private pipe, no sensors");
        }
        finally
        {
            main.Close(); quick.Shutdown(); stop.Cancel();
            try { server.GetAwaiter().GetResult(); } catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
            Application.Current.DispatcherUnhandledException -= Unhandled;
            foreach (string suffix in new[] { ".recording-preview.json", ".recording-plans.json" })
            {
                string path = preferences.Replace(".startup.json", suffix, StringComparison.Ordinal);
                if (File.Exists(path)) File.Delete(path);
            }
        }
    }
}

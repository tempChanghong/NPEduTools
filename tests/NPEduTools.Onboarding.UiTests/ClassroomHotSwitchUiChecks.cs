using System.Collections.Concurrent;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Threading;
using NPEduTools.App;
using NPEduTools.Contracts;
using NPEduTools.Host;

internal static partial class Program
{
    private static void RunClassroomHotSwitchCheck()
    {
        string pipe = "NPEduTools.Test.classroom-hot-switch." + Guid.NewGuid().ToString("N");
        string data = Path.Combine(_output, "isolated-mode");
        if (Directory.Exists(data)) throw new InvalidOperationException("Use a fresh output directory for this run.");
        var effects = new HotSwitchEffects();
        var mode = new ClassroomModeStore(data);
        mode.Save(new(Mode: "Daily", Actual: effects.Startup, CheckedAt: DateTimeOffset.UtcNow, MatchesMode: true));
        var service = new ClassroomModeService(mode, effects);
        var commands = new ConcurrentQueue<HostRequest>();
        using var stop = new CancellationTokenSource();
        var server = Task.Run(() => WorkspaceProxy(pipe, request =>
        {
            if (request.Capability.StartsWith("classroom.", StringComparison.Ordinal))
            {
                if (request.Capability == "classroom.set") commands.Enqueue(request);
                return Task.FromResult(service.Handle(request));
            }
            if (request.Capability.StartsWith("noise.management.status", StringComparison.Ordinal))
                return Task.FromResult(new HostResponse(Protocol.Version, request.RequestId, "Succeeded", null, "隔离测例",
                    NoiseProtection: new(false, false, Guid.NewGuid(), null)));
            return Task.FromResult(ClassroomSetupResponse(request.Capability) with { RequestId = request.RequestId });
        }, stop.Token));

        var previous = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Application.Current.Dispatcher));
        var main = IsolatedMainWindow(pipe);
        try
        {
            main.Width = 840;
            main.Height = 600;
            main.Show();
            PumpUntil(() => main.IsActive, "the isolated main window was not active");
            nint hwnd = new WindowInteropHelper(main).Handle;
            Click(main, "HomeClassroomMode", true);
            var container = (ContentControl)main.FindName("ClassroomModeWorkspaceHost");
            var page = (ClassroomModeWindow)((WorkspacePanel)container.Content).Controller;
            typeof(ClassroomModeWindow).GetField("_setupCheck", BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(page, SyntheticClassroomSetup());
            var scope = (CheckBox)page.FindName("SwitchRunning");
            var exam = Button(page, "ExamButton");
            var daily = Button(page, "DailyButton");
            var feedback = (TextBlock)page.FindName("OperationFeedbackText");
            PumpUntil(() => exam.IsEnabled && daily.IsEnabled, "mode controls unavailable after isolated setup");
            Assert(scope.IsChecked == false, "classroom mode must preserve the non-disruptive default");

            Switch("ClassroomExam", "Exam", running: false, number: 1, "本次仅更新设置");
            Assert(effects.ClassIslandRunning && !effects.ExamAwareRunning && effects.RuntimeActions.IsEmpty,
                "settings-only should not run the runtime coordinator");
            Snapshot(main, "classroom-settings-only.png");

            scope.IsChecked = true;
            Switch("ClassroomExam", "Exam", running: true, number: 2, "本次即时切换已完成");
            Assert(!effects.ClassIslandRunning && effects.ExamAwareRunning && mode.State.AutomaticPaused,
                "exam hot switch must prepare ExamAware, close ClassIsland, and keep automatic recording paused");
            Snapshot(main, "classroom-hot-exam.png");

            Switch("ClassroomDaily", "Daily", running: true, number: 3, "本次即时切换已完成");
            Assert(effects.ClassIslandRunning && !effects.ExamAwareRunning && !mode.State.AutomaticPaused &&
                mode.State.Recovery is null && mode.State.Runtime is null, "return to daily must restore normal runtime and recording policy");
            Assert(effects.RuntimeActions.SequenceEqual(new[] {"PrepareExam", "CloseClassIsland", "VerifyExam", "CloseExam", "StartClassIsland", "VerifyDaily"}),
                "the production runtime coordinator changed the order of external effects");
            Snapshot(main, "classroom-hot-daily.png");
            Checks.Add("Real WPF workspace through isolated named pipe and production Host state machine: settings-only does not switch runtime; hot switch in both directions; external programs and startup are simulated");

            void Switch(string button, string target, bool running, int number, string completion)
            {
                var action = target == "Daily" ? daily : exam;
                Assert(action.Content.ToString()?.Contains(running ? "即时" : "设置") == true, "the action does not say whether current programs will switch");
                using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(7));
                var answer = Task.Run(() => RespondToDrawAcknowledgement(hwnd, accept: true, deadline.Token));
                Click(main, button, true);
                PumpUntil(() => answer.IsCompleted, "mode confirmation did not use the original main HWND");
                Assert(answer.GetAwaiter().GetResult(), "mode confirmation must default to Cancel");
                PumpUntil(() => commands.Count == number && mode.State is { Phase: "Idle", MatchesMode: true } state &&
                    state.Mode == target && feedback.Text.Contains(completion), "mode request did not reach verified completion");
                var request = commands.Last();
                Assert(request.ClassroomMode == new ClassroomModeCommand(target, running, running && target == "Daily") &&
                    mode.State.RecentRequests?.Last() == request.RequestId && new WindowInteropHelper(main).Handle == hwnd,
                    "a different or repeated operation was executed");
            }
        }
        finally
        {
            main.Close();
            stop.Cancel();
            try { server.GetAwaiter().GetResult(); } catch (OperationCanceledException) { }
            service.DisposeAsync().AsTask().GetAwaiter().GetResult();
            SynchronizationContext.SetSynchronizationContext(previous);
        }
    }

    private sealed class HotSwitchEffects : IClassroomModeEffects, IClassroomRuntimeActions
    {
        internal ClassroomStartupSnapshot Startup { get; private set; } = new("fixture-ci.exe", 1, true, "fixture-ea.exe", 1, false);
        internal bool ClassIslandRunning { get; private set; } = true;
        internal bool ExamAwareRunning { get; private set; }
        internal ConcurrentQueue<string> RuntimeActions { get; } = new();
        public Task<ClassroomStartupSnapshot> ObserveAsync(bool connect) => Task.FromResult(Startup);
        public Task SetClassIslandAsync(ClassroomStartupSnapshot expected, bool enabled)
        { Startup = Startup with { ClassIslandEnabled = enabled }; return Task.CompletedTask; }
        public Task SetExamAwareAsync(ClassroomStartupSnapshot expected, bool enabled)
        { Startup = Startup with { ExamAwareEnabled = enabled }; return Task.CompletedTask; }
        public Task PauseRecordingAsync() => Task.CompletedTask;
        public Task RunRuntimeAsync(ClassroomRuntimeIntent intent, Action<string, string> progress) => new ClassroomRuntimeCoordinator(this).RunAsync(intent, progress);
        public Task ValidateAsync(ClassroomStartupSnapshot expected)
        { Assert(expected == Startup, "runtime changed before dispatch"); return Task.CompletedTask; }
        public Task PrepareExamAsync(ClassroomStartupSnapshot expected)
        { ExamAwareRunning = true; RuntimeActions.Enqueue("PrepareExam"); return Task.CompletedTask; }
        public Task CloseClassIslandAsync(ClassroomStartupSnapshot expected)
        { ClassIslandRunning = false; RuntimeActions.Enqueue("CloseClassIsland"); return Task.CompletedTask; }
        public Task CloseExamAsync(ClassroomStartupSnapshot expected)
        { ExamAwareRunning = false; RuntimeActions.Enqueue("CloseExam"); return Task.CompletedTask; }
        public Task StartClassIslandAsync(ClassroomStartupSnapshot expected)
        { ClassIslandRunning = true; RuntimeActions.Enqueue("StartClassIsland"); return Task.CompletedTask; }
        public Task VerifyAsync(ClassroomStartupSnapshot expected, string target)
        {
            Assert(ClassIslandRunning == (target == "Daily") && ExamAwareRunning == (target == "Exam"), "runtime verification did not see the selected programs");
            RuntimeActions.Enqueue("Verify" + target);
            return Task.CompletedTask;
        }
    }
}

using System.IO.Pipes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using NPEduTools.App;
using NPEduTools.Contracts;
using NPEduTools.Host;

internal static partial class Program
{
    private static void RunStudyReturnChecks()
    {
        var previous = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Application.Current.Dispatcher));
        try
        {
            StudyReturnCheck(false); StudyReturnCheck(true);
            var hideErrors = new List<Exception>();
            foreach (bool manual in new[] { false, true })
                foreach (bool delayed in new[] { false, true })
                    try { StudyHideAfterReturnCheck(manual, delayed); }
                    catch (Exception e) { hideErrors.Add(new InvalidOperationException($"manual={manual}, delayed={delayed}: {e.Message}", e)); }
            if (hideErrors.Count > 0) throw new AggregateException(hideErrors);
            foreach (string presentation in new[] { "manual", "manual-report", "school-report" })
                WorkspaceStudyNotificationPriority(presentation, "_exitBusy", duringRead: true);
        }
        finally { SynchronizationContext.SetSynchronizationContext(previous); }
    }
    private static void StudyReturnCheck(bool manual)
    {
        string pipe = "NPEduTools.Test.return-study." + Guid.NewGuid().ToString("N");
        var source = manual ? ManualOperationState() : StudyFixture();
        bool projection = false;
        var display = new StudyDisplayService(() => ("isolated-return", true, source),
            projection: () => Volatile.Read(ref projection), inactivity: () => TimeSpan.Zero);
        void HideStudy()
        {
            var current = display.Snapshot();
            var reply = display.Handle(new(1, Guid.NewGuid(), "study.hide", StudyHide: new(current.Display!.InstanceId, current.Version!, current.Window!)));
            Assert(reply.Outcome == "Succeeded", "fixture failed to hide study");
        }
        HideStudy();
        using var stop = new CancellationTokenSource();
        var noise = new NoiseService(_ => throw new InvalidOperationException("No hardware allowed"), () => []);
        int returned = 0;
        var server = Task.Run(async () =>
        {
            try
            {
                while (!stop.IsCancellationRequested)
                {
                    using var stream = new NamedPipeServerStream(pipe, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                    await stream.WaitForConnectionAsync(stop.Token);
                    var request = await Protocol.ReadAsync<HostRequest>(stream, stop.Token);
                    Assert(Protocol.Validate(request) is null && request.Noise is null && request.ManualStudy is null,
                        "return UI sent a capture/session command");
                    HostResponse reply;
                    if (request.Capability is "study.show" or "manual.study.show")
                    {
                        Assert(request.Capability == (manual ? "manual.study.show" : "study.show"), "return crossed school/manual ownership");
                        reply = display.Handle(request with { Capability = "study.show" });
                        Interlocked.Increment(ref returned);
                    }
                    else reply = new(1, request.RequestId, "Succeeded", null, "isolated state", Noise: noise.Snapshot(), NoiseDevices: [],
                        Study: display.Snapshot(), ManualStudy: source.Manual);
                    await Protocol.WriteAsync(stream, reply, stop.Token);
                }
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        });
        Window window = manual ? new NoiseWindow(pipe) : new StudyMonitoringWindow(pipe, () => { });
        try
        {
            Exercise(window, owner =>
            {
                var button = (Button)window.FindName("ReturnStudyButton");
                PumpUntil(() => button.IsVisible && button.IsEnabled, "return button did not receive hidden study state");
                window.MinWidth = window.MinHeight = 0; window.Width = 760; window.Height = 520; window.UpdateLayout();
                var p = button.TranslatePoint(new Point(), window);
                Assert(p.X >= 0 && p.Y >= 0 && p.X + button.ActualWidth <= window.ActualWidth && p.Y + button.ActualHeight < window.ActualHeight - 4,
                    "return button clipped in compact layout");
                Snapshot(owner, manual ? "manual-return-study.png" : "school-return-study.png");
                button.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
                PumpUntil(() => display.Snapshot().Display!.Available && button.Visibility == Visibility.Collapsed, "return did not clear Host hide state");
                Assert(returned == 1 && source.Execution == display.Snapshot().Execution, "return duplicated request or changed collection");
                HideStudy();
                PumpUntil(() => button.IsVisible && button.IsEnabled, "button did not recover after a later hide");
                Volatile.Write(ref projection, true);
                button.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
                PumpUntil(() => returned == 2 && button.Visibility == Visibility.Collapsed, "projection rejection did not settle");
                Assert(display.Snapshot().Display!.HiddenRemainingSeconds > 0 && !display.Snapshot().Display!.Available,
                    "return overrode exam projection priority");
                Checks.Add((manual ? "Manual" : "School") + " WPF/private pipe: return uses exact current display identity, clears only hide, compact layout, projection race rejected; no hardware/session command");
                if (window is NoiseWindow signal) signal.Shutdown(); else window.Close();
            });
        }
        finally
        {
            if (window is NoiseWindow manualWindow) manualWindow.Shutdown(); else window.Close();
            stop.Cancel(); PumpUntil(() => server.IsCompleted, "return fixture did not stop"); server.GetAwaiter().GetResult();
            noise.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
    }
}

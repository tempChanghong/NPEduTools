using System.IO.Pipes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using NPEduTools.App;
using NPEduTools.Contracts;
using NPEduTools.Host;

internal static partial class Program
{
    private static void RunStudyManagementCloseChecks()
    {
        var errors = new List<Exception>();
        foreach (bool manual in new[] { false, true })
        foreach (bool shutdown in new[] { false, true })
        foreach (bool disconnect in new[] { false, true })
            try { StudyManagementCloseCheck(manual, shutdown, disconnect); }
            catch (Exception error) { errors.Add(new InvalidOperationException($"manual={manual}, shutdown={shutdown}, disconnect={disconnect}: {error.Message}", error)); }
        if (errors.Count > 0) throw new AggregateException(errors);
    }
    private static void StudyManagementCloseCheck(bool manual, bool shutdown, bool disconnect)
    {
        string pipe = "NPEduTools.Test.manage-close." + Guid.NewGuid().ToString("N");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(12));
        using var server = new NamedPipeServerStream(pipe, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        var source = manual ? ManualOperationState() : StudyFixture();
        var display = new StudyDisplayService(() => ("isolated-management-close", true, source), inactivity: () => TimeSpan.Zero);
        int opened = 0;
        // Formal navigation eventually restores MainWindow. Retain a closed owner
        // to exercise that same WPF boundary without starting its device pages.
        var closedMain = shutdown ? PrivateWorkspace(pipe) : null;
        var window = new StudyWindow(pipe, () => { opened++; closedMain?.RestoreWindow(); }, muted: true, manual: manual) { Width = 760, Height = 520 };
        var surface = (StudySurface)window.Content;
        var received = new TaskCompletionSource(); var release = new TaskCompletionSource();
        var unhandled = new List<Exception>();
        void OnError(object sender, DispatcherUnhandledExceptionEventArgs args) { unhandled.Add(args.Exception); args.Handled = true; }
        Application.Current.DispatcherUnhandledException += OnError;
        async Task Reply()
        {
            await server.WaitForConnectionAsync(timeout.Token);
            var request = await Protocol.ReadAsync<HostRequest>(server, timeout.Token);
            Assert(Protocol.Validate(request) is null && request.Capability == (manual ? "manual.study.hide" : "study.hide") && request.Noise is null && request.ManualStudy is null,
                "management navigation changed collection/session ownership");
            var reply = display.Handle(request with { Capability = "study.hide" });
            Assert(reply.Outcome == "Succeeded", "Host fixture did not accept hide");
            received.SetResult(); await release.Task.WaitAsync(timeout.Token);
            if (disconnect) server.Disconnect(); else await Protocol.WriteAsync(server, reply, timeout.Token);
        }
        Task? response = null;
        try
        {
            Exercise(window, _ =>
            {
                window.Apply(display.Snapshot(), null);
                response = Reply();
                ((Button)surface.FindName("ManageButton")).RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
                PumpUntil(() => received.Task.IsCompleted, "management hide did not reach the Host");
                if (shutdown) { closedMain?.Close(); window.Shutdown(); }
                release.SetResult();
                PumpUntil(() => response.IsCompleted && !(bool)typeof(StudySurface).GetField("_busy", WorkspaceFlags)!.GetValue(surface)!, "management hide did not settle");
                response.GetAwaiter().GetResult();
                Application.Current.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
                Assert(unhandled.Count == 0, "late management callback raised an unhandled UI exception: " + string.Join("; ", unhandled.Select(e => e.Message)));
                Assert(opened == (!shutdown && !disconnect ? 1 : 0), "closed or unconfirmed presentation opened management");
                if (!shutdown) Assert(window.IsVisible == disconnect, "ordinary management hide changed its confirmation behavior");
                Checks.Add($"Management close/manual={manual}, shutdown={shutdown}, disconnect={disconnect}: actual StudyWindow/private Host hide confirmation respects shutdown and transport uncertainty; valid navigation opens once, no capture commands");
                window.Shutdown();
            });
        }
        finally
        {
            release.TrySetResult(); window.Shutdown(); closedMain?.Close();
            Application.Current.DispatcherUnhandledException -= OnError;
        }
    }
}

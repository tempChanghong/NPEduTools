using System.IO.Pipes;
using System.Windows;
using System.Windows.Threading;
using NPEduTools.App;
using NPEduTools.Contracts;
using NPEduTools.Host;

internal static partial class Program
{
    private static void RunStudyIdleHideChecks()
    {
        var previous = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Application.Current.Dispatcher));
        try
        {
            var errors = new List<Exception>();
            foreach (string scenario in new[] { "school", "manual-ten", "manual-never", "over-limit", "contradictory" })
                try { StudyIdleHideCheck(scenario); } catch (Exception e) { errors.Add(new InvalidOperationException(scenario + ": " + e.Message, e)); }
            if (errors.Count > 0) throw new AggregateException(errors);
        }
        finally { SynchronizationContext.SetSynchronizationContext(previous); }
    }
    private static void StudyIdleHideCheck(string scenario)
    {
        string pipe = "NPEduTools.Test.idle-hide." + Guid.NewGuid().ToString("N");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var server = new NamedPipeServerStream(pipe, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        bool manual = scenario.StartsWith("manual-", StringComparison.Ordinal);
        var clock = new StudyUiClock();
        var source = StudyFixture();
        if (manual) source = source with { Mode = "Manual", ClockSource = "Windows", Execution = source.Execution! with { Owner = "Manual" },
            Manual = new(Guid.NewGuid(), 1, source.Execution!.SessionId, "Active", null, null, null, true, scenario == "manual-never" ? null : 600) };
        var display = new StudyDisplayService(() => ("isolated", true, source), clock, inactivity: () => TimeSpan.Zero);
        var state = display.Snapshot();
        var window = new StudyWindow(pipe, () => { }, muted: true, time: clock, manual: manual) { Width = 640, Height = 480 };
        try
        {
            Exercise(window, _ =>
            {
                window.Apply(state, null);
                async Task Reply()
                {
                    await server.WaitForConnectionAsync(timeout.Token);
                    var request = await Protocol.ReadAsync<HostRequest>(server, timeout.Token);
                    Assert(request.Capability == (manual ? "manual.study.hide" : "study.hide") && request.Noise is null && request.ManualStudy is null,
                        "hide touched collection instead of presentation");
                    var response = display.Handle(request with { Capability = "study.hide" });
                    Assert(response.Outcome == "Succeeded", "actual Host display rejected fixture hide");
                    var confirmation = response.Study!.Display!;
                    Assert(confirmation.HiddenRemainingSeconds == (scenario == "manual-never" ? 10801 : 600), "Host idle policy was not exercised");
                    if (scenario == "over-limit") response = response with { Study = response.Study! with { Display = confirmation with { HiddenRemainingSeconds = 601 } } };
                    if (scenario == "contradictory") response = response with { Study = response.Study! with { Display = confirmation with { HiddenRemainingSeconds = 180, Available = true } } };
                    await Protocol.WriteAsync(server, response, timeout.Token);
                }
                var reply = Reply(); var hide = ((StudySurface)window.Content).HideAsync();
                PumpUntil(() => reply.IsCompleted && hide.IsCompleted, "idle hide did not settle");
                reply.GetAwaiter().GetResult(); hide.GetAwaiter().GetResult();
                Assert(window.IsVisible == (scenario is "over-limit" or "contradictory"), "idle hide receipt did not match current Host rules");
                Checks.Add(scenario + ": actual Host idle-hide receipt/private pipe controls actual study window; ten minutes/no-auto-return accepted, inconsistent receipts rejected, no capture");
                window.Shutdown();
            });
        }
        finally { window.Shutdown(); }
    }
}

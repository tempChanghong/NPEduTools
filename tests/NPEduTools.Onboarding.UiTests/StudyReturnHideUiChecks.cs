using System.IO.Pipes;
using System.Windows;
using NPEduTools.App;
using NPEduTools.Contracts;
using NPEduTools.Host;

internal static partial class Program
{
    private static void StudyHideAfterReturnCheck(bool manual, bool delayed)
    {
        string pipe = "NPEduTools.Test.return-hide." + Guid.NewGuid().ToString("N");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(12));
        using var server = new NamedPipeServerStream(pipe, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        var source = manual ? ManualOperationState() : StudyFixture();
        var display = new StudyDisplayService(() => ("isolated-return-hide", true, source), inactivity: () => TimeSpan.Zero);
        var window = new StudyWindow(pipe, () => { }, muted: true, manual: manual) { Width = 760, Height = 520 };
        var surface = (StudySurface)window.Content;
        var received = new TaskCompletionSource<HostRequest>();
        var release = new TaskCompletionSource();
        async Task<HostRequest> HandleHide(bool dropReply, bool delayReply = false)
        {
            await server.WaitForConnectionAsync(timeout.Token);
            var request = await Protocol.ReadAsync<HostRequest>(server, timeout.Token);
            Assert(Protocol.Validate(request) is null && request.Capability == (manual ? "manual.study.hide" : "study.hide") &&
                request.Noise is null && request.ManualStudy is null, "hide changed capture/session ownership");
            var reply = display.Handle(request with { Capability = "study.hide" });
            if (delayReply) { received.SetResult(request); await release.Task.WaitAsync(timeout.Token); }
            if (dropReply) server.Disconnect();
            else await Protocol.WriteAsync(server, reply, timeout.Token);
            return request;
        }
        try
        {
            Exercise(window, _ =>
            {
                window.Apply(display.Snapshot(), null); window.ShowDisplay();
                var lost = HandleHide(!delayed, delayed); var firstHide = surface.HideAsync();
                PumpUntil(() => delayed ? received.Task.IsCompleted : lost.IsCompleted && firstHide.IsCompleted,
                    "first hide request did not reach the Host");
                var first = delayed ? received.Task.GetAwaiter().GetResult() : lost.GetAwaiter().GetResult();
                if (!delayed)
                {
                    firstHide.GetAwaiter().GetResult();
                    var retryRead = HandleHide(true); var retry = surface.HideAsync();
                    PumpUntil(() => retryRead.IsCompleted && retry.IsCompleted, "uncertain same-view retry did not settle");
                    Assert(retryRead.GetAwaiter().GetResult().RequestId == first.RequestId, "ordinary uncertain retry renewed its identity");
                    retry.GetAwaiter().GetResult();
                }
                Assert(display.Snapshot().Display!.HiddenRemainingSeconds > 0 && window.IsVisible,
                    "fixture did not preserve an uncertain but Host-applied hide");
                // Main polling hides the window without applying its negative snapshot.
                window.HideDisplay();
                var hidden = display.Snapshot();
                var shown = display.Handle(new(1, Guid.NewGuid(), "study.show",
                    StudyHide: new(hidden.Display!.InstanceId, hidden.Version!, hidden.Window!))).Study!;
                Assert(shown.Display!.Available, "Host did not authorize explicit return");
                window.Apply(shown, null); window.ShowDisplay();
                if (delayed)
                {
                    release.SetResult();
                    PumpUntil(() => lost.IsCompleted && firstHide.IsCompleted, "late hide acknowledgement did not settle");
                    lost.GetAwaiter().GetResult(); firstHide.GetAwaiter().GetResult();
                    Assert(window.IsVisible && display.Snapshot().Display!.Available, "late hide reply withdrew the returned display");
                    server.Disconnect();
                }
                var next = HandleHide(false); var secondHide = surface.HideAsync();
                PumpUntil(() => next.IsCompleted && secondHide.IsCompleted, "hide after explicit return did not settle");
                var second = next.GetAwaiter().GetResult(); secondHide.GetAwaiter().GetResult();
                Assert(second.RequestId != first.RequestId && !window.IsVisible && display.Snapshot().Display!.HiddenRemainingSeconds > 0,
                    "return reused the lost hide request: Host deduplicated it and a second long press was required");
                Checks.Add((manual ? "Manual" : "School") + $" private-pipe/WPF, delayed={delayed}: uncertain retries retain identity; Host return retires the old hide, late acknowledgements cannot withdraw it, and the next hide succeeds once; no capture/session command");
                window.Shutdown();
            });
        }
        finally { release.TrySetResult(); window.Shutdown(); }
    }
}

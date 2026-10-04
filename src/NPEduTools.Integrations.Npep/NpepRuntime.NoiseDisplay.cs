using System.Diagnostics;
using System.Text.Json.Nodes;

namespace NPEduTools.Integrations.Npep;

public sealed partial class NpepRuntime
{
    private Task _noiseDisplayLoop = Task.CompletedTask;
    private void InitializeNoiseDisplay(INpepNoiseDisplay? display)
    {
        if (display is null || _device is null) return;
        _noiseDisplayLoop = Task.Run(async () =>
        {
            while (!_lifetime.IsCancellationRequested)
            {
                try
                {
                    var state = Snapshot();
                    if (state.State is "UNPAIRED" or "SUSPENDED" || state.ReportingPaused) display.BindDisplay(null);
                    else if (state is { State: "ACTIVE", Connection: "ONLINE", Busy: false })
                        await NoiseDisplayCycleAsync(display, _lifetime.Token);
                }
                catch (Exception e) when (Handled(e) || e is InvalidDataException)
                { display.DisplayFailed(e is NpepException n ? n.Code : "DISPLAY_OFFLINE"); }
                try { await Task.Delay(TimeSpan.FromSeconds(3), _lifetime.Token); }
                catch (OperationCanceledException) { break; }
            }
        });
    }
    internal async Task NoiseDisplayCycleAsync(INpepNoiseDisplay display, CancellationToken token)
    {
        var context = await _device!.ControlContextAsync(Guid.Empty, token);
        display.BindDisplay(context["identity"]!.ToJsonString());
        var body = display.ObserveDisplay();
        if (body is null) return;
        var pending = display.PendingDisplayReturn();
        body["requestId"] = pending?["requestId"]?.DeepClone() ?? JsonValue.Create(NpepProtocol.Id());
        body["context"] = context.DeepClone();
        if (pending is not null)
        {
            body["offlineStartedAt"] = pending["offlineStartedAt"]!.DeepClone();
            body["returnMinutes"] = pending["returnMinutes"]!.DeepClone();
        }
        long sent = Stopwatch.GetTimestamp();
        var reply = await _device.NoiseDisplayAsync(pending is null ? "observe" : "return", body, token);
        var latest = await _device.ControlContextAsync(Guid.Empty, token);
        if (!JsonNode.DeepEquals(context, latest) || Snapshot() is not { State: "ACTIVE", ReportingPaused: false, Busy: false })
            throw new NpepException("SESSION_SUPERSEDED");
        display.ConfirmDisplay(body, reply, Stopwatch.GetElapsedTime(sent).TotalSeconds);
    }
}

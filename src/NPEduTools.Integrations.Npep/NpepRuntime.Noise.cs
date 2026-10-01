using System.Diagnostics;
using System.Text.Json.Nodes;

namespace NPEduTools.Integrations.Npep;

public sealed partial class NpepRuntime
{
    private Task _noiseLoop = Task.CompletedTask;
    private long _noiseSequence;
    private void InitializeNoise(INpepNoise? noise)
    {
        if (noise is null || _device is null) return;
        _noiseLoop = Task.Run(async () =>
        {
            while (!_lifetime.IsCancellationRequested)
            {
                try
                {
                    var state = Snapshot();
                    if (state.State is "UNPAIRED" or "SUSPENDED") noise.Bind(null);
                    if (state is { State: "ACTIVE", Connection: "ONLINE", ReportingPaused: false, Busy: false })
                        await NoiseCycleAsync(noise, _lifetime.Token);
                    else noise.Observe(); // Keeps bounded crash checkpoints while offline; never starts capture.
                }
                catch (Exception e) when (Handled(e) || e is InvalidDataException) { /* Retry transport; effects are journalled by the Host. */ }
                try { await Task.Delay(TimeSpan.FromSeconds(3), _lifetime.Token); }
                catch (OperationCanceledException) { break; }
            }
        });
    }

    internal async Task NoiseCycleAsync(INpepNoise noise, CancellationToken token)
    {
        var context = await _device!.ControlContextAsync(Guid.Empty, token);
        noise.Bind(context["identity"]!.ToJsonString());
        long sent = Stopwatch.GetTimestamp();
        var reply = await _device.NoiseAsync(new JsonObject {
            ["requestId"] = NpepProtocol.Id(), ["context"] = context.DeepClone(), ["sequence"] = ++_noiseSequence,
            ["status"] = noise.Observe(), ["reports"] = noise.Reports(), ["receipts"] = noise.Receipts()
        }, token);
        var latest = await _device.ControlContextAsync(Guid.Empty, token);
        if (!JsonNode.DeepEquals(context, latest)) throw new NpepException("SESSION_SUPERSEDED");
        noise.Acknowledge(reply);
        if (reply["command"] is not JsonObject command) return;
        double remaining = (DateTimeOffset.Parse(command.Text("expiresAt")) - DateTimeOffset.Parse(reply.Text("serverTime"))).TotalSeconds;
        noise.Execute(command, () => {
            if (Snapshot() is not { State: "ACTIVE", Connection: "ONLINE", ReportingPaused: false, Busy: false }) throw new NpepException("CONTROL_OFFLINE");
            if (Stopwatch.GetElapsedTime(sent).TotalSeconds >= remaining) throw new NpepException("COMMAND_EXPIRED");
        });
    }
}

using System.Text.Json.Nodes;

namespace NPEduTools.Integrations.Npep;

public sealed partial class NpepRuntime
{
    private Task _noiseScheduleLoop = Task.CompletedTask;
    private long _noiseScheduleSequence;
    private void InitializeNoiseSchedules(INpepNoiseSchedules? schedules)
    {
        if (schedules is null || _device is null) return;
        _noiseScheduleLoop = Task.Run(async () =>
        {
            try
            {
                while (!_lifetime.IsCancellationRequested)
                {
                    try
                    {
                        var state = Snapshot();
                        if (state.State is "UNPAIRED" or "SUSPENDED") schedules.Bind(null, false);
                        else if (state.ReportingPaused || state.Busy) schedules.Bind(_scheduleScope, false);
                        else if (state is { State: "ACTIVE", Connection: "ONLINE" }) await NoiseScheduleCycleAsync(schedules, _lifetime.Token);
                        // Offline retains the existing monotonic lease; the Host still checks school time.
                    }
                    catch (Exception e) when (Handled(e) || e is InvalidDataException)
                    {
                        if (e is NpepException { Status: 401 or 403 }) schedules.Bind(null, false);
                        else if (e is NpepException { Code: "NOISE_SCHEDULE_UNSUPPORTED" or "INVALID_RESPONSE" or "BINDING_CHANGED" or "INSTANCE_MISMATCH" })
                            schedules.Bind(_scheduleScope, false);
                    }
                    await Task.Delay(TimeSpan.FromSeconds(3), _lifetime.Token);
                }
            }
            catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
            finally { schedules.Bind(_scheduleScope, false); } // Shutdown preserves user skips for the next Host.
        });
    }
    private string? _scheduleScope;
    internal async Task NoiseScheduleCycleAsync(INpepNoiseSchedules schedules, CancellationToken token)
    {
        var context = await _device!.ControlContextAsync(Guid.Empty, token);
        _scheduleScope = context["identity"]!.ToJsonString();
        schedules.Bind(_scheduleScope, true);
        var reply = await _device.NoiseScheduleAsync(new JsonObject {
            ["requestId"] = NpepProtocol.Id(), ["context"] = context.DeepClone(), ["sequence"] = ++_noiseScheduleSequence,
            ["status"] = schedules.Observe()
        }, token);
        var latest = await _device.ControlContextAsync(Guid.Empty, token);
        if (!JsonNode.DeepEquals(context, latest) || Snapshot() is not {State:"ACTIVE",ReportingPaused:false,Busy:false})
            throw new NpepException("SESSION_SUPERSEDED");
        schedules.Confirm(reply);
    }
}

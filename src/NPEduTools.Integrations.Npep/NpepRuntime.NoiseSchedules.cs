using System.Diagnostics;
using System.Text.Json.Nodes;

namespace NPEduTools.Integrations.Npep;

public sealed partial class NpepRuntime
{
    private Task _noiseScheduleLoop = Task.CompletedTask;
    private long _noiseScheduleSequence;
    private StudyRetry? _noiseScheduleRetry;
    private void InitializeNoiseSchedules(INpepNoiseSchedules? schedules)
    {
        if (schedules is null || _device is null) return;
        _noiseScheduleLoop = Task.Run(async () =>
        {
            try
            {
                while (!_lifetime.IsCancellationRequested)
                {
                    var state = Snapshot();
                    try
                    {
                        if (state.State is "UNPAIRED" or "SUSPENDED") schedules.Bind(null, false);
                        else if (state.ReportingPaused || state.Busy && !ContinuingStudyResume) schedules.Bind(_scheduleScope, false);
                        else if (state is { State: "ACTIVE", Connection: "ONLINE", Busy: false })
                            await NoiseScheduleCycleAsync(schedules, state.Revision, _lifetime.Token);
                        // Offline retains the existing monotonic lease; the Host still checks school time.
                    }
                    catch (Exception e) when (Handled(e) || e is InvalidDataException)
                    {
                        lock (_sync)
                        {
                            if (_state.Revision == state.Revision && !_lifetime.IsCancellationRequested)
                            {
                                if (e is NpepException { Status: 401 or 403 }) schedules.Bind(null, false);
                                else if (e is NpepException { Status: not (429 or >= 500),
                                    Code: "NOISE_SCHEDULE_UNSUPPORTED" or "INVALID_RESPONSE" or "BINDING_CHANGED" or "INSTANCE_MISMATCH" })
                                    schedules.Bind(_scheduleScope, false);
                            }
                        }
                        if (e is NpepException { Status: 401 } refusal && Snapshot().Revision == state.Revision)
                            Publish("STOPPED", "设备授权已失效，学校自习安排同步已停止。", refusal.Code, requireSuspended: true);
                    }
                    await Task.Delay(TimeSpan.FromSeconds(3), _lifetime.Token);
                }
            }
            catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
            finally { schedules.Bind(_scheduleScope, false); } // Shutdown preserves user skips for the next Host.
        });
    }
    private string? _scheduleScope;
    internal Task NoiseScheduleCycleAsync(INpepNoiseSchedules schedules, CancellationToken token) =>
        NoiseScheduleCycleAsync(schedules, Snapshot().Revision, token);

    private async Task NoiseScheduleCycleAsync(INpepNoiseSchedules schedules, long revision, CancellationToken token)
    {
        var context = await _device!.ControlContextAsync(Guid.Empty, token);
        string key = context.ToJsonString();
        if (StudyRetryPending(_noiseScheduleRetry, key)) return;
        JsonObject? status = null;
        UpdateCurrentStudyCycle(revision, token, () =>
        {
            _scheduleScope = context["identity"]!.ToJsonString();
            schedules.Bind(_scheduleScope, true);
            status = schedules.Observe();
        });
        long sent = Stopwatch.GetTimestamp();
        JsonObject reply;
        try
        {
            reply = await _device.NoiseScheduleAsync(new JsonObject
            {
                ["requestId"] = NpepProtocol.Id(), ["context"] = context, ["sequence"] = ++_noiseScheduleSequence,
                ["status"] = status
            }, () => UpdateCurrentStudyCycle(revision, token), token);
        }
        catch (NpepException e) when (e.Code == "NOISE_SCHEDULE_UNSUPPORTED" || e.Status is 429 or >= 500)
        {
            UpdateCurrentStudyCycle(revision, token);
            _noiseScheduleRetry = e.Code == "NOISE_SCHEDULE_UNSUPPORTED"
                ? new StudyRetry(key, _receiptTime.GetTimestamp(), TimeSpan.FromSeconds(60))
                : NewStudyRetry(key, e);
            throw;
        }
        UpdateCurrentStudyCycle(revision, token, () => schedules.Confirm(reply, Stopwatch.GetElapsedTime(sent)));
        _noiseScheduleRetry = null;
    }
}

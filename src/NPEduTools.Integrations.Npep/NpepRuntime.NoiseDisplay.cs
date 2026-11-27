using System.Diagnostics;
using System.Text.Json.Nodes;

namespace NPEduTools.Integrations.Npep;

public sealed partial class NpepRuntime
{
    private Task _noiseDisplayLoop = Task.CompletedTask;
    private StudyRetry? _displayObserveRetry, _displayReturnRetry;
    private void InitializeNoiseDisplay(INpepNoiseDisplay? display)
    {
        if (display is null || _device is null) return;
        _noiseDisplayLoop = Task.Run(async () =>
        {
            while (!_lifetime.IsCancellationRequested)
            {
                var state = Snapshot();
                try
                {
                    if (state.State is "UNPAIRED" or "SUSPENDED" || state.ReportingPaused)
                    {
                        lock (_sync)
                            if (_state.Revision == state.Revision) display.BindDisplay(null);
                    }
                    else if (state is { State: "ACTIVE", Connection: "ONLINE", Busy: false })
                        await NoiseDisplayCycleAsync(display, _lifetime.Token);
                }
                catch (Exception e) when (Handled(e) || e is InvalidDataException)
                {
                    if (_lifetime.IsCancellationRequested) break;
                    lock (_sync)
                        if (_state.Revision == state.Revision) display.DisplayFailed(e is NpepException n ? n.Code : "DISPLAY_OFFLINE");
                    if (e is NpepException { Status: 401 } refusal && Snapshot().Revision == state.Revision)
                        Publish("STOPPED", "设备授权已失效，网页展示同步已停止。", refusal.Code, requireSuspended: true);
                }
                try { await Task.Delay(TimeSpan.FromSeconds(3), _lifetime.Token); }
                catch (OperationCanceledException) { break; }
            }
        });
    }
    internal async Task NoiseDisplayCycleAsync(INpepNoiseDisplay display, CancellationToken token)
    {
        long revision = Snapshot().Revision;
        void UpdateCurrentDisplay(Action? update = null)
        {
            lock (_sync)
            {
                if (_state is { State: "ACTIVE", Connection: "ONLINE", ReportingPaused: false, Busy: false } &&
                    _state.Revision == revision && HasFreshStatusReceipt() && !token.IsCancellationRequested)
                {
                    update?.Invoke();
                    return;
                }
            }
            token.ThrowIfCancellationRequested();
            throw new NpepException("SESSION_SUPERSEDED");
        }

        var context = await _device!.ControlContextAsync(Guid.Empty, token);
        JsonObject? body = null, pending = null;
        UpdateCurrentDisplay(() =>
        {
            display.BindDisplay(context["identity"]!.ToJsonString());
            body = display.ObserveDisplay();
            if (body is not null) pending = display.PendingDisplayReturn();
        });
        if (body is null) return;
        string key = context.ToJsonString();
        bool returning = pending is not null;
        if (StudyRetryPending(returning ? _displayReturnRetry : _displayObserveRetry, key)) return;
        body["requestId"] = pending?["requestId"]?.DeepClone() ?? JsonValue.Create(NpepProtocol.Id());
        body["context"] = context.DeepClone();
        if (pending is not null)
        {
            body["offlineStartedAt"] = pending["offlineStartedAt"]!.DeepClone();
            body["returnMinutes"] = pending["returnMinutes"]!.DeepClone();
        }
        long sent = Stopwatch.GetTimestamp();
        JsonObject reply;
        try { reply = await _device.NoiseDisplayAsync(returning ? "return" : "observe", body, () => UpdateCurrentDisplay(), token); }
        catch (NpepException e) when (e.Code == "DISPLAY_UNSUPPORTED" || e.Status is 429 or >= 500)
        {
            UpdateCurrentDisplay();
            var retry = e.Code == "DISPLAY_UNSUPPORTED"
                ? new StudyRetry(key, _receiptTime.GetTimestamp(), TimeSpan.FromSeconds(60))
                : NewStudyRetry(key, e);
            if (returning) _displayReturnRetry = retry;
            else _displayObserveRetry = retry;
            throw;
        }
        UpdateCurrentDisplay(() => display.ConfirmDisplay(body, reply, Stopwatch.GetElapsedTime(sent).TotalSeconds));
        if (returning) _displayReturnRetry = null;
        else _displayObserveRetry = null;
    }
}

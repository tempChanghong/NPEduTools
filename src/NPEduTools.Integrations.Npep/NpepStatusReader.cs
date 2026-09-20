using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using NPEduTools.Contracts;

namespace NPEduTools.Integrations.Npep;

public sealed record NpepSample(JsonObject Status, long StartedAt)
{
    public int AgeMs => (int)Math.Min(int.MaxValue, Stopwatch.GetElapsedTime(StartedAt).TotalMilliseconds);
}

/// <summary>Reads an already-running Host. Never launches, probes, configures or commands software.</summary>
public static class NpepStatusReader
{
    public static async Task<NpepSample> ReadAsync(string? pipeName, string appVersion, CancellationToken token = default)
    {
        long started = Stopwatch.GetTimestamp();
        HostResponse? snapshot = null;
        if (!string.IsNullOrWhiteSpace(pipeName))
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
            deadline.CancelAfter(TimeSpan.FromSeconds(2));
            try { snapshot = await HostClient.RequestAsync(pipeName, "host.cached-status", deadline.Token); }
            catch (Exception e) when (e is IOException or TimeoutException or JsonException or OperationCanceledException)
            { token.ThrowIfCancellationRequested(); }
        }
        return new(Map(snapshot, appVersion), started);
    }

    public static JsonObject Map(HostResponse? response, string appVersion)
    {
        if (response?.Outcome != "Succeeded") response = null;
        var mode = response?.ClassroomMode;
        var clock = response?.SchoolClock;
        string ci = clock is null ? "UNKNOWN" : clock.State switch
        {
            "Advancing" or "WarmingUp" or "Discontinuous" or "FrozenSuspected" when
                clock.BridgeInstanceId != Guid.Empty && double.IsFinite(clock.AgeMs) && clock.AgeMs is >= 0 and < SchoolClockFrame.MaxAgeMs => "READY",
            "Stale" => "DISCONNECTED",
            _ => "UNKNOWN"
        };
        string ea = response?.ExamAware?.BridgeState switch { "Connected" => "READY", "Disconnected" => "DISCONNECTED", _ => "UNKNOWN" };
        var status = new JsonObject
        {
            ["appVersion"] = appVersion,
            ["mode"] = mode?.Mode switch { "Daily" => "DAILY", "Exam" => "EXAM", "Unconfigured" => "UNCONFIGURED", _ => "UNKNOWN" },
            ["modePhase"] = mode?.Phase switch
            {
                "Idle" => "IDLE", "Checking" => "CHECKING", "Switching" => "SWITCHING", "Running" => "RUNNING",
                "Incomplete" => "INCOMPLETE", "Unavailable" => "UNAVAILABLE", _ => "UNKNOWN"
            },
            ["modeRevision"] = mode?.Revision is >= 0 and <= NpepProtocol.MaxInteger ? JsonValue.Create(mode.Revision) : null,
            ["automaticRecording"] = response?.Automatic is { } automatic ? automatic.Enabled ? "ENABLED" : "DISABLED" : "UNKNOWN",
            ["recording"] = response?.Recording?.Phase switch
            {
                "Idle" or "Saved" => "IDLE", "Recording" => "RECORDING", "Paused" => "PAUSED", "Saving" => "FINALIZING", _ => "UNKNOWN"
            },
            // Existing caches expose application versions, not bridge package versions. Do not substitute them.
            ["classIsland"] = new JsonObject { ["connection"] = ci, ["bridgeVersion"] = null },
            ["examAware"] = new JsonObject { ["connection"] = ea, ["bridgeVersion"] = null }
        };
        NpepProtocol.Validate("status", status);
        return status;
    }
}

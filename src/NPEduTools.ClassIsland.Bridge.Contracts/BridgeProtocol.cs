#nullable enable
using System.Text.Json;
using System.Text.Json.Serialization;
using dotnetCampus.Ipc.CompilerServices.Attributes;

namespace NPEduTools.ClassIsland.Bridge.Contracts;

// P0 is deliberately separate from the final recording contract: read-only, no control methods.
[IpcPublic(IgnoresIpcException = false, Timeout = 2000)]
public interface IRecordingBridgeP0
{
    Task<string> GetHelloAsync();
    Task<string> GetSnapshotAsync();
}

// Optional additive capability; old clock clients keep their original generated joint identity.
[IpcPublic(IgnoresIpcException = false, Timeout = 2000)]
public interface IRecordingBridgeCalendar
{
    Task<string> GetDayAsync(string date);
}
public sealed record BridgeCalendarReply(int ProtocolVersion, Guid BridgeInstanceId, string? SchoolNow,
    string RequestedDate, string Status, bool Forecast, BridgeDay? Day);

public static class BridgeProtocol
{
    public const int Version = 1;
    public const int MaxBytes = 64 * 1024;
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    { UnmappedMemberHandling = JsonUnmappedMemberHandling.Skip };
    public static string Encode<T>(T value)
    {
        string json = JsonSerializer.Serialize(value, Json);
        if (System.Text.Encoding.UTF8.GetByteCount(json) > MaxBytes) throw new InvalidDataException("Bridge response exceeds 64 KiB.");
        return json;
    }
}

public sealed record BridgeHello(int ProtocolVersion, string Contract, Guid BridgeInstanceId,
    string PluginVersion, string ClassIslandVersion, string Lifecycle, string[] Capabilities);
public sealed record BridgeLesson(int Number, Guid SubjectId, string Subject, string Start, string End, bool Enabled);
public sealed record BridgeDay(string Date, Guid ProfileId, Guid? PlanId, Guid? LayoutId, string Name,
    string Status, string Revision, BridgeLesson[] Lessons);
public sealed record BridgeSnapshot(int ProtocolVersion, Guid BridgeInstanceId, string Lifecycle, long Sequence,
    long ClockEpoch, string? EffectiveLocalDateTime, double SampleAgeMs, string ClockState,
    bool LessonTimerRunning, BridgeDay? Day, string? Error);

/// <summary>Uses local elapsed time, never the Windows calendar, to detect observed clock discontinuity.</summary>
public sealed class ClockObservation
{
    private DateTime? _last;
    private double _lastMs, _lastAdvanceMs, _stableSince;
    private int _stableSamples;
    public long Epoch { get; private set; }
    public string State { get; private set; } = "WarmingUp";
    public void Observe(DateTime schoolTime, double elapsedMs)
    {
        if (_last is null)
        { _lastAdvanceMs = _stableSince = elapsedMs; _stableSamples = 1; }
        else
        {
            double delta = (schoolTime - _last.Value).TotalMilliseconds;
            double elapsed = elapsedMs - _lastMs;
            if (delta < -1 || Math.Abs(delta - elapsed) > 2000 && delta != 0)
            {
                Epoch++; State = "Discontinuous"; _stableSamples = 0;
                _stableSince = _lastAdvanceMs = elapsedMs;
            }
            else if (delta <= 0)
            {
                if (elapsedMs - _lastAdvanceMs >= 2000)
                { State = "FrozenSuspected"; _stableSamples = 0; _stableSince = elapsedMs; }
            }
            else
            {
                _lastAdvanceMs = elapsedMs;
                if (_stableSamples++ == 0) { _stableSince = elapsedMs; State = "WarmingUp"; }
                if (_stableSamples >= 3 && elapsedMs - _stableSince >= 500) State = "Advancing";
            }
        }
        _last = schoolTime; _lastMs = elapsedMs;
    }
}

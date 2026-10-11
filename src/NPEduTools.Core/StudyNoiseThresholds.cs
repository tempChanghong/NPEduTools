using NPEduTools.Contracts;

namespace NPEduTools.Core;

/// <summary>Processes every energy frame; UI polling never adds continuous time. No PCM retention.</summary>
public sealed class StudyNoiseThresholds(TimeProvider? time = null)
{
    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private Guid? _session;
    private StudyNoise? _policy;
    private long? _last, _highAt;
    private double _sampled, _firstSeconds;
    private string _band = "None";
    public void Configure(Guid? session, StudyNoise? policy)
    {
        if (session == _session && policy == _policy) return;
        _session = session; _policy = policy; _last = null; _band = "None"; ResetHigh();
    }
    private void ResetHigh() { _highAt = null; _sampled = _firstSeconds = 0; }
    public void Receive(Guid session, NoiseFrame frame, double? dbfs, bool invalid)
    {
        if (session != _session || _policy is not { Enabled: true, High: { } high }) return;
        long now = _time.GetTimestamp();
        bool gap = _last is { } previous && _time.GetElapsedTime(previous, now).TotalSeconds > .5;
        _last = now;
        if (gap) ResetHigh();
        _band = !invalid && dbfs is { } level && double.IsFinite(level)
            ? level >= high ? "High" : _policy.Medium is { } medium && level >= medium ? "Medium" : "None" : "None";
        if (_band != "High") { ResetHigh(); return; }
        if (_highAt is null) { _highAt = now; _firstSeconds = frame.Seconds; }
        _sampled += frame.Seconds;
    }
    public (string Band, bool Qualified) Snapshot()
    {
        if (_last is not { } last || _time.GetElapsedTime(last).TotalSeconds is < 0 or >= 1) return ("None", false);
        // Both sampled duration and monotonic duration are required: a buffered burst is not five elapsed seconds.
        bool qualified = _band == "High" && _policy is { HighContinuousSeconds: > 0 } p && _highAt is { } at &&
            _time.GetElapsedTime(last).TotalSeconds <= .5 &&
            _sampled + 1e-9 >= p.HighContinuousSeconds &&
            _time.GetElapsedTime(at).TotalSeconds + _firstSeconds + 1e-9 >= p.HighContinuousSeconds;
        return (_band, qualified);
    }
}

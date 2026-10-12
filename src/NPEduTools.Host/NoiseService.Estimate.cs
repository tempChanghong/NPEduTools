using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using NPEduTools.Contracts;
using NPEduTools.Core;

namespace NPEduTools.Host;

public sealed partial class NoiseService
{
    // Persistent tombstone: switching back to an old gain/device cannot silently reactivate a reference.
    private string? _referenceInvalidated;
    private static string ProfileId(NoiseReferenceProfile p) => Convert.ToHexStringLower(SHA256.HashData(
        Encoding.UTF8.GetBytes(JsonSerializer.Serialize(p, Protocol.Json))));
    private void InvalidateReference(string reason)
    {
        if (!_referenceLoaded && (ReferencePath is not { } path || !File.Exists(path))) return;
        LoadReference();
        if (_referenceProfile is null || _referenceInvalidated is not null) return;
        // Selection changes can invalidate a reference before any capture exists.
        // Establish durable evidence before changing that binding too.
        EnsureReferenceUseMarker();
        _referenceInvalidated = reason;
        SaveReference(_referenceDraft, _referenceProfile);
    }
    private void CheckReferenceInput(Session s)
    {
        // Calibration captures also witness input changes. Checking metadata needs no extra DSP.
        if (s.Stop.IsCancellationRequested || s.Calibration is null && s.Reference is null) return;
        LoadReference();
        if (_referenceInvalidated is not null || _referenceProfile is not { } p ||
            s.Metadata is not IReferenceNoiseCapture capture) return;
        try
        {
            if (p.DeviceId != s.DeviceId) InvalidateReference("DEVICE_CHANGED");
            else if (capture.ReferenceSignature != p.Signature) InvalidateReference("INPUT_SETTINGS_CHANGED");
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        { InvalidateReference("INPUT_SETTINGS_UNAVAILABLE"); }
    }
    private void PrepareEstimate(Session s, INoiseCapture capture)
    {
        LoadReference();
        if (s.Calibration is not null || s.Reference is not null || _referenceProfile is not { } p) return;
        s.EstimateProfile = p;
        s.EstimateReferenceNote = NoiseReferenceCalibration.Describe(p);
        s.EstimateProfileId = ProfileId(p);
        s.EstimateReason = _referenceError ?? _referenceInvalidated;
        if (s.EstimateReason is not null) return;
        if (p.DeviceId != s.DeviceId) { InvalidateReference("DEVICE_CHANGED"); s.EstimateReason = _referenceInvalidated; return; }
        if (capture is not IReferenceNoiseCapture weighted) { s.EstimateReason = "A_WEIGHTING_UNAVAILABLE"; return; }
        s.EstimateCapture = weighted;
        try
        {
            if (weighted.ReferenceSignature != p.Signature)
            { InvalidateReference("INPUT_SETTINGS_CHANGED"); s.EstimateReason = _referenceInvalidated; return; }
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        { InvalidateReference("INPUT_SETTINGS_UNAVAILABLE"); s.EstimateReason = _referenceInvalidated; return; }
        try
        {
            weighted.EnableReference();
            weighted.ReferenceFrame += frame => ReceiveEstimate(s, frame);
        }
        catch (Exception e) when (e is not OutOfMemoryException) { s.EstimateReason = "A_WEIGHTING_UNAVAILABLE"; }
    }
    private void CheckEstimateInput(Session s)
    {
        if (s.Stop.IsCancellationRequested) return; // Do not inspect a COM endpoint while normal cleanup disposes it.
        if (s.EstimateProfile is not { } p || s.EstimateReason is not null || s.EstimateCapture is null) return;
        try
        {
            if (s.EstimateCapture.ReferenceSignature == p.Signature) return;
            InvalidateReference("INPUT_SETTINGS_CHANGED"); s.EstimateReason = _referenceInvalidated;
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        { InvalidateReference("INPUT_SETTINGS_UNAVAILABLE"); s.EstimateReason = _referenceInvalidated; }
        s.EstimateCurrent = null; s.EstimateQuality = "Invalidated";
        if (s.ThresholdPolicy?.Estimated is not null) _studyThresholds.Configure(null, null);
    }
    private void ReceiveEstimate(Session s, ReferenceNoiseFrame f, bool terminalTail = false)
    {
        lock (_sync)
        {
            if (_session != s || (terminalTail ? !s.Finishing : s.Stop.IsCancellationRequested) || s.EstimateProfile is not { } p) return;
            if (!terminalTail) CheckEstimateInput(s);
            if (s.EstimateReason is not null) return;
            double energy = p.TimeWeighting == "Fast" ? f.FastMeanSquare : f.SlowMeanSquare;
            double value = energy > 0 ? 10 * Math.Log10(energy) + p.OffsetDb : double.NaN;
            if (s.EstimateAt is { } previous && _time.GetElapsedTime(previous).TotalSeconds > .5) s.EstimateResumeAt = _time.GetTimestamp();
            bool settled = f.Settled && (s.EstimateResumeAt is not { } resumed || _time.GetElapsedTime(resumed).TotalSeconds >= ReferenceNoiseMeter.WarmupSeconds);
            bool valid = !f.Invalid && settled && double.IsFinite(f.Seconds) && f.Seconds is > 0 and <= 1 &&
                double.IsFinite(f.ClippedRatio) && f.ClippedRatio is >= 0 and <= .001 &&
                double.IsFinite(energy) && energy > 1e-16 && double.IsFinite(f.MeanSquare) && f.MeanSquare > 1e-16 &&
                double.IsFinite(f.RawPeak) && f.RawPeak > 0 && value is >= 30 and <= 130;
            s.EstimateAt = _time.GetTimestamp();
            s.EstimateCurrent = valid ? value : null;
            s.EstimateQuality = valid ? "Active" : !settled ? "WarmingUp" : "NoData";
            if (valid)
            {
                ReportEstimated(s, f, energy, value);
                s.EstimateSampled += f.Seconds; s.EstimateEnergy += energy * f.Seconds;
                s.EstimateMaximum = s.EstimateMaximum is { } max ? Math.Max(max, value) : value;
            }
            if (!terminalTail && s.ThresholdPolicy?.Estimated is not null)
                _studyThresholds.Receive(s.Id, new(f.Seconds, 0, 0, 0, false), s.EstimateCurrent, !valid);
            double elapsed = _time.GetElapsedTime(s.Started).TotalSeconds;
            if (elapsed - s.EstimateLastPoint >= .5)
            {
                s.EstimateTrend.Enqueue(new(elapsed, s.EstimateCurrent, valid ? "Good" : "Invalid")); s.EstimateLastPoint = elapsed;
                while (s.EstimateTrend.Count > 120) s.EstimateTrend.Dequeue();
            }
        }
    }
    private NoiseEstimate? EstimateSnapshot(Session? s, double elapsed)
    {
        if (s?.EstimateProfile is not { } p) return null;
        CheckEstimateInput(s);
        bool fresh = s.EstimateAt is { } at && _time.GetElapsedTime(at).TotalSeconds is >= 0 and < 1;
        // A fresh cached frame is no longer a live reading once stopping begins. Keep the
        // aggregate statistics, but match the raw channel and transport's active-session boundary.
        string state = s.EstimateReason is not null ? "Invalidated" : _state is "Stopped" or "Faulted" ? "Stopped" :
            _state != "Active" || s.Stop.IsCancellationRequested || !fresh ? "NoData" : s.EstimateQuality;
        return new(s.EstimateProfileId!, p.Algorithm, p.Method, p.TimeWeighting, p.OffsetDb,
            p.ConfirmedAt.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", System.Globalization.CultureInfo.InvariantCulture),
            state, s.EstimateReason, state == "Active" ? s.EstimateCurrent : null,
            new(s.EstimateSampled, elapsed > 0 ? Math.Clamp(s.EstimateSampled / elapsed, 0, 1) : 0,
                s.EstimateSampled > 0 ? 10 * Math.Log10(s.EstimateEnergy / s.EstimateSampled) + p.OffsetDb : null, s.EstimateMaximum));
    }
}

using NPEduTools.Contracts;

namespace NPEduTools.Host;

public sealed partial class NoiseService
{
    public NoiseCalibrationStatus CalibrationStatusSnapshot(string? scope, StudyHostState? study)
    {
        lock (_sync)
        {
            LoadCalibration(); LoadReference();
            NoiseCalibrationStatus Status(string state, string method, string? reason = null, DateTimeOffset? at = null) =>
                new(_instance, _revision, state, method, reason, at);
            // This only reads existing evidence. It never opens a microphone, meter or camera.
            if (_referenceError is not null) return Status("Unavailable", "Reference", _referenceError);
            if (_referenceProfile is { } reference)
            {
                string? reason = _referenceInvalidated ?? (reference.DeviceId != _selected ? "DEVICE_CHANGED" : null);
                if (reason is not null) return Status("Invalidated", reference.Method, reason, reference.ConfirmedAt);
                var session = _session;
                bool live = _state == "Active" && session is { EstimateProfile: not null } &&
                    !session.Stop.IsCancellationRequested && session.EstimateProfile == reference;
                if (live && session!.EstimateReason is { } unavailable)
                    return Status("Unavailable", reference.Method, unavailable, reference.ConfirmedAt);
                return Status(live ? "Ready" : "Saved", reference.Method, at: reference.ConfirmedAt);
            }
            if (_calError is not null) return Status("Unavailable", "Quick", _calError);
            if (_calProfile is not { } quick) return Status("Uncalibrated", "None");
            string? context = study?.PolicyState == "APPLIED" ? ContextKey(scope, study.Version) : null;
            string? invalid = quick.DeviceId != _selected ? "DEVICE_CHANGED" :
                context is not null && quick.Context != context ? "SCHOOL_CONFIGURATION_CHANGED" :
                _lastSignature is not null && _lastSignature != quick.Signature ? "INPUT_SETTINGS_CHANGED" : null;
            if (invalid is not null) return Status("Invalidated", "Quick", invalid, quick.ConfirmedAt);
            bool matched = context is not null && quick.Context == context && quick.Signature == _lastSignature &&
                _state == "Active" && _session is { } current && !current.Stop.IsCancellationRequested &&
                _time.GetElapsedTime(current.LastData).TotalSeconds is >= 0 and < 1;
            return Status(matched ? "Ready" : "Saved", "Quick",
                context is null ? "SCHOOL_CONFIGURATION_UNCONFIRMED" : null, quick.ConfirmedAt);
        }
    }
}

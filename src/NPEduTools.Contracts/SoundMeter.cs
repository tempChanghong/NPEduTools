namespace NPEduTools.Contracts;

// Explicit local diagnostics only; no reference profile, PCM or cloud payload.
public sealed record SoundMeterCommand(string Action, string Secret, string? DevicePath = null);
public sealed record SoundMeterDevice(string Path, string Name, string? MetadataError = null, int? NativeErrorCode = null);
public sealed record SoundMeterSample(double Decibels, string Weighting, string TimeWeighting,
    bool MaximumHold, bool LowBattery, int Range, double RoundTripMilliseconds, int InputReportLength, int OutputReportLength);
public sealed record SoundMeterDiagnostic(string? Stage, int? InputReportLength, int? OutputReportLength, int? NativeErrorCode);
public sealed record SoundMeterState(IReadOnlyList<SoundMeterDevice>? Devices = null, SoundMeterSample? Reading = null,
    SoundMeterDiagnostic? Diagnostic = null);
public static class SoundMeterContract
{
    public static bool Valid(SoundMeterCommand c) => c.Secret is { Length: > 0 and <= 128 } &&
        (c.Action == "list" ? c.DevicePath is null : c.Action == "read" &&
            c.DevicePath is { Length: > 0 and <= 4096 } && !string.IsNullOrWhiteSpace(c.DevicePath) && !c.DevicePath.Any(char.IsControl));
}

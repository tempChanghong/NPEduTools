using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using NPEduTools.Contracts;

namespace NPEduTools.Host;

public sealed partial class NoiseService
{
    // The runtime scope is the authenticated binding identity, including server,
    // deployment, device and binding revision. Configuration versions also include
    // homework, presentation and schedules; those must not invalidate acoustic work.
    // Public thresholds and their unit still fence a school administrator's change.
    private static string? CalibrationContextKey(string? scope, string? version, StudyNoise? policy) =>
        scope is null || version is null || policy is null ? null :
        HashCalibrationContext("noise-quick-context-v2\n" + scope + "\n" + JsonSerializer.Serialize(
            new { policy.RedLineDbfs, policy.MediumLineDbfs, policy.Estimated }, Protocol.Json));

    private static string HashCalibrationContext(string value) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    // A v1 store contains only the hash of binding + full configuration version.
    // Upgrade only when that complete old context can still be proven. Never infer
    // a different old binding from samples or silently adopt an unmatched profile.
    // Save both records atomically; failed writes retain the old file and fail closed.
    private void UpgradeCalibrationContext(string? scope, string? version, StudyNoise? policy)
    {
        if (_calError is not null || CalibrationContextKey(scope, version, policy) is not { } context) return;
        string legacy = HashCalibrationContext(scope + "\n" + version);
        var draft = _calDraft is { ContextVersion: 1 } d && d.Context == legacy
            ? d with { Context = context, ContextVersion = 2 } : _calDraft;
        var profile = _calProfile is { Version: 1 } p && p.Context == legacy
            ? p with { Context = context, Version = 2 } : _calProfile;
        if (draft != _calDraft || profile != _calProfile) SaveCalibration(draft, profile);
    }
}

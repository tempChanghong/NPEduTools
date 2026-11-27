using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using NPEduTools.Contracts;

namespace NPEduTools.Integrations.Npep;

internal sealed partial class ManagedUpdateSession
{
    private sealed record SavedReceipt(string ScopeHash, SoftwareMaintenanceReceipt Receipt);
    private SoftwareMaintenanceReceipt? _storedReceipt;
    private string? _receiptDeviceId;

    internal void HideBinding(string runningVersion, bool forget)
    {
        lock (_sync)
        {
            _scope = null;
            _online = false;
            if (forget) ClearReceipt();
            else _storedReceipt = null;
            _status = new("UNMANAGED", null, false, runningVersion, Reason: "STATE_UNKNOWN");
        }
    }

    internal void ForgetReceipt(string? expectedScope)
    {
        lock (_sync)
        {
            if (_scope != expectedScope) return;
            ClearReceipt();
            _status = _status with { MaintenanceReceipt = null, RollbackConfirmed = false };
            _online = false;
        }
    }

    private static string ScopeHash(string scope) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(scope)));
    private static string? Text(JsonNode? node) => node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;
    private static string? ReceiptTime(JsonNode? node) => ReceiptTime(Text(node));
    private static string? ReceiptTime(string? text) =>
        DateTimeOffset.TryParseExact(text, "yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal, out _) ? text : null;
    private static string? ReceiptLabel(string? value) => value is { Length: > 0 and <= 180 } &&
        !value.Any(char.IsControl) && !value.Contains("://", StringComparison.Ordinal) ? value.Trim() : null;
    private static string? ReceiptDeviceId(string? value) =>
        Guid.TryParseExact(value, "D", out var id) && id != Guid.Empty ? id.ToString("D") : null;
    private static bool KnownState(string state) =>
        state is "APPROVED" or "DEFERRED" or "INSTALLING" or "AWAITING_RESTART" or "SUCCEEDED" or "FAILED" or "CANCELLED";

    private SoftwareMaintenanceReceipt? MakeReceipt(JsonObject snapshot, ManagedUpdatePlan plan, string? reason, string? rollbackAt)
    {
        if (!KnownState(plan.State) || new Version(plan.PreviousRelease.Version) >= new Version(plan.TargetVersion)) return null;
        string? version = Text(snapshot["observedVersion"]);
        if (version is null || !Canonical(version)) version = Canonical(_status.RunningVersion) ? _status.RunningVersion : null;
        return new(plan.Id, ReceiptDeviceId(_receiptDeviceId), ReceiptLabel(_status.SchoolName), ReceiptLabel(_status.ClassName),
            ReceiptLabel(_status.DeviceName), plan.State, reason is null ? null : SafeReason(reason), plan.PreviousRelease.Version,
            plan.TargetVersion, version, ReceiptTime(snapshot["plan"]?["updatedAt"]), ReceiptTime(snapshot["lastSeenAt"]), rollbackAt);
    }

    private SoftwareMaintenanceReceipt? ReadReceipt(string scope)
    {
        try
        {
            if (!File.Exists(_receiptPath)) return null;
            using var file = File.OpenRead(_receiptPath);
            if (file.Length > 8192) return null;
            var saved = JsonSerializer.Deserialize<SavedReceipt>(file);
            if (saved is null || saved.ScopeHash != ScopeHash(scope) || saved.Receipt is not { } receipt ||
                receipt.PlanId == Guid.Empty || !KnownState(receipt.State) || receipt.PreviousVersion is null ||
                receipt.TargetVersion is null || !Canonical(receipt.PreviousVersion) || !Canonical(receipt.TargetVersion) ||
                new Version(receipt.PreviousVersion) >= new Version(receipt.TargetVersion)) return null;
            return receipt with { IsHistorical = false,
                DeviceId = ReceiptDeviceId(receipt.DeviceId), SchoolName = ReceiptLabel(receipt.SchoolName),
                ClassName = ReceiptLabel(receipt.ClassName), DeviceName = ReceiptLabel(receipt.DeviceName),
                Reason = receipt.Reason is null ? null : SafeReason(receipt.Reason),
                RunningVersion = receipt.RunningVersion is { } running && Canonical(running) ? running : null,
                UpdatedAt = ReceiptTime(receipt.UpdatedAt), ObservedAt = ReceiptTime(receipt.ObservedAt),
                RollbackConfirmedAt = receipt.State == "FAILED" && receipt.Reason is ("ROLLED_BACK" or "ROLLBACK_SUCCEEDED")
                    ? ReceiptTime(receipt.RollbackConfirmedAt) : null };
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) { return null; }
    }

    private void StoreReceipt(string scope, SoftwareMaintenanceReceipt receipt)
    {
        // Heartbeats that only move the observation clock do not rewrite the local file.
        if (_storedReceipt is { } previous && (previous with { ObservedAt = receipt.ObservedAt }) == receipt) return;
        try
        {
            Save(_receiptPath, new SavedReceipt(ScopeHash(scope), receipt));
            _storedReceipt = receipt;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    private void ClearReceipt()
    {
        _storedReceipt = null;
        try { File.Delete(_receiptPath); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }
}

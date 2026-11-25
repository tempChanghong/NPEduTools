using System.Text.Json;
using Microsoft.Win32;
using System.Runtime.Versioning;

namespace NPEduTools.Host;

internal sealed record EdgeWebsiteRule(int Slot, string Filter, bool Prepared);
internal sealed record EdgeWebsiteJournal(string Owner, string Scope, string PolicyId, string Source, string[] Desired, EdgeWebsiteRule[] Rules,
    EdgeWebsiteTiming? Timing = null);

internal interface IEdgeWebsitePolicyStore : IDisposable
{
    EdgeWebsiteJournal? ReadJournal();
    void SaveJournal(EdgeWebsiteJournal? journal);
    IReadOnlyDictionary<int, string?> ReadBlocks();
    bool HasSeparatePolicyAuthority();
    bool HasExternalMatch(string host, IReadOnlySet<int> owned);
    bool TryWrite(int slot, string filter);
    bool RemoveIfOwned(int slot, string filter);
}

[SupportedOSPlatform("windows")]
internal sealed class EdgeWebsitePolicyStore : IEdgeWebsitePolicyStore
{
    private const string Edge = @"SOFTWARE\Policies\Microsoft\Edge";
    private const string Blocks = Edge + @"\URLBlocklist";
    private const string Ledger = @"SOFTWARE\NPEduTools\ClassroomEdgeWebsitePolicy";
    private readonly RegistryKey _machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);

    public EdgeWebsiteJournal? ReadJournal()
    {
        using var key = _machine.OpenSubKey(Ledger);
        if (key is null || !Present(key, "Ownership")) return null;
        object? value = key.GetValue("Ownership");
        if (value is not string { Length: <= 49152 } json ||
            JsonSerializer.Deserialize<EdgeWebsiteJournal>(json) is not { } journal ||
            !EdgeWebsitePolicy.IsDigest(journal.Owner) || !EdgeWebsitePolicy.IsDigest(journal.PolicyId) ||
            journal.Source is not ("NONE" or "GRADE" or "CLASS") ||
            journal.Scope is not { Length: > 0 and <= 4096 } || journal.Rules is not { Length: <= 24 } ||
            journal.Desired is not { Length: <= 24 } || journal.Desired.Any(host => !EdgeWebsitePolicy.ValidHost(host)) ||
            journal.Timing is { } timing && (!timing.Valid || !journal.Desired.All(host => timing.Rules.Any(rule => rule.Host == host))) ||
            journal.Desired.Distinct(StringComparer.Ordinal).Count() != journal.Desired.Length ||
            journal.Rules.Any(rule => rule is null || rule.Slot is < 1 or > 1000 ||
                rule.Filter is not { Length: > 1 } filter || filter[0] != '.' || !journal.Desired.Contains(filter[1..], StringComparer.Ordinal)) ||
            journal.Rules.Select(rule => rule.Slot).Distinct().Count() != journal.Rules.Length)
            throw new InvalidDataException("Invalid Edge policy ownership journal.");
        return journal;
    }

    public void SaveJournal(EdgeWebsiteJournal? journal)
    {
        if (journal is null)
        {
            using var key = _machine.OpenSubKey(Ledger, writable: true);
            key?.DeleteValue("Ownership", throwOnMissingValue: false);
            key?.Flush();
            return;
        }
        using var ledger = _machine.CreateSubKey(Ledger);
        ledger.SetValue("Ownership", JsonSerializer.Serialize(journal), RegistryValueKind.String);
        ledger.Flush();
    }

    public IReadOnlyDictionary<int, string?> ReadBlocks()
    {
        using var key = _machine.OpenSubKey(Blocks);
        var slots = new Dictionary<int, string?>();
        foreach (string name in key?.GetValueNames() ?? [])
            if (int.TryParse(name, out int slot) && slot is >= 1 and <= 1000 && name == SlotName(slot))
                slots[slot] = ReadFilter(key!, name);
        return slots;
    }

    public bool HasExternalMatch(string host, IReadOnlySet<int> owned)
    {
        return HasMatch(_machine, "URLBlocklist", host, owned) || HasMatch(_machine, "URLAllowlist", host);
    }

    public bool HasSeparatePolicyAuthority()
    {
        using var machinePolicy = _machine.OpenSubKey(Edge);
        if (HasCloudManagement(machinePolicy)) return true;
        // A machine list can mask an entire user list, even for disjoint hosts.
        // Check all currently loaded Windows user profiles without loading any hive.
        using var users = RegistryKey.OpenBaseKey(RegistryHive.Users, RegistryView.Registry64);
        foreach (string sid in users.GetSubKeyNames().Where(name => name.StartsWith("S-1-", StringComparison.Ordinal) && !name.EndsWith("_Classes", StringComparison.OrdinalIgnoreCase)))
        {
            using var policy = users.OpenSubKey(sid + "\\" + Edge);
            if (HasCloudManagement(policy)) return true;
            using var blocks = policy?.OpenSubKey("URLBlocklist");
            using var allows = policy?.OpenSubKey("URLAllowlist");
            if (blocks?.ValueCount > 0 || allows?.ValueCount > 0) return true;
        }
        return false;
    }

    private static bool HasCloudManagement(RegistryKey? key) => key is not null && key.GetValueNames().Any(name =>
        name.Equals("EdgeManagementEnrollmentToken", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("CloudManagementEnrollmentToken", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("EdgeManagementPolicyOverridesPlatformPolicy", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("EdgeManagementUserPolicyOverridesCloudMachinePolicy", StringComparison.OrdinalIgnoreCase));

    private static bool HasMatch(RegistryKey hive, string list, string host, IReadOnlySet<int>? skip = null)
    {
        using var key = hive.OpenSubKey(Edge + "\\" + list);
        foreach (string name in key?.GetValueNames() ?? [])
        {
            if (int.TryParse(name, out int slot) && name == SlotName(slot) && skip?.Contains(slot) == true) continue;
            if (ReadFilter(key!, name) is { } filter && CouldMatch(filter, host)) return true;
        }
        return false;
    }

    // A path, port or scheme restriction may overlap some visits to this host.
    // Keep such policy and report the overlap instead of crediting its effect to us.
    internal static bool CouldMatch(string filter, string host)
    {
        string pattern = filter.Trim();
        int scheme = pattern.IndexOf("://", StringComparison.Ordinal);
        if (scheme >= 0)
        {
            if (pattern[..scheme].ToLowerInvariant() is not ("http" or "https" or "*")) return false;
            pattern = pattern[(scheme + 3)..];
        }
        string authority = pattern.Split(['/', ':', '@', '?', '#'], 2)[0];
        if (authority.Contains('*') || authority.Length == 0) return true;
        bool exact = authority.StartsWith('.');
        authority = authority.Trim('.');
        try { authority = new System.Globalization.IdnMapping().GetAscii(authority); }
        catch (ArgumentException) { return true; }
        return host.Equals(authority, StringComparison.OrdinalIgnoreCase) ||
            !exact && host.EndsWith("." + authority, StringComparison.OrdinalIgnoreCase);
    }

    public bool TryWrite(int slot, string filter)
    {
        using var key = _machine.CreateSubKey(Blocks);
        string name = SlotName(slot);
        if (Present(key, name)) return false;
        key.SetValue(name, filter, RegistryValueKind.String);
        key.Flush();
        return ReadFilter(key, name) == filter;
    }

    public bool RemoveIfOwned(int slot, string filter)
    {
        using var key = _machine.OpenSubKey(Blocks, writable: true);
        string name = SlotName(slot);
        if (key is null || !Present(key, name)) return true;
        if (ReadFilter(key, name) != filter) return false;
        key.DeleteValue(name, throwOnMissingValue: false);
        key.Flush();
        return true;
    }

    private static string SlotName(int slot) => slot.ToString(System.Globalization.CultureInfo.InvariantCulture);
    private static bool Present(RegistryKey key, string name) => key.GetValueNames().Contains(name, StringComparer.OrdinalIgnoreCase);
    private static string? ReadFilter(RegistryKey key, string name) =>
        key.GetValue(name) is string text && key.GetValueKind(name) == RegistryValueKind.String ? text : null;

    public void Dispose() => _machine.Dispose();
}

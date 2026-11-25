using System.Security;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using NPEduTools.Contracts;

namespace NPEduTools.Host;

internal sealed class EdgeWebsitePolicy(string dataDirectory, Func<IEdgeWebsitePolicyStore> open)
{
    private const string Pending = "WRITTEN_PENDING";
    private const string Cleared = "CLEARED_PENDING";
    private const string Conflict = "CONFLICT";
    private const string Failed = "FAILED";
    private static readonly Regex Digest = new(@"\A[0-9a-f]{64}\z", RegexOptions.NonBacktracking);
    private static readonly Regex Label = new(@"\A[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?\z", RegexOptions.NonBacktracking);
    private readonly string _owner = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(
        Path.GetFullPath(dataDirectory).TrimEnd(Path.DirectorySeparatorChar).ToUpperInvariant())));
    private readonly Dictionary<string, long> _proofs = new(StringComparer.Ordinal);
    private string? _scope;
    private bool _knownScope;
    private WebsitePolicyDesktopState? _desktop;
    private string? _policyId;
    private string[] _desired = [];
    private string _status = Failed;
    private long _lastCheck;

    internal static bool IsDigest(string? value) => value is { Length: 64 } && Digest.IsMatch(value);
    internal static bool ValidHost(string? host)
    {
        if (host is not { Length: > 0 and <= 253 }) return false;
        var labels = host.Split('.');
        return labels.Length >= 2 && labels.All(Label.IsMatch) && labels[^1].Any(character => character is >= 'a' and <= 'z');
    }

    internal WebsitePolicyDesktopState? ReadDesktop()
    {
        if (!_knownScope) return null;
        long now = Environment.TickCount64;
        if (now - _lastCheck >= 3000)
        {
            _lastCheck = now;
            if (_scope is null || _policyId is null) Bind(_scope);
            else
            {
                string status = Failed;
                string[] installed = [];
                string source = "NONE";
                WithStore((store, journal) =>
                {
                    if (journal?.Owner == _owner && journal.Scope == _scope && journal.PolicyId == _policyId)
                    {
                        source = journal.Source;
                        if (journal.Rules.Length > 0 && store.HasSeparatePolicyAuthority())
                        {
                            // The competing authority may appear while this device is offline.
                            ReleaseRules(store, journal);
                            journal = journal with { Rules = [] };
                            store.SaveJournal(journal);
                        }
                        (status, installed) = Verify(store, journal, _desired);
                    }
                    else if (_status is Conflict or Cleared) status = _status;
                });
                Publish(_policyId, source, installed, status);
            }
        }
        return _desktop;
    }
    internal string Status => _status == Pending && _desktop is { BlockedHosts.Length: > 0 } frame &&
        frame.BlockedHosts.All(host => _proofs.TryGetValue(host, out long at) && Environment.TickCount64 - at < 300_000)
        ? "EDGE_CONFIRMED" : _status;

    internal bool Observe(WebsitePolicyBrowserEvidence evidence)
    {
        if (ReadDesktop() is not { } frame || frame.ObservationId != evidence.ObservationId ||
            !frame.BlockedHosts.Contains(evidence.Host, StringComparer.Ordinal)) return false;
        _proofs[evidence.Host] = Environment.TickCount64;
        return true;
    }

    internal void Bind(string? scope)
    {
        _knownScope = true;
        if (_scope != scope)
        {
            _scope = scope;
            _policyId = null;
            _desired = [];
            _desktop = null;
            _proofs.Clear();
        }
        WithStore((store, journal) =>
        {
            if (journal is null || journal.Owner != _owner) return;
            if (journal.Scope != scope)
            {
                Release(store, journal);
                return;
            }
            // Startup while offline can resume the last installed list for this
            // exact durable pairing. An unconfirmed journal is never a warning source.
            if (_policyId is null)
            {
                _desired = journal.Desired;
                var (status, installed) = Verify(store, journal, _desired);
                Publish(journal.PolicyId, journal.Source, installed, status);
            }
        });
    }

    internal void Apply(string scope, string policyId, string source, string[] hosts)
    {
        if (_scope != scope) return;
        _desired = hosts;
        string status = Failed;
        string[] installed = [];
        WithStore((store, journal) =>
        {
            if (journal is not null && journal.Owner != _owner)
            {
                status = Conflict;
                return;
            }
            if (journal is not null && journal.Scope != scope)
            {
                bool removed = Release(store, journal);
                journal = null;
                if (!removed) { status = Conflict; return; }
            }

            if (journal is null && hosts.Length == 0) { status = Cleared; return; }

            var rules = journal?.Rules.ToList() ?? [];
            bool separateAuthority = hosts.Length != 0 && store.HasSeparatePolicyAuthority();
            var filters = hosts.Select(host => "." + host).ToHashSet(StringComparer.Ordinal);
            bool changed = false;
            // Retain shared names even if additions cannot be installed. Only
            // remove today's obsolete names (or yield to a separate authority).
            foreach (var rule in rules.Where(rule => separateAuthority || !filters.Contains(rule.Filter)).ToArray())
            {
                store.RemoveIfOwned(rule.Slot, rule.Filter);
                rules.Remove(rule);
                changed = true;
            }

            var missing = hosts.Where(host => !rules.Any(rule => rule.Filter == "." + host)).ToArray();
            var occupied = store.ReadBlocks();
            var owned = rules.Select(rule => rule.Slot).ToHashSet();
            var free = Enumerable.Range(1, 1000).Where(slot => !occupied.ContainsKey(slot) && !owned.Contains(slot)).Take(missing.Length).ToArray();
            if (!separateAuthority && free.Length == missing.Length && missing.All(host => !store.HasExternalMatch(host, owned)))
            {
                rules.AddRange(missing.Select((host, index) => new EdgeWebsiteRule(free[index], "." + host, true)));
                changed |= missing.Length > 0;
            }

            if (journal is null || journal.PolicyId != policyId || changed)
            {
                journal = new(_owner, scope, policyId, source, hosts, rules.ToArray());
                store.SaveJournal(journal);
            }
            if (!separateAuthority)
            {
                for (int index = 0; index < journal.Rules.Length; index++)
                {
                    var rule = journal.Rules[index];
                    if (!rule.Prepared || store.HasExternalMatch(rule.Filter[1..], owned)) continue;
                    if (occupied.TryGetValue(rule.Slot, out string? existing) ? existing != rule.Filter : !store.TryWrite(rule.Slot, rule.Filter)) continue;
                    journal.Rules[index] = rule with { Prepared = false };
                    store.SaveJournal(journal);
                }
            }
            (status, installed) = Verify(store, journal, hosts);
        });
        Publish(policyId, source, installed, status);
    }

    private static (string Status, string[] Installed) Verify(IEdgeWebsitePolicyStore store, EdgeWebsiteJournal journal, string[] desired)
    {
        if (desired.Length == 0) return (Cleared, []);
        if (store.HasSeparatePolicyAuthority()) return (Conflict, []);
        var entries = store.ReadBlocks();
        var intact = journal.Rules.Where(rule => !rule.Prepared && entries.GetValueOrDefault(rule.Slot) == rule.Filter).ToArray();
        var owned = intact.Select(rule => rule.Slot).ToHashSet();
        var installed = intact.Select(rule => rule.Filter[1..]).Where(host => !store.HasExternalMatch(host, owned)).ToArray();
        return (installed.Length == desired.Length ? Pending : Conflict, installed);
    }

    private static bool Release(IEdgeWebsitePolicyStore store, EdgeWebsiteJournal journal)
    {
        bool all = ReleaseRules(store, journal);
        store.SaveJournal(null);
        return all;
    }

    private static bool ReleaseRules(IEdgeWebsitePolicyStore store, EdgeWebsiteJournal journal)
    {
        bool all = true;
        foreach (var rule in journal.Rules) all &= store.RemoveIfOwned(rule.Slot, rule.Filter);
        return all;
    }

    private void Publish(string policyId, string source, string[] hosts, string status)
    {
        if (_policyId != policyId || _status != status || !(_desktop?.BlockedHosts ?? []).SequenceEqual(hosts))
        {
            _desktop = hosts.Length > 0 ? new(Guid.NewGuid(), source, hosts) : null;
            _proofs.Clear();
        }
        _policyId = policyId;
        _status = status;
    }

    private static void WithStore(Action<IEdgeWebsitePolicyStore, EdgeWebsiteJournal?> run, Func<IEdgeWebsitePolicyStore> open)
    {
        try
        {
            using var gate = new Mutex(false, @"Global\NPEduTools.ClassroomEdgeWebsitePolicy");
            bool acquired;
            try { acquired = gate.WaitOne(TimeSpan.FromSeconds(2)); }
            catch (AbandonedMutexException) { acquired = true; }
            if (!acquired) return;
            try { using var store = open(); run(store, store.ReadJournal()); }
            finally { gate.ReleaseMutex(); }
        }
        catch (Exception e) when (e is UnauthorizedAccessException or IOException or SecurityException or JsonException or InvalidOperationException) { }
    }

    private void WithStore(Action<IEdgeWebsitePolicyStore, EdgeWebsiteJournal?> run) => WithStore(run, open);
}

using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using NPEduTools.Contracts;

namespace NPEduTools.Host;

public sealed partial class SchoolManagementService
{
    private static readonly Regex WebsiteId = new(@"\A[-A-Za-z0-9_.:]{1,191}\z", RegexOptions.NonBacktracking);
    private readonly EdgeWebsitePolicy? _edgeWebsitePolicy;
    private JsonObject? _websitePolicyReceipt;
    private bool _websitePolicyApplied;

    public bool SupportsWebsitePolicyAutomation => _edgeWebsitePolicy is not null;
    public bool SupportsWebsitePolicyTiming => SupportsWebsitePolicyAutomation;

    public void ObserveWebsiteBinding(string? scope)
    {
        lock (_sync) _edgeWebsitePolicy?.Bind(scope);
    }

    private void ReceiveWebsitePolicy(string scope, JsonNode? payload)
    {
        if (payload is not JsonObject policy || policy["schemaVersion"] is not JsonValue version ||
            !version.TryGetValue<int>(out var schema) || schema is not (1 or 2 or 3) || policy.Count != (schema == 3 ? 10 : 7) ||
            !WebsiteId.IsMatch(WebsiteText(policy, "schoolId")) || !WebsiteId.IsMatch(WebsiteText(policy, "termId")) ||
            !WebsiteId.IsMatch(WebsiteText(policy, "classId")) ||
            WebsiteText(policy, "source") is not ("NONE" or "GRADE" or "CLASS") ||
            policy["blockedHosts"] is not JsonArray { Count: <= 24 } hosts ||
            WebsiteText(policy, "source") == "NONE" && hosts.Count != 0)
        { _websitePolicyReceipt = null; return; }
        string policyId = WebsiteText(policy, "policyId");
        if (!EdgeWebsitePolicy.IsDigest(policyId)) { _websitePolicyReceipt = null; return; }
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var node in hosts)
        {
            if (node is not JsonValue text || !text.TryGetValue<string>(out var host) ||
                !EdgeWebsitePolicy.ValidHost(host) || !names.Add(host!))
            { _websitePolicyReceipt = null; return; }
        }

        string source = WebsiteText(policy, "source");
        if (schema == 3)
        {
            if (policy["revision"] is not JsonValue stamp || !stamp.TryGetValue<long>(out long revision) ||
                policy["rules"] is not JsonArray rules || rules.Count != names.Count)
            { _websitePolicyReceipt = null; return; }
            var timed = new List<EdgeWebsiteScheduleRule>(rules.Count);
            foreach (var row in rules)
            {
                if (row is not JsonObject {Count: 2} rule)
                { _websitePolicyReceipt = null; return; }
                timed.Add(new(WebsiteText(rule, "host"), WebsiteText(rule, "period")));
            }
            var timing = new EdgeWebsiteTiming(revision, timed.ToArray(), WebsiteText(policy, "unavailableBehavior"));
            if (!timing.Valid || !names.SetEquals(timing.Rules.Select(rule => rule.Host)))
            { _websitePolicyReceipt = null; return; }
            if (_edgeWebsitePolicy is null || !_edgeWebsitePolicy.ApplyTiming(scope, policyId, source, timing)) return;
            _websitePolicyApplied = true;
        }
        else _websitePolicyApplied = schema == 2 && _edgeWebsitePolicy?.Apply(scope, policyId, source, names.ToArray()) == true;
        _websitePolicyReceipt = new JsonObject
        {
            ["schemaVersion"] = schema,
            ["policyId"] = policyId,
            ["status"] = "RECEIVED_NOT_APPLIED"
        };
    }

    internal async Task RunWebsitePolicyClockAsync(CancellationToken token)
    {
        if (_edgeWebsitePolicy is null) return;
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(600));
        try
        {
            while (await timer.WaitForNextTickAsync(token))
                lock (_sync) _edgeWebsitePolicy.RefreshTiming();
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
    }

    internal WebsitePolicyDesktopState? ReadWebsitePolicyDesktop()
    {
        lock (_sync) return _edgeWebsitePolicy?.ReadDesktop();
    }

    internal bool ObserveWebsitePolicyDesktop(WebsitePolicyBrowserEvidence evidence)
    {
        lock (_sync) return _edgeWebsitePolicy?.Observe(evidence) == true;
    }

    private static string WebsiteText(JsonObject policy, string key) =>
        policy[key] is JsonValue value && value.TryGetValue<string>(out var text) ? text ?? "" : "";
}

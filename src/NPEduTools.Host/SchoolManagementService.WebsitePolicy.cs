using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using NPEduTools.Contracts;

namespace NPEduTools.Host;

public sealed partial class SchoolManagementService
{
    private static readonly Regex WebsiteId = new(@"\A[-A-Za-z0-9_.:]{1,191}\z", RegexOptions.NonBacktracking);
    private readonly EdgeWebsitePolicy? _edgeWebsitePolicy;
    private JsonObject? _websitePolicyReceipt;

    public bool SupportsWebsitePolicyAutomation => _edgeWebsitePolicy is not null;

    public void ObserveWebsiteBinding(string? scope)
    {
        lock (_sync) _edgeWebsitePolicy?.Bind(scope);
    }

    private void ReceiveWebsitePolicy(string scope, JsonNode? payload)
    {
        _websitePolicyReceipt = null;
        if (payload is not JsonObject { Count: 7 } policy || policy["schemaVersion"] is not JsonValue version ||
            !version.TryGetValue<int>(out var schema) || schema is not (1 or 2) ||
            !WebsiteId.IsMatch(WebsiteText(policy, "schoolId")) || !WebsiteId.IsMatch(WebsiteText(policy, "termId")) ||
            !WebsiteId.IsMatch(WebsiteText(policy, "classId")) ||
            WebsiteText(policy, "source") is not ("NONE" or "GRADE" or "CLASS") ||
            policy["blockedHosts"] is not JsonArray { Count: <= 24 } hosts ||
            WebsiteText(policy, "source") == "NONE" && hosts.Count != 0)
            return;
        string policyId = WebsiteText(policy, "policyId");
        if (!EdgeWebsitePolicy.IsDigest(policyId)) return;
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var node in hosts)
        {
            if (node is not JsonValue text || !text.TryGetValue<string>(out var host) ||
                !EdgeWebsitePolicy.ValidHost(host) || !names.Add(host!))
                return;
        }

        if (schema == 2) _edgeWebsitePolicy?.Apply(scope, policyId, WebsiteText(policy, "source"), names.ToArray());
        _websitePolicyReceipt = new JsonObject
        {
            ["schemaVersion"] = schema,
            ["policyId"] = policyId,
            ["status"] = "RECEIVED_NOT_APPLIED"
        };
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

using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace NPEduTools.Host;

public sealed partial class SchoolManagementService
{
    private static readonly Regex WebsiteId = new(@"\A[-A-Za-z0-9_.:]{1,191}\z", RegexOptions.NonBacktracking);
    private static readonly Regex WebsiteDigest = new(@"\A[0-9a-f]{64}\z", RegexOptions.NonBacktracking);
    private static readonly Regex WebsiteLabel = new(@"\A[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?\z", RegexOptions.NonBacktracking);
    private JsonObject? _websitePolicyReceipt;

    private void ReceiveWebsitePolicy(JsonNode? payload)
    {
        _websitePolicyReceipt = null;
        if (payload is not JsonObject { Count: 7 } policy || policy["schemaVersion"] is not JsonValue version ||
            !version.TryGetValue<int>(out var schema) || schema != 1 ||
            !WebsiteId.IsMatch(WebsiteText(policy, "schoolId")) || !WebsiteId.IsMatch(WebsiteText(policy, "termId")) ||
            !WebsiteId.IsMatch(WebsiteText(policy, "classId")) ||
            WebsiteText(policy, "source") is not ("NONE" or "GRADE" or "CLASS") ||
            policy["blockedHosts"] is not JsonArray { Count: <= 24 } hosts ||
            WebsiteText(policy, "source") == "NONE" && hosts.Count != 0)
            return;
        string policyId = WebsiteText(policy, "policyId");
        if (!WebsiteDigest.IsMatch(policyId)) return;
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var node in hosts)
        {
            if (node is not JsonValue text || !text.TryGetValue<string>(out var host) || host is not { Length: <= 253 })
                return;
            var labels = host.Split('.');
            if (labels.Length < 2 || !labels.All(WebsiteLabel.IsMatch) ||
                !labels[^1].Any(character => character is >= 'a' and <= 'z') || !names.Add(host)) return;
        }

        // This acknowledges the received policy for the authenticated binding only.
        // No Windows or Edge settings are written in this phase.
        _websitePolicyReceipt = new JsonObject
        {
            ["schemaVersion"] = 1,
            ["policyId"] = policyId,
            ["status"] = "RECEIVED_NOT_APPLIED"
        };
    }

    private static string WebsiteText(JsonObject policy, string key) =>
        policy[key] is JsonValue value && value.TryGetValue<string>(out var text) ? text ?? "" : "";
}

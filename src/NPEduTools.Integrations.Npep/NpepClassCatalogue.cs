using System.Text.Json;
using System.Text.Json.Nodes;
using NPEduTools.Contracts;

namespace NPEduTools.Integrations.Npep;

public sealed partial class NpepDevice
{
    public async Task<JsonObject> ClassCatalogueAsync(string origin, CancellationToken token = default)
    {
        using var api = Api(origin);
        var data = await api.SchoolManagementAsync(null, "admission/catalog", null, token);
        return ReadCatalogue(data, null);
    }

    public async Task<JsonObject> FindClassScreensAsync(string origin, JsonObject server, string schoolQuery,
        string classQuery, string? after = null, CancellationToken token = default)
    {
        var query = new NpepCommand("find-class", 0, Origin: origin, ServerInstanceId: server.Text("serverInstanceId"),
            DeploymentEpoch: server.Text("deploymentEpoch"), SchoolQuery: schoolQuery, ClassQuery: classQuery, CatalogueAfter: after);
        if (!NpepContract.Valid(query)) throw new NpepException("INVALID_REQUEST");
        query = query with { SchoolQuery = schoolQuery.Trim(), ClassQuery = classQuery.Trim() };
        var body = NpepProtocol.Body();
        body["serverInstanceId"] = query.ServerInstanceId;
        body["deploymentEpoch"] = query.DeploymentEpoch;
        body["schoolQuery"] = query.SchoolQuery;
        body["classQuery"] = query.ClassQuery;
        if (after is not null) body["after"] = after;
        using var api = Api(origin);
        var data = await api.SchoolManagementAsync(null, "admission/catalog/lookup", body, token);
        RequireCatalogueIdentity(data, server);
        return ReadCatalogue(data, query);
    }

    internal static void RequireCatalogueIdentity(JsonObject data, JsonObject server)
    {
        if (data.Text("serverInstanceId") != server.Text("serverInstanceId") ||
            data.Text("deploymentEpoch") != server.Text("deploymentEpoch")) throw new NpepException("INSTANCE_MISMATCH");
    }

    private static JsonObject ReadCatalogue(JsonObject data, NpepCommand? query)
    {
        static bool Flag(JsonObject source, string field) =>
            source[field] is JsonValue flag && flag.TryGetValue<bool>(out var value) ? value : throw new NpepException("INVALID_RESPONSE");
        static bool Matches(JsonObject row, string prefix, string term) =>
            row.Text(prefix + "Name") == term || string.Equals(row.Text(prefix + "Code"), term, StringComparison.OrdinalIgnoreCase);

        var items = new JsonArray();
        var result = new JsonObject { ["serverInstanceId"] = data.Text("serverInstanceId"), ["deploymentEpoch"] = data.Text("deploymentEpoch"),
            ["items"] = items, ["truncated"] = Flag(data, "truncated") };
        if (data.ContainsKey("supportsLookup")) result["supportsLookup"] = Flag(data, "supportsLookup");
        if (data["items"] is not JsonArray rows || rows.Count > (query is null ? 200 : 24)) throw new NpepException("INVALID_RESPONSE");
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var size = 0;
        foreach (var node in rows)
        {
            if (node is not JsonObject raw) throw new NpepException("INVALID_RESPONSE");
            var row = new JsonObject();
            foreach (var field in new[] { "screenBindingId", "schoolId", "classId", "schoolName", "className", "gradeName", "screenName" })
            {
                var value = raw.Text(field);
                if (value.Length is 0 or > 382) throw new NpepException("INVALID_RESPONSE");
                row[field] = value;
            }
            if (!NpepContract.CatalogueId(row.Text("screenBindingId")) || !seen.Add(row.Text("screenBindingId"))) throw new NpepException("INVALID_RESPONSE");
            row["preauthorized"] = Flag(raw, "preauthorized");
            if (query is not null)
            {
                row["schoolCode"] = raw.Text("schoolCode");
                row["classCode"] = raw.Text("classCode");
                if (!Matches(row, "school", query.SchoolQuery!) || !Matches(row, "class", query.ClassQuery!) ||
                    row.Text("screenBindingId") == query.CatalogueAfter) throw new NpepException("INVALID_RESPONSE");
            }
            size += JsonSerializer.SerializeToUtf8Bytes(row, Protocol.Json).Length + 1;
            if (size > 44 * 1024)
            {
                if (query is not null) throw new NpepException("INVALID_RESPONSE");
                result["truncated"] = true;
                break;
            }
            items.Add(row);
        }
        if (query is not null)
        {
            if (result["supportsLookup"]?.GetValue<bool>() != true || data.Text("schoolQuery") != query.SchoolQuery ||
                data.Text("classQuery") != query.ClassQuery || !data.ContainsKey("after") ||
                (data["after"] is null ? null : data.Text("after")) != query.CatalogueAfter || !data.ContainsKey("nextCursor"))
                throw new NpepException("INVALID_RESPONSE");
            var next = data["nextCursor"] is null ? null : data.Text("nextCursor");
            if (Flag(result, "truncated") ? next is null || items.Count == 0 || next != items[^1]!.AsObject().Text("screenBindingId") : next is not null)
                throw new NpepException("INVALID_RESPONSE");
            result["schoolQuery"] = query.SchoolQuery;
            result["classQuery"] = query.ClassQuery;
            result["after"] = query.CatalogueAfter;
            result["nextCursor"] = next;
        }
        return result;
    }
}

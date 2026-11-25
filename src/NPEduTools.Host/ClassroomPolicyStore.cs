using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using NPEduTools.Integrations.Npep;

namespace NPEduTools.Host;

internal sealed record ClassroomUsbKey(string KeyId, string MediaHash, string SecretHash);

// The device receipt identifies the effective policy that this Host actually installed.
// The school-wide sequence can change even when this particular classroom's rules do not.
internal sealed record ClassroomPolicy(long Revision, string PolicyId, string Source, string Stop, string Exit,
    IReadOnlyList<ClassroomUsbKey> Keys, int SchemaVersion, string? SwitchMode, string? StartManualStudy = null)
{
    internal static bool IsDigest(string? text) =>
        text is { Length: 64 } && text.All(ch => ch is >= '0' and <= '9' or >= 'a' and <= 'f');

    internal static ClassroomPolicy Parse(JsonNode? node)
    {
        if (node is not JsonObject p || p.Count != 6 || p["schemaVersion"]?.GetValue<int>() is not (1 or 2 or 3 or 4) ||
            p["revision"]?.GetValue<long>() is not { } rev || rev < 0 || rev > NpepProtocol.MaxInteger ||
            p["policyId"]?.GetValue<string>() is not { } id || !IsDigest(id) || !p.ContainsKey("actions") ||
            p["source"]?.GetValue<string>() is not ("LEGACY" or "GRADE" or "CLASS") ||
            p["usbKeys"] is not JsonArray keys || keys.Count > 64)
            throw new InvalidDataException("Invalid classroom policy.");
        string source = p["source"]!.GetValue<string>();
        int schema = p["schemaVersion"]!.GetValue<int>();
        if (source == "LEGACY")
        {
            bool defaults = schema < 3 ? p["actions"] is null :
                p["actions"] is JsonObject a && a.Count == schema && (schema < 4 || a.ContainsKey("startManualStudy")) &&
                a.ContainsKey("stopScheduledMonitoring") && a.ContainsKey("exitBackground") &&
                a.ContainsKey("switchClassroomMode") && a.All(pair => pair.Value is null);
            if (!defaults || keys.Count != 0) throw new InvalidDataException("Invalid default policy.");
            return new(rev, id, source, "default", "default", [], schema, null);
        }
        if (p["actions"] is not JsonObject actions || !actions.ContainsKey("stopScheduledMonitoring") ||
            !actions.ContainsKey("exitBackground") || schema >= 3 && !actions.ContainsKey("switchClassroomMode") ||
            schema == 4 && !actions.ContainsKey("startManualStudy") ||
            actions.Count != (schema >= 3 ? schema : schema == 2 && actions.ContainsKey("switchClassroomMode") ? 3 : 2))
            throw new InvalidDataException("Invalid classroom actions.");
        var parsed = new List<ClassroomUsbKey>(keys.Count);
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var nodeKey in keys)
        {
            if (nodeKey is not JsonObject key || key.Count != 3 ||
                key["keyId"]?.GetValue<string>() is not { } keyId || !Guid.TryParseExact(keyId, "D", out var guid) ||
                guid == Guid.Empty || guid.ToString("D") != keyId || !ids.Add(keyId) ||
                key["mediaHash"]?.GetValue<string>() is not { } media || !IsDigest(media) ||
                key["secretHash"]?.GetValue<string>() is not { } secret || !IsDigest(secret))
                throw new InvalidDataException("Invalid classroom key.");
            parsed.Add(new(keyId, media, secret));
        }
        string EffectiveRule(string action) => schema >= 3 && actions[action] is null ? "default" : ReadRule(actions[action]);
        return new(rev, id, source, EffectiveRule("stopScheduledMonitoring"), EffectiveRule("exitBackground"),
            parsed.OrderBy(k => k.KeyId, StringComparer.Ordinal).ToArray(), schema,
            actions["switchClassroomMode"] is { } switchMode ? ReadRule(switchMode) : null,
            actions["startManualStudy"] is { } start ? ReadRule(start) : null);
    }

    private static string ReadRule(JsonNode? node)
    {
        if (node is not JsonObject rule || rule.Count != 2 || !rule.ContainsKey("factor"))
            throw new InvalidDataException("Invalid action rule.");
        return (rule["mode"]?.GetValue<string>(), rule["factor"]?.GetValue<string>()) switch
        {
            ("ALLOW", null) => "allow", ("DENY", null) => "deny",
            ("VERIFY", "CLASS_PASSWORD") => "password", ("VERIFY", "USB") => "usb",
            ("VERIFY", "ANY") => "either", ("VERIFY", "ALL") => "both",
            _ => throw new InvalidDataException("Invalid action rule.")
        };
    }

    private bool SameBaseRules(ClassroomPolicy other) =>
        Source == other.Source && Stop == other.Stop && Exit == other.Exit && Keys.SequenceEqual(other.Keys);

    internal static bool UsesUsb(string? rule) => rule is "usb" or "both" or "either";

    internal bool CanFollow(ClassroomPolicy prior)
    {
        if (Revision < prior.Revision || SchemaVersion == 1 && prior.SwitchMode is not null) return false;
        if (PolicyId == prior.PolicyId) return SameBaseRules(prior) && SwitchMode == prior.SwitchMode && StartManualStudy == prior.StartManualStudy;
        if (Revision > prior.Revision) return true;
        if (SchemaVersion <= prior.SchemaVersion) return false;
        // A class override solely for manual study was invisible to older Hosts.
        if (Source != prior.Source && !(SchemaVersion == 4 && StartManualStudy is not null && Source == "CLASS" &&
            prior.Source is "GRADE" or "LEGACY")) return false;
        bool keysOnlyForManualStudy = UsesUsb(StartManualStudy) && !UsesUsb(Stop) && !UsesUsb(Exit) && !UsesUsb(SwitchMode);
        if (!Keys.SequenceEqual(prior.Keys) && !(keysOnlyForManualStudy && prior.Keys.Count == 0)) return false;
        // Match exactly the old wire projection of this revision before replacing it.
        string Project(string rule) => rule == "default" && prior.SchemaVersion < 3 && prior.Source != "LEGACY" ? "deny" : rule;
        return Project(Stop) == prior.Stop && Project(Exit) == prior.Exit &&
            (prior.SchemaVersion == 1 && prior.Source != "LEGACY" || SwitchMode == prior.SwitchMode) &&
            (Stop != prior.Stop || Exit != prior.Exit || SwitchMode != prior.SwitchMode || StartManualStudy != prior.StartManualStudy);
    }
}

internal sealed class ClassroomPolicyStore
{
    private sealed record Saved(int SchemaVersion, string? Scope, JsonObject? Policy, bool Hold);
    private readonly string _path;
    private readonly NoiseService _noise;
    private readonly ClassroomUsbPresence _usb = new();
    private string? _scope;
    private ClassroomPolicy? _current;
    private ClassroomPolicy? _awaitingSave;
    private bool _hold, _advertised, _dirty;
    private string? _error;

    internal ClassroomPolicyStore(string directory, NoiseService noise)
    {
        _noise = noise;
        _path = Path.Combine(directory, "classroom-action-policy.json");
        try
        {
            using var input = File.OpenRead(_path);
            if (input.Length > 131072) throw new InvalidDataException();
            var state = JsonSerializer.Deserialize<Saved>(input);
            if (state is not { SchemaVersion: 1 } || state.Scope is { Length: > 4096 or 0 } ||
                (state.Policy is null ? !state.Hold : state.Hold || state.Scope is null))
                throw new InvalidDataException();
            _scope = state.Scope;
            _current = state.Policy is null ? null : ClassroomPolicy.Parse(state.Policy);
            _hold = state.Hold;
        }
        catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException) { }
        catch (Exception e) when (StorageOrDataError(e))
        {
            _error = "CLASSROOM_POLICY_STORE_UNAVAILABLE";
            _hold = true;
        }
        Install();
    }

    internal void Bind(string? scope)
    {
        if (_scope == scope && !_dirty) return;
        if (_scope != scope)
        {
            _hold |= _current is { Source: not "LEGACY" };
            _current = null;
            _awaitingSave = null;
            _scope = scope;
            _advertised = false;
            _error = null;
            Install();
            _dirty = _hold;
        }
        if (_dirty)
        {
            try { Write(new(1, scope, null, true)); _dirty = false; }
            catch (Exception e) when (StorageOrDataError(e)) { _error = "CLASSROOM_POLICY_STORE_UNAVAILABLE"; }
        }
    }

    internal void Apply(string scope, JsonObject envelope)
    {
        if (_scope != scope) return;
        _advertised = envelope.ContainsKey("actionPolicy");
        if (!_advertised) return; // No assertion from an old KV. Retain the last applied policy.
        try
        {
            var raw = envelope["actionPolicy"] as JsonObject;
            var incoming = ClassroomPolicy.Parse(raw);
            // An older server cannot replace a rule it cannot express or read its receipt.
            if (incoming.SchemaVersion == 1 && _current?.SwitchMode is not null ||
                incoming.SchemaVersion < 3 && _current is { Source: not "LEGACY", SchemaVersion: >= 3 } priorV3 &&
                (priorV3.Stop == "default" || priorV3.Exit == "default") ||
                incoming.SchemaVersion < 4 && _current?.StartManualStudy is not null)
            {
                _advertised = false;
                return;
            }
            if ((_awaitingSave ?? _current) is { } prior && !incoming.CanFollow(prior))
                throw new InvalidDataException();
            if (_hold || _current?.Revision != incoming.Revision || _current.SchemaVersion != incoming.SchemaVersion)
            {
                _awaitingSave = incoming;
                Write(new(1, scope, raw, false));
            }
            bool changed = _hold || _current?.PolicyId != incoming.PolicyId;
            _current = incoming;
            _awaitingSave = null;
            _hold = _dirty = false;
            _error = null;
            if (changed) Install();
        }
        catch (Exception e) when (StorageOrDataError(e))
        {
            _error = e is IOException or UnauthorizedAccessException
                ? "CLASSROOM_POLICY_STORE_UNAVAILABLE" : "CLASSROOM_POLICY_INVALID";
            // Preserve a previously applied policy on malformed or stale input. With
            // no verified baseline, fail closed until a valid response is persisted.
            if (_current is null || _awaitingSave is { } waiting && _current.PolicyId != waiting.PolicyId)
            {
                // A known change may revoke a USB key or forbid exit. Fence the old
                // grant even when the normal policy file could not be promoted.
                _hold = true;
                Install();
                try { Write(new(1, scope, null, true)); _dirty = false; }
                catch (Exception writeError) when (StorageOrDataError(writeError)) { _dirty = true; }
            }
        }
    }

    internal JsonObject? Observe() => !_advertised ? null : new JsonObject
    {
        ["schemaVersion"] = _current?.SchemaVersion ?? 1, ["revision"] = _hold ? null : _current?.Revision,
        ["policyId"] = _hold ? null : _current?.PolicyId, ["error"] = _error
    };

    private void Install()
    {
        var policy = _current;
        string? manualRule = _hold ? "deny" : policy?.StartManualStudy;
        // An appointment belongs to this binding and action, independently of other published rules.
        var manualKeys = ClassroomPolicy.UsesUsb(manualRule) ? policy!.Keys : Array.Empty<ClassroomUsbKey>();
        var manualScope = new { scope = _scope, rule = manualRule ?? "default", keys = manualKeys };
        string manualVersion = Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(manualScope)));
        if (!_hold && (policy is null || policy.Source == "LEGACY"))
        {
            _noise.SetClassroomActionPolicy(null, null, null, null, manualStudyScopeVersion: manualVersion);
            return;
        }
        var id = _hold ? "awaiting-policy" : policy!.PolicyId;
        var token = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(_scope + "\n" + id)));
        _noise.SetClassroomActionPolicy(token, _hold ? "deny" : policy!.Stop, _hold ? "deny" : policy!.Exit,
            _hold ? null : () => _usb.HasRegisteredKey(policy!.Keys), _scope, _hold ? "deny" : policy!.SwitchMode,
            manualRule, manualVersion);
    }

    private void Write(Saved next)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        using (var output = new FileStream(_path + ".tmp", FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
        {
            JsonSerializer.Serialize(output, next);
            output.Flush(true);
        }
        File.Move(_path + ".tmp", _path, true);
    }

    private static bool StorageOrDataError(Exception e) =>
        e is IOException or InvalidDataException or UnauthorizedAccessException or JsonException or InvalidOperationException or FormatException or OverflowException;
}

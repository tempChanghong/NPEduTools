using System.Security.Cryptography;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Win32;
using NPEduTools.Contracts;
using NPEduTools.Integrations.Npep;

namespace NPEduTools.Host;

[SupportedOSPlatform("windows")]
public sealed class ManagedUninstall
{
    private const string InstallerKey = @"Software\NPEduTools\Installer";
    private const string RecordName = "ManagedUninstallBinding";
    private const string GrantName = "ManagedUninstallGrant";
    private const string PolicyAuthorityName = "SchoolPolicyAuthority";
    private const string LegacyCandidatesName = "ManagedUninstallLegacyCandidates";
    private const string MutationMutex = @"Global\NPEduTools.ManagedUninstall.v1";
    private readonly ClassroomUsbPresence _usb = new();
    private readonly SemaphoreSlim _work = new(1, 1);
    private string? _shownVersion;
    private long _lastFailure;
    private int _failed;

    private sealed record Binding(Guid InstallId, string Status, string? Origin = null, string? Server = null,
        string? Epoch = null, string? School = null, string? Class = null, string? Screen = null, string? Device = null,
        long Revision = 0, bool Detached = false);
    private sealed record Grant(Guid Attempt, Guid InstallId, string Device, long Revision, string InstallRoot,
        string InstallerVersion, string EncryptedToken, DateTimeOffset Expires);

    internal static bool CheckFailure(Exception error) => error is NpepException or IOException or UnauthorizedAccessException or
        System.Security.SecurityException or InvalidOperationException or FormatException or JsonException or CryptographicException or
        HttpRequestException or OperationCanceledException or ArgumentException;

    internal static bool IsInstalled()
    {
        using var key = Open(false);
        if (key?.GetValue("ManagedUninstallProtocol") is not 1 ||
            key.GetValue("ManagedUninstallRoot") is not string root) return false;
        return string.Equals(Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory),
            Path.Combine(root, "app", "Host"), StringComparison.OrdinalIgnoreCase);
    }

    internal string PolicyDirectory => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "school-policy-state"));

    internal FileStream AcquirePolicyLease()
    {
        // The installed cache is machine-wide even when Windows has multiple sessions.
        return new FileStream(Path.Combine(PolicyDirectory, "host.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
    }

    internal bool RequiresVerifiedPolicy()
    {
        try
        {
            using var key = Open(false);
            return key is null || key.GetValue(PolicyAuthorityName) is not null || ReadBinding(key).Status != "UNMANAGED";
        }
        catch (Exception error) when (CheckFailure(error)) { return true; }
    }

    internal bool AllowsPolicyScope(string scope, string? origin = null)
    {
        try
        {
            using var key = Open(false);
            if (key is null) return false;
            var authority = Read<Binding>(key, PolicyAuthorityName) ?? ReadBinding(key);
            var identity = JsonNode.Parse(scope);
            return authority.Status == "MANAGED" && (origin is null || NpepApi.ValidateOrigin(origin) == NpepApi.ValidateOrigin(authority.Origin!)) &&
                Text(identity, "deviceId") == authority.Device &&
                Text(identity, "serverInstanceId") == authority.Server && Text(identity, "deploymentEpoch") == authority.Epoch &&
                identity?["bindingRevision"]?.GetValue<long>() == authority.Revision &&
                identity?["credentialGeneration"]?.GetValue<int>() == 1;
        }
        catch (Exception error) when (CheckFailure(error)) { return false; }
    }

    private static RegistryKey? Open(bool writable)
    {
        using var hive = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        return hive.OpenSubKey(InstallerKey, writable);
    }

    private static T? Read<T>(RegistryKey key, string name) =>
        key.GetValue(name) switch
        {
            null => default,
            string body when body.Length <= 8192 => JsonSerializer.Deserialize<T>(body),
            _ => throw new InvalidDataException("Invalid managed uninstall record.")
        };

    private static Binding ReadBinding(RegistryKey key) =>
        Read<Binding>(key, RecordName) is { InstallId: var id, Status: "UNMANAGED" or "PENDING" or "MANAGED" or "UNKNOWN" } binding &&
        id != Guid.Empty ? binding : throw new NpepException("UNINSTALL_BINDING_UNKNOWN");

    private static void Write<T>(RegistryKey key, string name, T value)
    {
        key.SetValue(name, JsonSerializer.Serialize(value), RegistryValueKind.String);
        key.Flush();
    }

    private static void Mutate(Action action)
    {
        using var mutex = new Mutex(false, MutationMutex);
        bool acquired;
        try { acquired = mutex.WaitOne(TimeSpan.FromSeconds(2)); }
        catch (AbandonedMutexException) { acquired = true; }
        if (!acquired) throw new NpepException("UNINSTALL_BUSY");
        try { action(); }
        finally { mutex.ReleaseMutex(); }
    }

    // Only administrator-owned registry values can identify an installation.
    // Create the first marker before a managed confirmation can write remotely.
    internal static int Initialize(string installationKind)
    {
        if (!IsInstalled()) return 1;
        using var key = Open(true) ?? throw new NpepException("UNINSTALL_BINDING_UNKNOWN");
        Mutate(() =>
        {
            if (key.GetValue(RecordName) is not null) _ = ReadBinding(key);
            else
            {
                bool clean = installationKind == "fresh" && NoExistingProfilesWithCredentials();
                if (installationKind == "legacy-upgrade")
                    Write(key, LegacyCandidatesName, CaptureLegacyCandidates());
                Write(key, RecordName, new Binding(Guid.NewGuid(), clean ? "UNMANAGED" : "UNKNOWN"));
            }
        });
        return 0;
    }

    private static Dictionary<string, string> CaptureLegacyCandidates()
    {
        using var hive = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        using var profiles = hive.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\ProfileList");
        var candidates = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (string sid in profiles?.GetSubKeyNames() ?? [])
        {
            if (candidates.Count > 1) return candidates;
            if (sid is "S-1-5-18" or "S-1-5-19" or "S-1-5-20") continue;
            using var profile = profiles!.OpenSubKey(sid);
            if (profile?.GetValue("ProfileImagePath") is not string directory || !Path.IsPathFullyQualified(directory)) return [];
            var file = Path.Combine(directory, "AppData", "Local", "NPEduTools", "config", "npep", "npep.credentials.dpapi");
            try
            {
                using var stream = File.OpenRead(file);
                if (stream.Length is <= 0 or > 131072) return [];
                candidates.Add(sid, Convert.ToHexStringLower(SHA256.HashData(stream)));
            }
            catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException) { }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return []; }
        }
        return candidates;
    }

    private static bool NoExistingProfilesWithCredentials()
    {
        using var hive = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        using var profiles = hive.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\ProfileList");
        if (profiles is null) return false;
        foreach (string sid in profiles.GetSubKeyNames())
        {
            if (sid is "S-1-5-18" or "S-1-5-19" or "S-1-5-20") continue;
            using var profile = profiles.OpenSubKey(sid);
            if (profile?.GetValue("ProfileImagePath") is not string directory || !Path.IsPathFullyQualified(directory)) return false;
            string file = Path.Combine(directory, "AppData", "Local", "NPEduTools", "config", "npep", "npep.credentials.dpapi");
            try { _ = File.GetAttributes(file); return false; }
            catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException) { }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return false; }
        }
        string current = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "NPEduTools", "config", "npep", "npep.credentials.dpapi");
        try { _ = File.GetAttributes(current); return false; }
        catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException) { return true; }
    }

    internal void ObserveBinding(JsonObject view) => Mutate(() => ObserveBindingCore(view));

    private static void ObserveBindingCore(JsonObject view)
    {
        var registration = view["registration"] as JsonObject;
        string? state = view["state"]?.GetValue<string>();
        if (registration is null && state != "CONFIRMING") return;
        using var key = Open(true) ?? throw new NpepException("UNINSTALL_BINDING_UNKNOWN");
        var prior = ReadBinding(key);
        if (registration is null)
        {
            if (prior.Status == "UNMANAGED")
            {
                Write(key, RecordName, prior with { Status = "PENDING" });
                key.DeleteValue(GrantName, false);
            }
            return;
        }
        var current = CreateBinding(prior.InstallId, view, registration);
        if (prior.Status == "UNKNOWN") return; // New pairing does not replace unresolved uninstall history.
        var recordedAuthority = Read<Binding>(key, PolicyAuthorityName);
        var authority = recordedAuthority ?? prior;
        if (state == "REVOKED")
        {
            if (authority with { Detached = false } == current) Write(key, PolicyAuthorityName, current with { Detached = true });
            return;
        }
        if (state == "ACTIVE" && CanUpdatePolicyAuthority(prior.Status, authority.Status,
            authority with { Detached = false } == current, recordedAuthority is { Detached: true }))
            Write(key, PolicyAuthorityName, current);
        if (prior.Status == "MANAGED" && prior with { Detached = false } != current)
            return; // A local re-pair cannot release the original school's protection.
        if (state is "UNPAIRING" or "SUSPENDED")
        {
            key.DeleteValue(GrantName, false);
            if (!prior.Detached) Write(key, RecordName, current with { Detached = true });
            return;
        }
        if (prior.Detached || prior == current) return;
        Write(key, RecordName, current);
        key.DeleteValue(GrantName, false);
    }

    private static Binding CreateBinding(Guid installId, JsonObject view, JsonObject registration) =>
        new(installId, "MANAGED", NpepApi.ValidateOrigin(Text(view, "origin")),
            Text(registration, "serverInstanceId"), Text(registration, "deploymentEpoch"), Text(registration, "schoolId"),
            Text(registration, "administrativeClassId"), Text(registration, "screenBindingId"), Text(registration, "deviceId"),
            registration["bindingRevision"]?.GetValue<long>() ?? throw new InvalidDataException());

    internal async Task RecoverLegacyBindingAsync(NpepRuntime npep, CancellationToken token)
    {
        using var key = Open(false) ?? throw new NpepException("UNINSTALL_BINDING_UNKNOWN");
        Binding prior = ReadBinding(key);
        if (prior.Status != "UNKNOWN") return;
        using var user = System.Security.Principal.WindowsIdentity.GetCurrent();
        string sid = user.User?.Value ?? throw new NpepException("UNINSTALL_LEGACY_EVIDENCE_REQUIRED");
        var candidates = Read<Dictionary<string, string>>(key, LegacyCandidatesName);
        string? fingerprint = npep.LegacyUninstallFingerprint;
        if (!MatchesLegacyEvidence(candidates, sid, fingerprint))
            throw new NpepException("UNINSTALL_LEGACY_EVIDENCE_REQUIRED");
        var device = npep.ReadUninstallBinding();
        if (device["state"]?.GetValue<string>() != "ACTIVE" || device["registration"] is not JsonObject registration)
            throw new NpepException("UNINSTALL_ORIGINAL_BINDING_REQUIRED");
        Binding recovered = CreateBinding(prior.InstallId, device, registration);
        var policy = await npep.ManagedUninstallAsync("check", prior.InstallId, null, token);
        if (policy["identity"] is not JsonObject identity || !Matches(recovered, identity) ||
            Text(identity, "installationId") != Text(registration, "installationId"))
            throw new NpepException("UNINSTALL_BINDING_CHANGED");
        Mutate(() =>
        {
            using var writable = Open(true) ?? throw new NpepException("UNINSTALL_BINDING_UNKNOWN");
            if (ReadBinding(writable) == recovered) return;
            if (ReadBinding(writable) != prior || npep.LegacyUninstallFingerprint != fingerprint ||
                !JsonNode.DeepEquals(npep.ReadUninstallBinding(), device)) throw new NpepException("UNINSTALL_BINDING_CHANGED");
            writable.DeleteValue(GrantName, false);
            // A new authorized pairing can have its own classroom policy authority;
            // this operation only restores the immutable original uninstall binding.
            Write(writable, RecordName, recovered);
            writable.DeleteValue(LegacyCandidatesName, false);
        });
    }

    internal static bool MatchesLegacyEvidence(IReadOnlyDictionary<string, string>? candidates, string sid, string? fingerprint) =>
        fingerprint is not null && candidates is { Count: 1 } && candidates.TryGetValue(sid, out string? expected) && fingerprint == expected;

    internal static bool CanUpdatePolicyAuthority(string originalStatus, string authorityStatus, bool sameBinding, bool confirmedRelease) =>
        originalStatus != "UNKNOWN" && (authorityStatus != "MANAGED" || sameBinding || confirmedRelease);

    internal async Task<ManagedUninstallStatus> ExchangeAsync(NpepRuntime npep, ManagedUninstallInput? input, CancellationToken token)
    {
        if (!await _work.WaitAsync(TimeSpan.Zero, token)) throw new NpepException("UNINSTALL_BUSY");
        try
        {
            using var key = Open(input is not null) ?? throw new NpepException("UNINSTALL_BINDING_UNKNOWN");
            var binding = ReadBinding(key);
            if (binding.Status == "UNKNOWN" && input is null)
            {
                await RecoverLegacyBindingAsync(npep, token);
                binding = ReadBinding(key);
            }
            if (binding.Status == "UNMANAGED") return new("UNMANAGED");
            if (binding.Status != "MANAGED") throw new NpepException("UNINSTALL_BINDING_UNKNOWN");
            if (binding.Detached) throw new NpepException("UNINSTALL_BINDING_DETACHED");
            var device = npep.ReadUninstallBinding();
            if (device["state"]?.GetValue<string>() != "ACTIVE" ||
                device["registration"] is not JsonObject registration ||
                registration["deviceId"]?.GetValue<string>() != binding.Device ||
                NpepApi.ValidateOrigin(Text(device, "origin")) != NpepApi.ValidateOrigin(binding.Origin!))
                throw new NpepException("UNINSTALL_ORIGINAL_BINDING_REQUIRED");

            var policy = await npep.ManagedUninstallAsync("check", binding.InstallId, null, token);
            if (policy["identity"] is not JsonObject identity || !Matches(binding, identity))
                throw new NpepException("UNINSTALL_BINDING_CHANGED");
            string version = Text(policy, "version");
            if (!ClassroomPolicy.IsDigest(version)) throw new InvalidDataException();
            string target = Text(policy, "target");
            if (target.Length > 360 || target.Any(char.IsControl)) throw new InvalidDataException();
            if (policy["rule"] is not JsonObject rule || rule["mode"]?.GetValue<string>() != "VERIFY")
                return new("DENIED", target);
            string factor = Text(rule, "factor");
            if (factor is not ("CLASS_PASSWORD" or "USB" or "ANY" or "ALL")) throw new InvalidDataException();
            if (input is null)
            {
                _shownVersion = version;
                return new("VERIFY", target, factor, version);
            }
            if (_shownVersion != version || input.PolicyVersion != version)
                throw new NpepException("UNINSTALL_POLICY_CHANGED");
            if (_failed >= 5 && Environment.TickCount64 - _lastFailure < 30000)
                throw new NpepException("UNINSTALL_TRY_LATER");
            if (Environment.TickCount64 - _lastFailure >= 30000) _failed = 0;
            if (!VerifyFactors(factor, policy, input.Password))
            {
                _failed++;
                _lastFailure = Environment.TickCount64;
                throw new NpepException("UNINSTALL_VERIFICATION_FAILED");
            }
            _failed = 0;
            var issued = await npep.ManagedUninstallAsync("issue", binding.InstallId, version, token);
            if (ReadBinding(key) != binding) throw new NpepException("UNINSTALL_BINDING_CHANGED");
            string value = Text(issued, "token");
            if (value.Length != 43 || !value.All(ch => char.IsAsciiLetterOrDigit(ch) || ch is '-' or '_') ||
                !DateTimeOffset.TryParse(Text(issued, "expiresAt"), out var expires) || expires <= DateTimeOffset.UtcNow ||
                expires > DateTimeOffset.UtcNow.AddMinutes(4)) throw new InvalidDataException();
            var bytes = Encoding.ASCII.GetBytes(value);
            try
            {
                var attempt = Guid.NewGuid();
                var grant = new Grant(attempt, binding.InstallId, binding.Device!, binding.Revision,
                    InstallValue(key, "ManagedUninstallRoot"), InstallValue(key, "InstallerVersion"),
                    Convert.ToBase64String(NpepVault.ProtectUninstallGrant(bytes, true)), expires);
                Mutate(() =>
                {
                    if (ReadBinding(key) != binding || !SameInstallation(key, grant) ||
                        npep.ReadUninstallBinding()["state"]?.GetValue<string>() != "ACTIVE")
                        throw new NpepException("UNINSTALL_BINDING_CHANGED");
                    Write(key, GrantName, grant);
                });
                return new("AUTHORIZED", target, Attempt: attempt);
            }
            finally { CryptographicOperations.ZeroMemory(bytes); }
        }
        finally { _work.Release(); }
    }

    private bool VerifyFactors(string factor, JsonObject policy, string? password)
    {
        bool pass = factor != "USB" && VerifyPassword(policy["credential"], password);
        if (factor == "CLASS_PASSWORD" || factor == "ANY" && pass) return pass;
        if (factor == "ALL" && !pass) return false;
        if (policy["usbKeys"] is not JsonArray rows || rows.Count > 64) return false;
        var keys = rows.Select(item => new ClassroomUsbKey(Text(item, "keyId"), Text(item, "mediaHash"), Text(item, "secretHash"))).ToArray();
        if (keys.Any(key => !Guid.TryParse(key.KeyId, out _) || !ClassroomPolicy.IsDigest(key.MediaHash) ||
            !ClassroomPolicy.IsDigest(key.SecretHash))) return false;
        return _usb.HasRegisteredKey(keys);
    }

    private static bool VerifyPassword(JsonNode? credential, string? password)
    {
        if (!NoiseManagementContract.SecretValid(password) || credential?["verifier"] is not JsonObject verifier ||
            verifier["iterations"]?.GetValue<int>() != 210000) return false;
        var salt = Convert.FromBase64String(Text(verifier, "salt"));
        var hash = Convert.FromBase64String(Text(verifier, "hash"));
        if (salt.Length != 16 || hash.Length != 32) return false;
        var check = Rfc2898DeriveBytes.Pbkdf2(password!, salt, 210000, HashAlgorithmName.SHA256, 32);
        try { return CryptographicOperations.FixedTimeEquals(check, hash); }
        finally { CryptographicOperations.ZeroMemory(check); }
    }

    private static bool Matches(Binding binding, JsonObject identity) =>
        binding.Device == Text(identity, "deviceId") && binding.Screen == Text(identity, "bindingId") &&
        binding.School == Text(identity, "schoolId") && binding.Class == Text(identity, "classId") &&
        binding.Server == Text(identity, "serverInstanceId") && binding.Epoch == Text(identity, "deploymentEpoch") &&
        binding.Revision == identity["bindingRevision"]?.GetValue<long>();

    internal static async Task<int> NativeGateAsync(string operation, Guid? attempt)
    {
        if (!IsInstalled()) return 1;
        using var key = Open(operation == "consume") ?? throw new NpepException("UNINSTALL_BINDING_UNKNOWN");
        var binding = ReadBinding(key);
        if (binding.Status == "UNMANAGED") return 0;
        if (binding.Status != "MANAGED" || binding.Detached || attempt == Guid.Empty) return 1;
        var grant = Read<Grant>(key, GrantName);
        // Windows Settings supplies no product argument. It can only consume a recent
        // grant issued after local verification for this exact Windows account and installation.
        if (grant is null || grant.Attempt == Guid.Empty || attempt is { } requested && grant.Attempt != requested ||
            grant.InstallId != binding.InstallId ||
            grant.Device != binding.Device || grant.Revision != binding.Revision || !SameInstallation(key, grant) ||
            grant.Expires <= DateTimeOffset.UtcNow || binding.Origin is null) return 1;
        // The old Windows user's short token cannot be used by a new login.
        var bytes = NpepVault.ProtectUninstallGrant(Convert.FromBase64String(grant.EncryptedToken), false);
        try
        {
            if (operation == "probe") return 0;
            var body = new JsonObject
            {
                ["requestId"] = Guid.NewGuid().ToString("D"), ["installId"] = binding.InstallId.ToString("D"),
                ["token"] = Encoding.ASCII.GetString(bytes)
            };
            using var api = new NpepApi(binding.Origin);
            var receipt = await api.ManagedUninstallAsync("redeem", body, null, default);
            if (ReadBinding(key) != binding || receipt["authorized"]?.GetValue<bool>() != true ||
                Text(receipt, "installId") != binding.InstallId.ToString("D") || Text(receipt, "deviceId") != binding.Device ||
                receipt["bindingRevision"]?.GetValue<long>() != binding.Revision) return 1;
            bool unchanged = false;
            Mutate(() =>
            {
                if (ReadBinding(key) != binding || Read<Grant>(key, GrantName) != grant || !SameInstallation(key, grant)) return;
                key.DeleteValue(GrantName, false);
                unchanged = true;
            });
            if (!unchanged) return 1;
            return 0;
        }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }

    private static string Text(JsonNode? node, string property) =>
        node?[property]?.GetValue<string>() is { Length: > 0 } value ? value : throw new InvalidDataException();

    private static string InstallValue(RegistryKey key, string name) =>
        key.GetValue(name) is string { Length: > 0 } value ? value : throw new InvalidDataException();

    private static bool SameInstallation(RegistryKey key, Grant grant) =>
        string.Equals(InstallValue(key, "ManagedUninstallRoot"), grant.InstallRoot, StringComparison.OrdinalIgnoreCase) &&
        InstallValue(key, "InstallerVersion") == grant.InstallerVersion;
}

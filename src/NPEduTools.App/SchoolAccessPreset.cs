using System.Globalization;
using System.IO;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using NPEduTools.Contracts;

namespace NPEduTools.App;

internal sealed record SchoolAccessPreset(string BackendUrl, string SchoolName, string? SchoolCode)
{
    internal const string ProvisionArgument = "--provision-school-access";
    private const string FileName = "school-access-v1.json";
    private const int MaxBytes = 8192;

    internal static SchoolAccessPreset? ReadForOnboarding()
    {
        string profile = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string? legacy = profile.Length == 0 ? null : Path.Combine(profile, "NPDownloader", FileName);
        if (!OperatingSystem.IsWindows()) return legacy is null ? null : Read(legacy);
        try
        {
            string? root = MachineRoot();
            if (root is not null)
            {
                string path = Path.Combine(root, FileName);
                FileAttributes? attributes = Attributes(path);
                if (attributes is not null)
                    return (attributes.Value & (FileAttributes.ReparsePoint | FileAttributes.Directory)) == 0 &&
                           ProtectedDirectory(new DirectoryInfo(root)) && Protected(new FileInfo(path).GetAccessControl(), true)
                        ? Read(path) : null;
            }
            return legacy is null ? null : Read(legacy);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Security.SecurityException or ArgumentException)
        { return null; }
    }

    internal static SchoolAccessPreset? Read(string path)
    {
        try
        {
            using var file = File.OpenRead(path);
            if (file.Length is < 1 or > MaxBytes) return null;
            byte[] bytes = new byte[(int)file.Length];
            file.ReadExactly(bytes);
            return Parse(bytes);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException)
        { return null; }
    }

    private static SchoolAccessPreset? Parse(byte[] bytes)
    {
        try
        {
            using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 2 });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;
            var fields = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
            foreach (var field in root.EnumerateObject())
            {
                if (field.Name is not ("schemaVersion" or "backendUrl" or "schoolName" or "schoolCode") ||
                    !fields.TryAdd(field.Name, field.Value)) return null;
            }
            if (!fields.TryGetValue("schemaVersion", out var schema) || schema.ValueKind != JsonValueKind.Number ||
                !schema.TryGetInt32(out int version) || version != 1) return null;
            string? ReadText(string name) => fields.TryGetValue(name, out var value) && value.ValueKind == JsonValueKind.String
                ? value.GetString() : null;
            string? address = ReadText("backendUrl"), name = ReadText("schoolName"), code = ReadText("schoolCode");
            if (address is null || name is not { Length: > 0 and <= 191 } || name != name.Trim() ||
                name.Any(c => char.IsControl(c) || char.GetUnicodeCategory(c) == UnicodeCategory.Format) ||
                fields.TryGetValue("schoolCode", out var schoolCode) && schoolCode.ValueKind is not (JsonValueKind.String or JsonValueKind.Null) ||
                code is not null && (code.Length is < 1 or > 64 ||
                    !code.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-' or ':')) ||
                !NpepContract.Valid(new("inspect", 0, address)) || !Uri.TryCreate(address, UriKind.Absolute, out var origin) ||
                origin.Host.Length == 0 || !origin.IsWellFormedOriginalString() || address != origin.GetLeftPart(UriPartial.Authority))
                return null;
            return new(address, name, code);
        }
        catch (Exception error) when (error is JsonException or ArgumentException)
        { return null; }
    }

    internal static SchoolAccessPreset? ReadEnvelope(string value)
    {
        if (value.Length is < 1 or > 10923 || value.Length % 4 == 1 ||
            !value.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_')) return null;
        try
        {
            byte[] decoded = Convert.FromBase64String(value.Replace('-', '+').Replace('_', '/').PadRight((value.Length + 3) / 4 * 4, '='));
            return decoded.Length <= MaxBytes ? Parse(decoded) : null;
        }
        catch (FormatException) { return null; }
    }

    internal static int ProvisionMachine(string value)
    {
        if (!OperatingSystem.IsWindows()) return 3;
        if (ReadEnvelope(value) is not { } preset) return 2;
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            if (!new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator) || MachineRoot() is not { } root)
                return 3;
            var directory = new DirectoryInfo(root);
            var parent = directory.Parent!;
            if (!ProtectedDirectory(parent.Parent!, checkWrite: false)) return 3;
            EnsureProtectedDirectory(parent);
            EnsureProtectedDirectory(directory);
            string target = Path.Combine(root, FileName);
            if (Attributes(target) is { } attributes &&
                ((attributes & (FileAttributes.ReparsePoint | FileAttributes.Directory)) != 0 ||
                 !Protected(new FileInfo(target).GetAccessControl(), true))) return 3;

            string temporary = Path.Combine(root, "." + Guid.NewGuid().ToString("N") + ".tmp");
            try
            {
                using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    JsonSerializer.Serialize(file, new
                    {
                        schemaVersion = 1, backendUrl = preset.BackendUrl, schoolName = preset.SchoolName, schoolCode = preset.SchoolCode
                    }, new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
                    file.Flush(flushToDisk: true);
                }
                var temporaryFile = new FileInfo(temporary);
                var fileSecurity = temporaryFile.GetAccessControl();
                fileSecurity.SetOwner(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null));
                temporaryFile.SetAccessControl(fileSecurity);
                File.Move(temporary, target, overwrite: true);
            }
            finally { File.Delete(temporary); }
            return 0;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Security.SecurityException or ArgumentException)
        { return 3; }
    }

    private static string? MachineRoot()
    {
        string root = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        return root.Length == 0 ? null : Path.Combine(root, "NPEduTools", "Provisioning");
    }

    private static FileAttributes? Attributes(string path)
    {
        try { return File.GetAttributes(path); }
        catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException) { return null; }
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static void EnsureProtectedDirectory(DirectoryInfo directory)
    {
        if (Attributes(directory.FullName) is null)
        {
            var administrators = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
            var security = new DirectorySecurity();
            security.SetOwner(administrators);
            security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
            void Allow(SecurityIdentifier sid, FileSystemRights rights) =>
                security.AddAccessRule(new FileSystemAccessRule(sid, rights,
                    InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
            Allow(administrators, FileSystemRights.FullControl);
            Allow(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), FileSystemRights.FullControl);
            Allow(new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null), FileSystemRights.ReadAndExecute);
            directory.Create(security);
        }
        if (!ProtectedDirectory(directory)) throw new UnauthorizedAccessException();
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static bool ProtectedDirectory(DirectoryInfo directory, bool checkWrite = true)
    {
        for (DirectoryInfo? current = directory; current is not null; current = current.Parent)
        {
            if (Attributes(current.FullName) is not { } attributes ||
                (attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != FileAttributes.Directory ||
                !Protected(current.GetAccessControl(), checkWrite)) return false;
            checkWrite = false;
        }
        return true;
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static bool Protected(FileSystemSecurity security, bool checkWrite)
    {
        static bool Trusted(IdentityReference? owner) => owner?.Value is "S-1-5-32-544" or "S-1-5-18" or
            "S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464";
        if (!Trusted(security.GetOwner(typeof(SecurityIdentifier)))) return false;
        var rights = FileSystemRights.Delete | FileSystemRights.DeleteSubdirectoriesAndFiles |
            FileSystemRights.ChangePermissions | FileSystemRights.TakeOwnership | (FileSystemRights)0x10000000;
        if (checkWrite) rights |= FileSystemRights.Write | (FileSystemRights)0x40000000;
        return !security.GetAccessRules(true, true, typeof(SecurityIdentifier)).OfType<FileSystemAccessRule>()
            .Any(rule => rule.AccessControlType == AccessControlType.Allow &&
                (rule.PropagationFlags & PropagationFlags.InheritOnly) == 0 && (rule.FileSystemRights & rights) != 0 &&
                !Trusted(rule.IdentityReference));
    }
}

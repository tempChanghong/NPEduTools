using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;

namespace NPEduTools.Host;

// Only the Host inspects mounted USB media. IPC carries neither a drive path nor proof of presence.
internal sealed class ClassroomUsbPresence
{
    private const string KeyFileName = "NPEduTools.classroom-key.json";
    private const int MaxFileBytes = 512;
    private Task<bool>? _probe;
    private readonly object _sync = new();

    internal bool HasRegisteredKey(IReadOnlyList<ClassroomUsbKey> keys)
    {
        if (keys.Count == 0 || !OperatingSystem.IsWindows()) return false;
        // A stalled external drive must not leave the classroom operation waiting indefinitely.
        // An unfinished or timed-out enumeration is never reused as fresh evidence.
        Task<bool> probe;
        lock (_sync)
        {
            if (_probe is { IsCompleted: false }) return false;
            probe = _probe = Task.Run(() => ProbeVolumes(keys));
        }
        return probe.Wait(TimeSpan.FromSeconds(1)) && probe.Result;
    }

    private static bool ProbeVolumes(IReadOnlyList<ClassroomUsbKey> keys)
    {
        try
        {
            foreach (var drive in DriveInfo.GetDrives())
            {
                try
                {
                    if (drive.DriveType is not (DriveType.Removable or DriveType.Fixed)) continue;
                    var root = drive.RootDirectory.FullName;
                    var path = Path.Combine(root, KeyFileName);
                    var media = ReadMediaHash(root);
                    if (media is null || !keys.Any(k => k.MediaHash == media)) continue;
                    // OPEN_REPARSE_POINT ensures a link on the drive cannot point at a
                    // credential on a different volume. Reject every reparse file.
                    using var handle = CreateFile(path, 0x80000000, 1, IntPtr.Zero, 3, 0x00200000, IntPtr.Zero);
                    if (handle.IsInvalid || !GetFileInformationByHandleEx(handle, 9, out var attributes, 8) ||
                        (attributes.Attributes & (FileAttributes.ReparsePoint | FileAttributes.Directory)) != 0) continue;
                    using var file = new FileStream(handle, FileAccess.Read);
                    if (Matches(file, media, keys) && ReadMediaHash(root) == media) return true;
                }
                catch (Exception e) when (ReadFailure(e)) { }
            }
        }
        catch (Exception e) when (ReadFailure(e)) { }
        return false;
    }

    internal static bool Matches(Stream keyFile, string mediaHash, IReadOnlyList<ClassroomUsbKey> keys)
    {
        byte[] bytes = new byte[MaxFileBytes + 1];
        byte[]? secret = null;
        try
        {
            int size = keyFile.ReadAtLeast(bytes, bytes.Length, throwOnEndOfStream: false);
            if (size is 0 or > MaxFileBytes) return false;
            int start = bytes.AsSpan(0, size).StartsWith(Encoding.UTF8.Preamble) ? 3 : 0;
            using var json = JsonDocument.Parse(bytes.AsMemory(start, size - start));
            var record = json.RootElement;
            if (record.ValueKind != JsonValueKind.Object || record.EnumerateObject().Count() != 3 ||
                !record.TryGetProperty("schemaVersion", out var schema) || !schema.TryGetInt32(out var version) || version != 1 ||
                !record.TryGetProperty("keyId", out var key) || !record.TryGetProperty("secret", out var encoded)) return false;
            var match = keys.FirstOrDefault(item => item.MediaHash == mediaHash && item.KeyId == key.GetString());
            if (match is null || encoded.GetString() is not { Length: 43 } value ||
                !value.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_')) return false;
            secret = Convert.FromBase64String(value.Replace('-', '+').Replace('_', '/') + "=");
            return secret.Length == 32 && Convert.ToBase64String(secret).TrimEnd('=').Replace('+', '-').Replace('/', '_') == value &&
                CryptographicOperations.FixedTimeEquals(SHA256.HashData(secret), Convert.FromHexString(match.SecretHash));
        }
        catch (Exception e) when (ReadFailure(e)) { return false; }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
            if (secret is not null) CryptographicOperations.ZeroMemory(secret);
        }
    }

    private static bool ReadFailure(Exception e) =>
        e is IOException or UnauthorizedAccessException or System.Security.SecurityException or JsonException or
            InvalidOperationException or ArgumentException or FormatException or NotSupportedException;

    private static string? ReadMediaHash(string root)
    {
        if (root.Length != 3 || !char.IsAsciiLetter(root[0]) || root[1..] != ":\\") return null;
        using var volume = CreateFile(@"\\.\" + root[..2], 0, 7, IntPtr.Zero, 3, 0, IntPtr.Zero);
        if (volume.IsInvalid) return null;
        byte[] descriptor = new byte[4096];
        // STORAGE_PROPERTY_QUERY(StorageDeviceProperty, PropertyStandardQuery).
        if (!DeviceIoControl(volume, 0x002D1400, new byte[12], 12, descriptor, descriptor.Length, out var read, IntPtr.Zero) ||
            read < 36 || read > descriptor.Length) return null;
        return DecodeMediaHash(descriptor.AsSpan(0, read));
    }

    internal static string? DecodeMediaHash(ReadOnlySpan<byte> descriptor)
    {
        if (descriptor.Length < 36 || BinaryPrimitives.ReadUInt32LittleEndian(descriptor[28..]) != 7) return null;
        uint size = BinaryPrimitives.ReadUInt32LittleEndian(descriptor[4..]);
        if (size < 36 || size > descriptor.Length) return null;
        descriptor = descriptor[..(int)size];
        var vendor = Field(descriptor, 12);
        var product = Field(descriptor, 16);
        var serial = Field(descriptor, 24);
        if (vendor is null || product is null || string.IsNullOrEmpty(serial)) return null;
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(
            "NPEDUTOOLS-USB-MEDIA-V1\n" + vendor + "\n" + product + "\n" + serial)));
    }

    private static string? Field(ReadOnlySpan<byte> descriptor, int index)
    {
        uint offset = BinaryPrimitives.ReadUInt32LittleEndian(descriptor[index..]);
        if (offset == 0) return "";
        if (offset < 36 || offset >= descriptor.Length) return null;
        var data = descriptor[(int)offset..];
        int end = data.IndexOf((byte)0);
        if (end is < 0 or > 256) return null;
        foreach (byte b in data[..end]) if (b is < 32 or > 126) return null;
        return Encoding.ASCII.GetString(data[..end]).Trim(' ').ToUpperInvariant();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct AttributeTag { public FileAttributes Attributes; public uint Tag; }
    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr security,
        uint disposition, uint flags, IntPtr template);
    [DllImport("kernel32.dll", ExactSpelling = true)]
    private static extern bool GetFileInformationByHandleEx(SafeFileHandle file, int type, out AttributeTag data, uint size);
    [DllImport("kernel32.dll", ExactSpelling = true)]
    private static extern bool DeviceIoControl(SafeFileHandle device, uint code, byte[] input, int inputSize,
        [Out] byte[] output, int outputSize, out int returned, IntPtr overlapped);
}

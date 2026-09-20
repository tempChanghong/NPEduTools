using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace NPEduTools.Integrations.Npep;

// User-scoped DPAPI plus an exclusive profile lease. Never silently replace unreadable credentials.
public sealed class NpepVault : IDisposable
{
    private readonly string _path;
    private readonly FileStream _lease;
    public NpepVault(string directory)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        string root = Path.GetFullPath(directory);
        Directory.CreateDirectory(root);
        _path = Path.Combine(root, "npep.credentials.dpapi");
        _lease = new FileStream(Path.Combine(root, "npep.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
    }
    internal JsonObject? Load()
    {
        if (!File.Exists(_path)) return null;
        if (new FileInfo(_path).Length > 131072) throw new NpepException("CREDENTIAL_STORE_INVALID");
        byte[] clear = Protect(File.ReadAllBytes(_path), false);
        try { return NpepProtocol.Parse(clear); }
        finally { CryptographicOperations.ZeroMemory(clear); }
    }
    internal void Save(JsonObject value)
    {
        byte[] clear = Encoding.UTF8.GetBytes(value.ToJsonString(NpepProtocol.Json));
        byte[] encrypted;
        try { encrypted = Protect(clear, true); }
        finally { CryptographicOperations.ZeroMemory(clear); }
        string temp = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var file = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            { file.Write(encrypted); file.Flush(true); }
            if (File.Exists(_path)) File.Replace(temp, _path, null);
            else File.Move(temp, _path);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
    internal void Clear() { if (File.Exists(_path)) File.Delete(_path); }
    public void Dispose() => _lease.Dispose();

    [StructLayout(LayoutKind.Sequential)] private struct Blob { public int Size; public IntPtr Data; }
    [DllImport("crypt32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptProtectData(ref Blob input, string? description, IntPtr entropy, IntPtr reserved, IntPtr prompt, int flags, out Blob output);
    [DllImport("crypt32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptUnprotectData(ref Blob input, IntPtr description, IntPtr entropy, IntPtr reserved, IntPtr prompt, int flags, out Blob output);
    [DllImport("kernel32.dll")] private static extern IntPtr LocalFree(IntPtr memory);
    private static byte[] Protect(byte[] bytes, bool encrypt)
    {
        var input = new Blob { Size = bytes.Length, Data = Marshal.AllocHGlobal(bytes.Length) };
        Blob output = default;
        try
        {
            Marshal.Copy(bytes, 0, input.Data, bytes.Length);
            bool success = encrypt ? CryptProtectData(ref input, "NPEP N1 user credential", IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 1, out output)
                : CryptUnprotectData(ref input, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 1, out output);
            if (!success) throw new NpepException("CREDENTIAL_STORE_UNAVAILABLE");
            byte[] result = new byte[output.Size];
            Marshal.Copy(output.Data, result, 0, result.Length);
            return result;
        }
        finally
        {
            for (int i = 0; i < input.Size; i++) Marshal.WriteByte(input.Data, i, 0);
            Marshal.FreeHGlobal(input.Data);
            if (output.Data != IntPtr.Zero)
            {
                for (int i = 0; i < output.Size; i++) Marshal.WriteByte(output.Data, i, 0);
                LocalFree(output.Data);
            }
        }
    }
}

using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using Microsoft.Win32.SafeHandles;
using NPEduTools.Core;

namespace NPEduTools.Host;

/// <summary>
/// Windows HID transport for explicitly selected SoundLab-compatible reference meters.
/// This backend is not registered at Host startup. No microphone or vendor DLL is loaded.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsSoundMeterHid : ISoundMeterHidBackend
{
    private const uint PresentDeviceInterfaces = 0x12;
    private const int NoMoreItems = 259;
    private const int InsufficientBuffer = 122;

    public IReadOnlyList<SoundMeterHidDevice> Enumerate()
    {
        Native.HidD_GetHidGuid(out Guid hidGuid);
        IntPtr set = Native.SetupDiGetClassDevs(ref hidGuid, null, IntPtr.Zero, PresentDeviceInterfaces);
        if (set == new IntPtr(-1)) throw NativeError("Could not enumerate reference meters.");
        var devices = new List<SoundMeterHidDevice>();
        try
        {
            for (uint index = 0; index < 512; index++)
            {
                var entry = new Native.InterfaceData { Size = Marshal.SizeOf<Native.InterfaceData>() };
                if (!Native.SetupDiEnumDeviceInterfaces(set, IntPtr.Zero, ref hidGuid, index, ref entry))
                {
                    if (Marshal.GetLastWin32Error() == NoMoreItems) return devices;
                    throw NativeError("Could not enumerate reference meter interface.");
                }
                string path = GetDevicePath(set, ref entry);
                // Avoid opening unrelated HID devices even for metadata.
                if (!path.Contains("vid_64bd&pid_74e3", StringComparison.OrdinalIgnoreCase)) continue;
                using var handle = Native.CreateFile(path, 0, 3, IntPtr.Zero, 3, 0, IntPtr.Zero);
                if (handle.IsInvalid)
                {
                    // SoundLab holds an exclusive handle. Retain the OS-enumerated path as a
                    // provisional selection so Open can report busy/denied, rather than silently
                    // treating an attached meter as absent. Open revalidates actual HID attributes.
                    devices.Add(new(path, "SoundLab-compatible reference meter (access pending)",
                        Dly2202Protocol.VendorId, Dly2202Protocol.ProductId));
                    continue;
                }
                Native.Attributes attributes;
                try { attributes = GetAttributes(handle); }
                catch (IOException e)
                {
                    // Preserve the candidate for explicit diagnostics, but do not let one
                    // disconnected metadata handle hide all healthy subsequent devices.
                    devices.Add(new(path, "设备信息未确认，读取时将重新核对身份", Dly2202Protocol.VendorId,
                        Dly2202Protocol.ProductId, "AttributesUnavailable", (e.InnerException as Win32Exception)?.NativeErrorCode));
                    continue;
                }
                if (attributes.VendorId != Dly2202Protocol.VendorId ||
                    attributes.ProductId != Dly2202Protocol.ProductId) continue;
                var name = new StringBuilder(256);
                string label = Native.HidD_GetProductString(handle, name, name.Capacity * 2)
                    ? name.ToString() : "DLY-2202 / SoundLab-compatible reference meter";
                if (string.IsNullOrWhiteSpace(label)) label = "DLY-2202 / SoundLab-compatible reference meter";
                devices.Add(new(path, label, attributes.VendorId, attributes.ProductId));
            }
            throw new IOException("HID interface enumeration exceeds bounds.");
        }
        finally { Native.SetupDiDestroyDeviceInfoList(set); }
    }

    public ISoundMeterHidConnection Open(SoundMeterHidDevice device)
    {
        ArgumentNullException.ThrowIfNull(device);
        if (device.VendorId != Dly2202Protocol.VendorId || device.ProductId != Dly2202Protocol.ProductId)
            throw new SoundMeterException(SoundMeterError.WrongDevice, "Unsupported reference meter identity.");
        // Share mode 0 matches the vendor application: do not compete with an active SoundLab session.
        var handle = Native.CreateFile(device.Path, 0xC0000000, 0, IntPtr.Zero, 3, 0x40000000, IntPtr.Zero);
        if (handle.IsInvalid)
        {
            int error = Marshal.GetLastWin32Error();
            handle.Dispose();
            if (error is 5 or 32)
                throw new UnauthorizedAccessException("Reference meter is busy or access is denied.", new Win32Exception(error));
            throw new IOException("Could not open selected reference meter.", new Win32Exception(error));
        }
        try
        {
            var attributes = GetAttributes(handle);
            if (attributes.VendorId != device.VendorId || attributes.ProductId != device.ProductId)
                throw new SoundMeterException(SoundMeterError.WrongDevice, "Opened HID identity changed.");
            var caps = GetCapabilities(handle);
            if (!Dly2202Protocol.SupportsReportLength(caps.InputLength) ||
                !Dly2202Protocol.SupportsReportLength(caps.OutputLength))
                throw new SoundMeterException(SoundMeterError.UnsupportedReport, "Unverified reference meter HID report lengths.",
                    inputReportLength: caps.InputLength, outputReportLength: caps.OutputLength, stage: "Capabilities");
            // Disable stream buffering: one command must remain one HID report, even when input
            // and output report lengths differ.
            var stream = new FileStream(handle, FileAccess.ReadWrite, 1, isAsync: true);
            return new Connection(device, caps.InputLength, caps.OutputLength, stream);
        }
        catch { handle.Dispose(); throw; }
    }

    private static string GetDevicePath(IntPtr set, ref Native.InterfaceData entry)
    {
        Native.SetupDiGetDeviceInterfaceDetail(set, ref entry, IntPtr.Zero, 0, out uint required, IntPtr.Zero);
        int error = Marshal.GetLastWin32Error();
        if (error != InsufficientBuffer || required < 6 || required > 8192)
            throw new IOException("Invalid HID interface detail length.");
        IntPtr detail = Marshal.AllocHGlobal(checked((int)required));
        try
        {
            Marshal.WriteInt32(detail, IntPtr.Size == 8 ? 8 : 6);
            if (!Native.SetupDiGetDeviceInterfaceDetail(set, ref entry, detail, required, out _, IntPtr.Zero))
                throw NativeError("Could not read reference meter path.");
            // cbSize includes alignment, but the WCHAR DevicePath field begins immediately after DWORD.
            string path = Marshal.PtrToStringUni(IntPtr.Add(detail, 4), checked((int)(required - 4) / 2)) ?? "";
            int end = path.IndexOf('\0');
            if (end < 0 || end == 0) throw new IOException("Invalid HID interface path.");
            return path[..end];
        }
        finally { Marshal.FreeHGlobal(detail); }
    }

    private static Native.Attributes GetAttributes(SafeFileHandle handle)
    {
        var attributes = new Native.Attributes { Size = Marshal.SizeOf<Native.Attributes>() };
        if (!Native.HidD_GetAttributes(handle, ref attributes)) throw NativeError("Could not read reference meter identity.");
        return attributes;
    }

    private static Native.Capabilities GetCapabilities(SafeFileHandle handle)
    {
        if (!Native.HidD_GetPreparsedData(handle, out IntPtr data))
            throw NativeError("Could not read reference meter HID capabilities.");
        try
        {
            if (Native.HidP_GetCaps(data, out var caps) != 0x00110000)
                throw new IOException("Invalid reference meter HID capabilities.");
            return caps;
        }
        finally { Native.HidD_FreePreparsedData(data); }
    }

    private static IOException NativeError(string message) => new(message, new Win32Exception(Marshal.GetLastWin32Error()));

    private sealed class Connection(SoundMeterHidDevice device, int inputLength, int outputLength,
        FileStream stream) : ISoundMeterHidConnection
    {
        public SoundMeterHidDevice Device { get; } = device;
        public int InputReportLength { get; } = inputLength;
        public int OutputReportLength { get; } = outputLength;
        public ValueTask WriteAsync(ReadOnlyMemory<byte> report, CancellationToken cancellationToken)
        {
            if (report.Length != OutputReportLength) throw new ArgumentException("HID output length mismatch.", nameof(report));
            return stream.WriteAsync(report, cancellationToken);
        }
        public ValueTask<int> ReadAsync(Memory<byte> report, CancellationToken cancellationToken) =>
            stream.ReadAsync(report, cancellationToken);
        public void DiscardQueuedInput()
        {
            if (!Native.HidD_FlushQueue(stream.SafeFileHandle))
                throw new SoundMeterException(SoundMeterError.Disconnected, "Could not discard queued HID reports.",
                    stage: "Freshness", nativeErrorCode: Marshal.GetLastWin32Error());
        }
        public void Dispose() => stream.Dispose();
    }

    internal static class Native
    {
        [StructLayout(LayoutKind.Sequential)]
        internal struct InterfaceData
        {
            internal int Size;
            internal Guid ClassGuid;
            internal uint Flags;
            internal IntPtr Reserved;
        }
        [StructLayout(LayoutKind.Sequential)]
        internal struct Attributes
        {
            internal int Size;
            internal ushort VendorId, ProductId, Version;
        }
        // HIDP_CAPS is 64 bytes. Only these documented fixed-layout fields are needed.
        [StructLayout(LayoutKind.Explicit, Size = 64)]
        internal struct Capabilities
        {
            [FieldOffset(0)] internal ushort Usage;
            [FieldOffset(2)] internal ushort UsagePage;
            [FieldOffset(4)] internal ushort InputLength;
            [FieldOffset(6)] internal ushort OutputLength;
        }
        [DllImport("hid.dll", ExactSpelling = true)]
        internal static extern void HidD_GetHidGuid(out Guid guid);
        [DllImport("hid.dll", ExactSpelling = true, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.U1)]
        internal static extern bool HidD_GetAttributes(SafeFileHandle handle, ref Attributes attributes);
        [DllImport("hid.dll", ExactSpelling = true, CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.U1)]
        internal static extern bool HidD_GetProductString(SafeFileHandle handle, [Out] StringBuilder buffer, int byteLength);
        [DllImport("hid.dll", ExactSpelling = true, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.U1)]
        internal static extern bool HidD_GetPreparsedData(SafeFileHandle handle, out IntPtr data);
        [DllImport("hid.dll", ExactSpelling = true)]
        [return: MarshalAs(UnmanagedType.U1)]
        internal static extern bool HidD_FreePreparsedData(IntPtr data);
        [DllImport("hid.dll", ExactSpelling = true, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.U1)]
        internal static extern bool HidD_FlushQueue(SafeFileHandle handle);
        [DllImport("hid.dll", ExactSpelling = true)]
        internal static extern int HidP_GetCaps(IntPtr data, out Capabilities caps);
        [DllImport("setupapi.dll", EntryPoint = "SetupDiGetClassDevsW", ExactSpelling = true, CharSet = CharSet.Unicode, SetLastError = true)]
        internal static extern IntPtr SetupDiGetClassDevs(ref Guid guid, string? enumerator, IntPtr parent, uint flags);
        [DllImport("setupapi.dll", ExactSpelling = true, SetLastError = true)]
        internal static extern bool SetupDiEnumDeviceInterfaces(IntPtr set, IntPtr device, ref Guid guid, uint index, ref InterfaceData entry);
        [DllImport("setupapi.dll", EntryPoint = "SetupDiGetDeviceInterfaceDetailW", ExactSpelling = true, CharSet = CharSet.Unicode, SetLastError = true)]
        internal static extern bool SetupDiGetDeviceInterfaceDetail(IntPtr set, ref InterfaceData entry,
            IntPtr detail, uint size, out uint required, IntPtr device);
        [DllImport("setupapi.dll", ExactSpelling = true)]
        internal static extern bool SetupDiDestroyDeviceInfoList(IntPtr set);
        [DllImport("kernel32.dll", EntryPoint = "CreateFileW", ExactSpelling = true, CharSet = CharSet.Unicode, SetLastError = true)]
        internal static extern SafeFileHandle CreateFile(string path, uint access, uint sharing,
            IntPtr security, uint creation, uint flags, IntPtr template);
    }
}

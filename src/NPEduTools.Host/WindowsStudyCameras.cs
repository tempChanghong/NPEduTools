using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using NPEduTools.Contracts;

namespace NPEduTools.Host;

[SupportedOSPlatform("windows")]
public static class WindowsStudyCameras
{
    private static readonly object Gate = new();
    // SDK mfidl.h. The symbolic link is an opaque, stable video endpoint identifier.
    private static Guid SourceType = new("c60ac5fe-252a-478f-a0ef-bc8fa5f7cad3");
    private static Guid VideoType = new("8ac3587a-4ae7-42d8-99e0-0a6013eef90f");
    private static Guid SymbolicLink = new("58f0aad8-22bf-4f8a-bb3d-d2c4978c6e2f");
    private static Guid FriendlyName = new("60d0e559-52f8-4fa2-bbce-acdb34a8ec01");
    [DllImport("mfplat.dll", ExactSpelling = true)] private static extern int MFStartup(int version, int flags);
    [DllImport("mfplat.dll", ExactSpelling = true)] private static extern int MFShutdown();
    [DllImport("mfplat.dll", ExactSpelling = true)] private static extern int MFCreateAttributes(out IntPtr attributes, int initialSize);
    [DllImport("mf.dll", ExactSpelling = true)] private static extern int MFEnumDeviceSources(IntPtr attributes, out IntPtr devices, out uint count);
    [DllImport("ole32.dll", ExactSpelling = true)] private static extern int CoInitializeEx(IntPtr reserved, uint flags);
    [DllImport("ole32.dll", ExactSpelling = true)] private static extern void CoUninitialize();
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int SetGuid(IntPtr self, ref Guid key, ref Guid value);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int GetString(IntPtr self, ref Guid key, out IntPtr text, out uint length);
    private static T Method<T>(IntPtr self, int slot) where T : Delegate =>
        Marshal.GetDelegateForFunctionPointer<T>(Marshal.ReadIntPtr(Marshal.ReadIntPtr(self), slot * IntPtr.Size));
    private static string Read(IntPtr self, ref Guid key)
    {
        // IMFAttributes::GetAllocatedString is slot 13, including the three IUnknown slots.
        IntPtr text = IntPtr.Zero;
        try
        {
            Marshal.ThrowExceptionForHR(Method<GetString>(self, 13)(self, ref key, out text, out uint length));
            if (length > 2048 || text == IntPtr.Zero) throw new InvalidDataException("Camera attribute exceeds bounds.");
            return Marshal.PtrToStringUni(text, checked((int)length)) ?? "";
        }
        finally { if (text != IntPtr.Zero) Marshal.FreeCoTaskMem(text); }
    }
    public static IReadOnlyList<StudyCameraDevice> Enumerate()
    {
        lock (Gate)
        {
            int com = CoInitializeEx(IntPtr.Zero, 0);
            if (com < 0 && com != unchecked((int)0x80010106)) Marshal.ThrowExceptionForHR(com);
            IntPtr attributes = IntPtr.Zero, array = IntPtr.Zero; uint count = 0; bool started = false;
            try
            {
                Marshal.ThrowExceptionForHR(MFStartup(0x20070, 1)); started = true;
                Marshal.ThrowExceptionForHR(MFCreateAttributes(out attributes, 1));
                // IMFAttributes::SetGUID is slot 24. Never call IMFActivate::ActivateObject.
                Marshal.ThrowExceptionForHR(Method<SetGuid>(attributes, 24)(attributes, ref SourceType, ref VideoType));
                Marshal.ThrowExceptionForHR(MFEnumDeviceSources(attributes, out array, out count));
                if (count > 4096) throw new InvalidDataException("Camera enumeration exceeds bounds.");
                var devices = new List<StudyCameraDevice>();
                for (int i = 0; i < count && devices.Count < 128; i++)
                {
                    IntPtr device = Marshal.ReadIntPtr(array, i * IntPtr.Size);
                    if (device == IntPtr.Zero) continue;
                    devices.Add(new(Read(device, ref SymbolicLink), Read(device, ref FriendlyName)));
                }
                return devices;
            }
            finally
            {
                if (array != IntPtr.Zero)
                {
                    for (int i = 0; i < count; i++) { var p = Marshal.ReadIntPtr(array, i * IntPtr.Size); if (p != IntPtr.Zero) Marshal.Release(p); }
                    Marshal.FreeCoTaskMem(array);
                }
                if (attributes != IntPtr.Zero) Marshal.Release(attributes);
                if (started) MFShutdown();
                if (com >= 0) CoUninitialize();
            }
        }
    }
}

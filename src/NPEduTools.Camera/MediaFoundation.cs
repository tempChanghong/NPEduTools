using System.IO;
using System.Runtime.InteropServices;
namespace NPEduTools.Camera;
// Synchronous single-frame source reader. COM slots/GUIDs match Windows SDK mf*.h.
internal static class MediaFoundation
{
    private const uint Video = 0xfffffffc;
    private static Guid SourceType = new("c60ac5fe-252a-478f-a0ef-bc8fa5f7cad3"), VideoSource = new("8ac3587a-4ae7-42d8-99e0-0a6013eef90f");
    private static Guid Link = new("58f0aad8-22bf-4f8a-bb3d-d2c4978c6e2f"), Processing = new("fb394f3d-ccf1-42ee-bbb3-f9b845d5681d");
    private static Guid Major = new("48eba18e-f8c9-4687-bf11-0a74c9f96a8f"), Subtype = new("f7e34c9a-42e8-4714-b74b-cb29d72c35e5");
    private static Guid VideoMajor = new("73646976-0000-0010-8000-00aa00389b71"), Rgb32 = new("00000016-0000-0010-8000-00aa00389b71");
    private static Guid Size = new("1652c33d-d6b2-4012-b834-72030849a37d"), Stride = new("644b4e48-1e02-4516-b0eb-c01ca9d49ac6");
    [DllImport("mfplat.dll", ExactSpelling = true)] private static extern int MFStartup(int version, int flags);
    [DllImport("ole32.dll", ExactSpelling = true)] private static extern int CoInitializeEx(nint reserved, uint flags);
    [DllImport("ole32.dll", ExactSpelling = true)] private static extern void CoUninitialize();
    [DllImport("mfplat.dll", ExactSpelling = true)] private static extern int MFShutdown();
    [DllImport("mfplat.dll", ExactSpelling = true)] private static extern int MFCreateAttributes(out nint attributes, int count);
    [DllImport("mfplat.dll", ExactSpelling = true)] private static extern int MFCreateMediaType(out nint type);
    [DllImport("mf.dll", ExactSpelling = true)] private static extern int MFCreateDeviceSource(nint attributes, out nint source);
    [DllImport("mfreadwrite.dll", ExactSpelling = true)] private static extern int MFCreateSourceReaderFromMediaSource(nint source, nint attributes, out nint reader);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int SetGuid(nint self, ref Guid key, ref Guid value);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int SetString(nint self, ref Guid key, [MarshalAs(UnmanagedType.LPWStr)] string value);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int SetUInt(nint self, ref Guid key, uint value);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int GetUInt(nint self, ref Guid key, out uint value);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int GetLong(nint self, ref Guid key, out ulong value);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int GetGuid(nint self, ref Guid key, out Guid value);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int NativeType(nint self, uint stream, uint index, out nint type);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int CurrentType(nint self, uint stream, out nint type);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int SetType(nint self, uint stream, nint reserved, nint type);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int SelectStream(nint self, uint stream, [MarshalAs(UnmanagedType.Bool)] bool selected);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int Read(nint self, uint stream, uint controls, out uint actual, out uint flags, out long timestamp, out nint sample);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int GetBuffer(nint self, out nint buffer);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int Lock(nint self, out nint data, out uint maximum, out uint length);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int Simple(nint self);
    private static T Method<T>(nint self, int slot) where T : Delegate => Marshal.GetDelegateForFunctionPointer<T>(Marshal.ReadIntPtr(Marshal.ReadIntPtr(self), slot * IntPtr.Size));
    private static void Check(int hr) => Marshal.ThrowExceptionForHR(hr);
    private static void Release(ref nint p) { if (p != 0) { Marshal.Release(p); p = 0; } }
    private static (int Width, int Height) Dimensions(nint type)
    {
        Check(Method<GetLong>(type, 8)(type, ref Size, out var size));
        int w = checked((int)(size >> 32)), h = checked((int)(size & 0xffffffff));
        if (w is <= 0 or > 4096 || h is <= 0 or > 4096 || (long)w * h > 8388608) throw new InvalidDataException();
        return (w, h);
    }
    public static (byte[] Pixels, int Width, int Height) Capture(string device)
    {
        nint attributes = 0, source = 0, readerAttributes = 0, reader = 0, type = 0, best = 0, sample = 0, buffer = 0;
        bool started = false, locked = false;
        int com = CoInitializeEx(0,2); Check(com);
        try
        {
            Check(MFStartup(0x20070, 0)); started = true;
            Check(MFCreateAttributes(out attributes, 2)); Check(Method<SetGuid>(attributes, 24)(attributes, ref SourceType, ref VideoSource));
            Check(Method<SetString>(attributes, 25)(attributes, ref Link, device)); Check(MFCreateDeviceSource(attributes, out source));
            Check(MFCreateAttributes(out readerAttributes, 1)); Check(Method<SetUInt>(readerAttributes, 21)(readerAttributes, ref Processing, 1));
            Check(MFCreateSourceReaderFromMediaSource(source, readerAttributes, out reader));
            Check(Method<SelectStream>(reader, 4)(reader, 0xfffffffe, false)); Check(Method<SelectStream>(reader, 4)(reader, Video, true));
            long bestScore = long.MaxValue;
            for (uint i = 0; i < 128; i++)
            {
                int hr = Method<NativeType>(reader, 5)(reader, Video, i, out type);
                if (hr == unchecked((int)0xc00d36b9)) break; // MF_E_NO_MORE_TYPES
                Check(hr);
                try
                {
                    var (w, h) = Dimensions(type);
                    long area = (long)w * h, score = Math.Max(w, h) <= 1280 ? 16777216 - area : 16777216 + area;
                    if (score < bestScore) { Release(ref best); best = type; type = 0; bestScore = score; }
                }
                catch (InvalidDataException) { }
                finally { Release(ref type); }
            }
            if (best == 0) throw new InvalidDataException();
            Check(Method<SetType>(reader, 7)(reader, Video, 0, best));
            Check(MFCreateMediaType(out type)); Check(Method<SetGuid>(type, 24)(type, ref Major, ref VideoMajor));
            Check(Method<SetGuid>(type, 24)(type, ref Subtype, ref Rgb32)); Check(Method<SetType>(reader, 7)(reader, Video, 0, type)); Release(ref type);
            for (int i = 0; i < 16; i++)
            {
                Check(Method<Read>(reader, 9)(reader, Video, 0, out _, out var flags, out _, out sample));
                if ((flags & 3) != 0) throw new InvalidDataException(); // reader error / end of stream
                if (sample != 0) break;
            }
            if (sample == 0) throw new InvalidDataException();
            Check(Method<CurrentType>(reader, 6)(reader, Video, out type));
            Check(Method<GetGuid>(type, 10)(type, ref Subtype, out var subtype)); if (subtype != Rgb32) throw new InvalidDataException();
            var (width, height) = Dimensions(type);
            int stride = width * 4; if (Method<GetUInt>(type, 7)(type, ref Stride, out var rawStride) >= 0) stride = unchecked((int)rawStride);
            if (Math.Abs((long)stride) < width * 4L || Math.Abs((long)stride) * height > 67108864) throw new InvalidDataException();
            Check(Method<GetBuffer>(sample, 41)(sample, out buffer));
            Check(Method<Lock>(buffer, 3)(buffer, out var pixels, out var maximum, out var length)); locked = true;
            if (pixels == 0 || length > maximum || Math.Abs((long)stride) * height > length) throw new InvalidDataException();
            var result = new byte[checked(width * height * 4)];
            for (int row = 0; row < height; row++)
            {
                long offset = (long)(stride < 0 ? height - 1 - row : row) * Math.Abs((long)stride);
                Marshal.Copy(pixels + checked((int)offset), result, row * width * 4, width * 4);
            }
            return (result, width, height);
        }
        finally
        {
            if (locked) Method<Simple>(buffer, 4)(buffer);
            Release(ref buffer); Release(ref sample); Release(ref type); Release(ref best); Release(ref reader); Release(ref readerAttributes);
            if (source != 0) Method<Simple>(source, 12)(source);
            Release(ref source); Release(ref attributes); if (started) MFShutdown(); CoUninitialize();
        }
    }
}

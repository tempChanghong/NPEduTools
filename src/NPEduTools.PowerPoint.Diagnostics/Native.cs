using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;

namespace NPEduTools.PowerPoint.Diagnostics;

[SupportedOSPlatform("windows")]
internal static class Native
{
    [StructLayout(LayoutKind.Sequential)] internal struct Point { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] internal struct Rect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] internal struct MouseData
    { public Point Point; public uint Data, Flags, Time; public nuint ExtraInfo; }
    [StructLayout(LayoutKind.Sequential)] internal struct Message
    { public nint Hwnd; public uint Id; public nuint WParam; public nint LParam; public uint Time; public Point Point; public uint Private; }
    internal delegate nint HookCallback(int code, nuint message, nint data);
    internal delegate bool WindowCallback(nint hwnd, nint parameter);

    [DllImport("user32.dll")] internal static extern bool SetProcessDpiAwarenessContext(nint context);
    [DllImport("user32.dll")] internal static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] internal static extern nint WindowFromPoint(Point point);
    [DllImport("user32.dll")] internal static extern nint GetAncestor(nint hwnd, uint flags);
    [DllImport("user32.dll")] internal static extern bool GetClientRect(nint hwnd, out Rect rect);
    [DllImport("user32.dll")] internal static extern bool ClientToScreen(nint hwnd, ref Point point);
    [DllImport("user32.dll")] internal static extern uint GetDpiForWindow(nint hwnd);
    [DllImport("user32.dll")] internal static extern uint GetWindowThreadProcessId(nint hwnd, out uint pid);
    [DllImport("user32.dll")] internal static extern bool EnumWindows(WindowCallback callback, nint parameter);
    [DllImport("user32.dll")] internal static extern bool IsWindowVisible(nint hwnd);
    [DllImport("user32.dll", SetLastError = true)] internal static extern bool PostMessageW(nint hwnd, uint message, nuint wParam, nint lParam);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern int GetClassNameW(nint hwnd, StringBuilder name, int maximum);
    [DllImport("user32.dll")] internal static extern int GetSystemMetrics(int index);
    [DllImport("user32.dll", SetLastError = true)] internal static extern nint SetWindowsHookExW(int id, HookCallback callback, nint module, uint thread);
    [DllImport("user32.dll")] internal static extern bool UnhookWindowsHookEx(nint hook);
    [DllImport("user32.dll")] internal static extern nint CallNextHookEx(nint hook, int code, nuint message, nint data);
    [DllImport("user32.dll")] internal static extern bool PeekMessageW(out Message message, nint hwnd, uint min, uint max, uint remove);
    [DllImport("user32.dll")] internal static extern bool TranslateMessage(ref Message message);
    [DllImport("user32.dll")] internal static extern nint DispatchMessageW(ref Message message);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] internal static extern nint GetModuleHandleW(string? module);
    [DllImport("oleaut32.dll", PreserveSig = false)] internal static extern void GetActiveObject(ref Guid clsid, nint reserved, [MarshalAs(UnmanagedType.IUnknown)] out object value);

    internal static void Pump()
    {
        while (PeekMessageW(out var message, 0, 0, 0, 1))
        { TranslateMessage(ref message); DispatchMessageW(ref message); }
    }
}

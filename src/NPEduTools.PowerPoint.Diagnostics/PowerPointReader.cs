using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;

namespace NPEduTools.PowerPoint.Diagnostics;

[SupportedOSPlatform("windows")]
internal sealed class PowerPointReader
{
    private readonly string _session = Guid.NewGuid().ToString("N");

    public ShowSnapshot Read()
    {
        string stage = "ProcessDiscovery";
        var objects = new Stack<object>();
        object Keep(object value) { objects.Push(value); return value; }
        try
        {
            using var self = Process.GetCurrentProcess();
            int processCount = 0;
            int powerpointPid = 0;
            string? officeBuild = null;
            foreach (var process in Process.GetProcessesByName("POWERPNT"))
            {
                using (process)
                {
                    if (process.SessionId != self.SessionId) continue;
                    processCount++; powerpointPid = process.Id;
                    try { officeBuild = process.MainModule?.FileVersionInfo.FileVersion; }
                    catch (System.ComponentModel.Win32Exception) { }
                }
            }
            if (processCount == 0) return new(DateTimeOffset.UtcNow, "NotRunning");
            if (processCount > 1) return new(DateTimeOffset.UtcNow, "MultipleInstancesUnsupported");
            var clsid = new Guid("91493441-5A91-11CF-8700-00AA0060263B");
            stage = "GetActiveObject";
            Native.GetActiveObject(ref clsid, 0, out var active);
            dynamic app = Keep(active);
            stage = "ApplicationVersion";
            string version = (string)app.Version;
            stage = "SlideShowWindows";
            dynamic windows = Keep((object)app.SlideShowWindows);
            stage = "WindowCount";
            int count = (int)windows.Count;
            if (count > 1) return new(DateTimeOffset.UtcNow, "MultipleShowsUnsupported", version);
            // This Office build does not expose SlideShowWindow.HWND via IDispatch. Map only
            // the unambiguous single-show case; never guess from translated window titles.
            var handles = new List<nint>();
            if (count == 1)
            {
                Native.EnumWindows((candidate, _) =>
                {
                    Native.GetWindowThreadProcessId(candidate, out uint owner);
                    if (owner == powerpointPid && Native.IsWindowVisible(candidate))
                    {
                        var name = new StringBuilder(128);
                        Native.GetClassNameW(candidate, name, name.Capacity);
                        if (name.ToString().Equals("screenClass", StringComparison.Ordinal)) handles.Add(candidate);
                    }
                    return true;
                }, 0);
                if (handles.Count != 1) return new(DateTimeOffset.UtcNow, "WindowMappingUnavailable", version);
            }
            var targets = new List<ShowTarget>();
            for (int i = 1; i <= count; i++)
            {
                stage = "WindowItem";
                dynamic window = Keep((object)windows.Item(i));
                stage = "WindowHandle";
                nint hwnd = handles[0];
                Native.GetWindowThreadProcessId(hwnd, out uint pid);
                using var process = Process.GetProcessById(checked((int)pid));
                if (!process.ProcessName.Equals("POWERPNT", StringComparison.OrdinalIgnoreCase) || process.SessionId != self.SessionId)
                    return new(DateTimeOffset.UtcNow, "WindowIdentityMismatch");
                if (!Native.GetClientRect(hwnd, out var rect)) return new(DateTimeOffset.UtcNow, "WindowUnavailable");
                var origin = new Native.Point();
                if (!Native.ClientToScreen(hwnd, ref origin)) return new(DateTimeOffset.UtcNow, "WindowUnavailable");
                stage = "View";
                dynamic view = Keep((object)window.View);
                stage = "Slide";
                dynamic slide = Keep((object)view.Slide);
                stage = "Presentation";
                dynamic presentation = Keep((object)window.Presentation);
                // Salt per worker: correlate a document within a run without exporting its name or path.
                string name = (string)presentation.FullName;
                string id = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(_session + name + hwnd)))[..24];
                stage = "SlideState";
                targets.Add(new(id, hwnd.ToInt64(), process.Id, process.StartTime.ToUniversalTime().Ticks,
                    origin.X, origin.Y, origin.X + rect.Right, origin.Y + rect.Bottom, Native.GetDpiForWindow(hwnd),
                    (int)window.IsFullScreen != 0, (int)slide.SlideID, (int)slide.SlideIndex,
                    (int)view.GetClickIndex(), (int)view.GetClickCount(), (int)view.PointerType, (int)view.State));
            }
            return new(DateTimeOffset.UtcNow, count == 0 ? "NoSlideShow" : "Showing", version, targets.ToArray(), OfficeBuild: officeBuild);
        }
        catch (COMException error)
        {
            string status = error.HResult == unchecked((int)0x800401e3) ? "RunningButNotRegistered" : "ComUnavailable";
            return new(DateTimeOffset.UtcNow, status, Error: $"{stage}:0x{error.HResult:X8}");
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        { return new(DateTimeOffset.UtcNow, "ProbeFailed", Error: $"{stage}:{error.GetType().Name}"); }
        finally
        {
            while (objects.TryPop(out var value))
                if (Marshal.IsComObject(value)) Marshal.ReleaseComObject(value);
        }
    }
}

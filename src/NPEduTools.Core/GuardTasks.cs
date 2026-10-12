using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Xml.Linq;
using NPEduTools.Contracts;

namespace NPEduTools.Core;

/// <summary>The scheduler, rather than the monitored process, owns each launch.
/// Task names and ownership metadata remain explicitly NPEduTools maintenance records.</summary>
[SupportedOSPlatform("windows")]
public sealed class GuardTasks(string bundle, string pipe, string? upstream)
{
    private static readonly XNamespace Ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";
    private string Root => Path.TrimEndingDirectorySeparator(Path.GetFullPath(bundle));
    private string Sid
    {
        get { using var identity = WindowsIdentity.GetCurrent(); return identity.User?.Value ?? throw new InvalidOperationException("无法核实当前 Windows 用户。"); }
    }
    private bool DefaultPipe => pipe == PipeEndpoint.DefaultName;
    public string Name(string role) => "NPEduTools.Guard." + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Sid + "|" + (DefaultPipe ? "default" : pipe))))[..24] + "." + role;
    public string Executable(string role) => role switch
    {
        "Guard" or "Login" => Path.Combine(Root, "Guard", "svchost.exe"),
        "App" => Path.Combine(Root, "NPEduTools.App.exe"),
        "Host" => Path.Combine(Root, "Host", "NPEduTools.Host.exe"),
        _ => throw new ArgumentException("Unknown guardian role.", nameof(role))
    };
    private string Arguments(string role)
    {
        static string Quote(string value)
        {
            if (value.Length is 0 or > 200 || value.IndexOfAny(['/', '\\', ':', '"', '\r', '\n']) >= 0 || value.StartsWith("--", StringComparison.Ordinal))
                throw new ArgumentException("INVALID_GUARD_ENDPOINT");
            return "\"" + value + "\"";
        }
        string value = DefaultPipe ? role is "Guard" or "Login" ? "--default-pipe" : "" : "--pipe " + Quote(pipe);
        if (role is "App" or "Host" && upstream is not null) value += " --classisland-pipe " + Quote(upstream);
        if (role == "App") value += " --guard-recovery";
        if (role == "Login") value += " --at-login";
        return value.Trim();
    }
    public string Definition(string role, bool highest = true)
    {
        XElement E(string n, object? v) => new(Ns + n, v);
        var registration = E("RegistrationInfo", new object[] { E("Author", "NPEduTools"), E("Description", "NPEduTools 自习守护；由学校维护人员管理。"), E("Source", "NPEduTools.Guard.v2") });
        var triggers = E("Triggers", role switch
        {
            "Login" => E("LogonTrigger", new object[] { E("Enabled", true), E("UserId", Sid) }),
            "Guard" => E("TimeTrigger", new object[] {
                E("Repetition", new object[] { E("Interval", "PT1M"), E("StopAtDurationEnd", false) }),
                E("StartBoundary", DateTimeOffset.UtcNow.AddMinutes(1).ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", System.Globalization.CultureInfo.InvariantCulture)), E("Enabled", true) }),
            _ => null
        });
        var principal = E("Principals", E("Principal", new object[] { new XAttribute("id", "User"), E("UserId", Sid), E("LogonType", "InteractiveToken"), E("RunLevel", highest ? "HighestAvailable" : "LeastPrivilege") }));
        var settings = E("Settings", new object[] { E("MultipleInstancesPolicy", "IgnoreNew"), E("DisallowStartIfOnBatteries", false), E("StopIfGoingOnBatteries", false),
            E("AllowHardTerminate", true), E("StartWhenAvailable", false), E("AllowStartOnDemand", true), E("Enabled", true), E("Hidden", false), E("ExecutionTimeLimit", "PT0S"), E("Priority", 7),
            role is "Guard" or "Login" ? E("RestartOnFailure", new object[] { E("Interval", "PT1M"), E("Count", 999) }) : null! });
        var action = E("Actions", new object[] { new XAttribute("Context", "User"), E("Exec", new object[] { E("Command", Executable(role)), E("Arguments", Arguments(role)), E("WorkingDirectory", Path.GetDirectoryName(Executable(role))) }) });
        return new XDocument(E("Task", new object[] { new XAttribute("version", "1.2"), registration, triggers, principal, settings, action })).ToString();
    }
    public bool OwnedDefinition(string xml, string role)
    {
        try
        {
            var t = XDocument.Parse(xml).Root;
            var a = t?.Element(Ns + "Actions"); var p = t?.Element(Ns + "Principals")?.Element(Ns + "Principal");
            var exec = a?.Element(Ns + "Exec");
            return t?.Element(Ns + "RegistrationInfo")?.Element(Ns + "Source")?.Value == "NPEduTools.Guard.v2" &&
                a?.Elements().Count() == 1 && exec is not null && p?.Element(Ns + "UserId")?.Value == Sid &&
                p.Element(Ns + "LogonType")?.Value == "InteractiveToken" &&
                string.Equals(exec.Element(Ns + "Command")?.Value, Executable(role), StringComparison.OrdinalIgnoreCase) &&
                (exec.Element(Ns + "Arguments")?.Value ?? "") == Arguments(role) &&
                string.Equals(exec.Element(Ns + "WorkingDirectory")?.Value, Path.GetDirectoryName(Executable(role)), StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception e) when (e is System.Xml.XmlException or ArgumentException) { return false; }
    }
    private static dynamic Connect()
    {
        var service = Activator.CreateInstance(Type.GetTypeFromProgID("Schedule.Service") ?? throw new InvalidOperationException("Windows 任务计划程序不可用。"))!;
        try { ((dynamic)service).Connect(); return service; }
        catch { Release(service); throw; }
    }
    private static void Release(object? value) { if (value is not null && Marshal.IsComObject(value)) Marshal.FinalReleaseComObject(value); }
    private static dynamic? Find(dynamic folder, string name)
    {
        try { return folder.GetTask(name); }
        catch (Exception e) when (e is COMException or FileNotFoundException && e.HResult == unchecked((int)0x80070002)) { return null; }
    }
    public void Register(bool highest = true)
    {
        if (File.Exists(Path.Combine(Root, "guard-maintenance.json"))) throw new IOException("安装器正在维护此目录，守护暂不登记；请等待维护完成。");
        dynamic service = Connect(); dynamic? folder = null;
        try
        {
            folder = service.GetFolder("\\");
            // Inspect all collisions before writing anything. Another bundle is not silently taken over.
            foreach (string role in new[] { "Guard", "Login", "App", "Host" })
            {
                if (!File.Exists(Executable(role))) throw new FileNotFoundException("守护组件不完整。", Executable(role));
                dynamic? existing = Find(folder, Name(role));
                try { if (existing is not null && !OwnedDefinition((string)existing.Xml, role)) throw new IOException("守护启动登记属于另一目录或已被更改；请联系维护人员。" ); }
                finally { Release(existing); }
            }
            foreach (string role in new[] { "Guard", "Login", "App", "Host" })
            {
                dynamic registered = folder.RegisterTask(Name(role), Definition(role, highest), 6, Sid, null, 3, null);
                Release(registered);
            }
        }
        finally { Release(folder); Release(service); }
    }
    public void Run(string role)
    {
        if (File.Exists(Path.Combine(Root, "guard-maintenance.json"))) throw new IOException("安装器正在维护此目录，暂不启动组件。");
        dynamic service = Connect(); dynamic? folder = null, task = null, running = null;
        try
        {
            folder = service.GetFolder("\\"); task = Find(folder, Name(role));
            if (task is null || !OwnedDefinition((string)task.Xml, role)) throw new IOException("独立启动登记缺失或不匹配；请在当前目录重新启动 NPEduTools。" );
            using var current = Process.GetCurrentProcess();
            running = task.RunEx(null, 4, current.SessionId, Sid);
        }
        finally { Release(running); Release(task); Release(folder); Release(service); }
    }
    // Maintenance and isolated tests only; there is deliberately no client UI for this.
    public void Unregister()
    {
        dynamic service = Connect(); dynamic? folder = null;
        try
        {
            folder = service.GetFolder("\\");
            foreach (string role in new[] { "Guard", "Login", "App", "Host" })
            {
                dynamic? task = Find(folder, Name(role));
                try
                {
                    if (task is null) continue;
                    if (!OwnedDefinition((string)task.Xml, role)) throw new IOException("维护登记不匹配，不删除其他程序的任务。" );
                    folder.DeleteTask(Name(role), 0);
                }
                finally { Release(task); }
            }
        }
        finally { Release(folder); Release(service); }
    }
    public bool GuardRunning()
    {
        if (!Mutex.TryOpenExisting($@"Local\{pipe}.Guard", out var mutex)) return false;
        mutex.Dispose();
        var status = GuardFiles.ForPipe(pipe).Read<GuardStatus>("status.json");
        if (status is null) return false;
        try
        {
            using var p = Process.GetProcessById(status.GuardPid); using var current = Process.GetCurrentProcess();
            return !p.HasExited && p.SessionId == current.SessionId && string.Equals(p.MainModule?.FileName, Executable("Guard"), StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception) { return false; }
    }
    public static bool StartupError(Exception e) => e is IOException or UnauthorizedAccessException or COMException or System.ComponentModel.Win32Exception or InvalidOperationException or ArgumentException;
}

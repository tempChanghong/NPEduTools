using System.Security.Cryptography;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace NPEduTools.ClassIsland.Admin;

public static class TaskDefinitionPolicy
{
    public const string TaskName = "ClassIsland.AdminStartup";
    public static readonly XNamespace Ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";
    public static string Fingerprint(string? xml) => xml is null ? "missing" : Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(xml)));

    // Accept only the plugin's single interactive logon / executable task for this user and installation.
    // A familiar name alone is insufficient authority to overwrite or remove a task.
    public static bool IsCompatible(string xml, string executable, string sid, Func<string, string?> resolveSid)
    {
        try
        {
            using var reader = XmlReader.Create(new StringReader(xml), new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, MaxCharactersInDocument = 65536 });
            var root = XDocument.Load(reader).Root;
            if (root?.Name != Ns + "Task") return false;
            var principals = root.Element(Ns + "Principals")?.Elements().ToArray();
            var triggers = root.Element(Ns + "Triggers")?.Elements().ToArray();
            var actions = root.Element(Ns + "Actions")?.Elements().ToArray();
            if (principals is not { Length: 1 } || triggers is not { Length: 1 } || actions is not { Length: 1 }) return false;
            var principal = principals[0]; var trigger = triggers[0]; var action = actions[0];
            return principal.Name == Ns + "Principal" && principal.Element(Ns + "GroupId") is null &&
                resolveSid((string?)principal.Element(Ns + "UserId") ?? "") == sid &&
                (string?)principal.Element(Ns + "LogonType") == "InteractiveToken" &&
                (string?)principal.Element(Ns + "RunLevel") == "HighestAvailable" &&
                trigger.Name == Ns + "LogonTrigger" && resolveSid((string?)trigger.Element(Ns + "UserId") ?? "") == sid &&
                action.Name == Ns + "Exec" && SamePath((string?)action.Element(Ns + "Command"), executable) &&
                string.IsNullOrWhiteSpace((string?)action.Element(Ns + "Arguments")) &&
                SamePath((string?)action.Element(Ns + "WorkingDirectory"), Path.GetDirectoryName(executable)!);
        }
        catch (Exception ex) when (ex is XmlException or ArgumentException or NotSupportedException or IOException) { return false; }
    }

    private static bool SamePath(string? a, string b) => !string.IsNullOrWhiteSpace(a) && Path.IsPathFullyQualified(a) &&
        string.Equals(Path.GetFullPath(a).TrimEnd('\\'), Path.GetFullPath(b).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);

    public static string Create(string executable, string sid) => new XDocument(new XElement(Ns + "Task", new XAttribute("version", "1.2"),
        new XElement(Ns + "RegistrationInfo", new XElement(Ns + "Description", "ClassIsland 管理员自启动（兼容 StartUpAsAdmin）")),
        new XElement(Ns + "Triggers", new XElement(Ns + "LogonTrigger", new XElement(Ns + "Enabled", true), new XElement(Ns + "UserId", sid))),
        new XElement(Ns + "Principals", new XElement(Ns + "Principal", new XAttribute("id", "Author"),
            new XElement(Ns + "UserId", sid), new XElement(Ns + "LogonType", "InteractiveToken"), new XElement(Ns + "RunLevel", "HighestAvailable"))),
        new XElement(Ns + "Settings", new XElement(Ns + "MultipleInstancesPolicy", "IgnoreNew"),
            new XElement(Ns + "DisallowStartIfOnBatteries", false), new XElement(Ns + "StopIfGoingOnBatteries", false),
            new XElement(Ns + "AllowHardTerminate", false), new XElement(Ns + "StartWhenAvailable", false),
            new XElement(Ns + "RunOnlyIfNetworkAvailable", false), new XElement(Ns + "AllowStartOnDemand", true),
            new XElement(Ns + "Enabled", true), new XElement(Ns + "Hidden", false), new XElement(Ns + "RunOnlyIfIdle", false),
            new XElement(Ns + "WakeToRun", false), new XElement(Ns + "ExecutionTimeLimit", "PT0S"), new XElement(Ns + "Priority", 7)),
        new XElement(Ns + "Actions", new XAttribute("Context", "Author"), new XElement(Ns + "Exec",
            new XElement(Ns + "Command", executable), new XElement(Ns + "WorkingDirectory", Path.GetDirectoryName(executable)))))).ToString();
}

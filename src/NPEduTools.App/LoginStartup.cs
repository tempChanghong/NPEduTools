using System.IO;
using System.Runtime.Versioning;
using Microsoft.Win32;

namespace NPEduTools.App;

// The caller owns the key. Tests use an isolated, non-startup key under HKCU.
[SupportedOSPlatform("windows")]
public sealed class LoginStartup(RegistryKey key, string executable)
{
    public const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    public const string ValueName = "NPEduTools";
    public string Command { get; } = BuildCommand(executable);

    public static string BuildCommand(string executable)
    {
        if (!Path.IsPathFullyQualified(executable) || executable.IndexOfAny(['"', '\r', '\n']) >= 0 ||
            !string.Equals(Path.GetFileName(executable), "NPEduTools.App.exe", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("无法确定 NPEduTools 程序位置。");
        string command = $"\"{executable}\" --startup";
        if (command.Length > 260) throw new InvalidDataException("程序路径过长，请将应用移到较短的目录后再设置登录启动。");
        return command;
    }

    public string ReadState()
    {
        object? value = key.GetValue(ValueName, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
        if (value is null) return "Missing";
        return value is string command && key.GetValueKind(ValueName) == RegistryValueKind.String &&
            string.Equals(command, Command, StringComparison.OrdinalIgnoreCase) ? "Registered" : "Conflict";
    }

    public void SetEnabled(bool enabled)
    {
        if (ReadState() == "Conflict") throw new InvalidOperationException("同名启动项指向其他位置，请先在原位置关闭自启动。");
        if (enabled) key.SetValue(ValueName, Command, RegistryValueKind.String);
        else key.DeleteValue(ValueName, false);
        if (ReadState() != (enabled ? "Registered" : "Missing")) throw new IOException("未能确认登录启动项已更新。");
    }
}

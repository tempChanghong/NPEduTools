using System.Diagnostics;

namespace NPEduTools.Core;

public static class ClassIslandExecutable
{
    public static string Validate(string path)
    {
        try { return ValidateCore(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or System.ComponentModel.Win32Exception)
        {
            throw new LaunchTargetException("ExecutableUnreadable", "无法读取所选程序，请检查文件及访问权限。");
        }
    }

    private static string ValidateCore(string path)
    {
        if (!Path.IsPathFullyQualified(path) || path.StartsWith(@"\\", StringComparison.Ordinal) ||
            path.Length > 2048 || path.IndexOfAny(['"', '\r', '\n']) >= 0)
            throw new LaunchTargetException("InvalidExecutablePath", "请选择本机 ClassIsland 可执行文件的完整路径。");
        string full = Path.GetFullPath(path);
        string name = Path.GetFileName(full);
        if (!name.Equals("ClassIsland.exe", StringComparison.OrdinalIgnoreCase) &&
            !name.Equals("ClassIsland.Desktop.exe", StringComparison.OrdinalIgnoreCase))
            throw new LaunchTargetException("NotClassIslandExecutable", "请选择 ClassIsland.exe 或 ClassIsland.Desktop.exe。");
        if (!File.Exists(full)) throw new LaunchTargetException("ExecutableNotFound", "文件不存在，请重新选择 ClassIsland 程序。");
        using var file = File.OpenRead(full);
        if (file.ReadByte() != 'M' || file.ReadByte() != 'Z' ||
            FileVersionInfo.GetVersionInfo(full).ProductName != "ClassIsland")
            throw new LaunchTargetException("NotClassIslandExecutable", "所选文件不是可识别的 ClassIsland 程序。");
        return full;
    }

}

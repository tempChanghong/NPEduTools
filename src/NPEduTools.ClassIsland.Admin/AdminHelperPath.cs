namespace NPEduTools.ClassIsland.Admin;

/// <summary>Fixed component locations in a desktop bundle; never searches PATH or arbitrary ancestors.</summary>
public static class AdminHelperPath
{
    public static string FromAppDirectory(string appDirectory) =>
        Path.Combine(Path.GetFullPath(appDirectory), "Admin", "NPEduTools.ClassIsland.Admin.exe");

    public static string FromHostDirectory(string hostDirectory)
    {
        string directory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(hostDirectory));
        string? parent = Path.GetDirectoryName(directory);
        // Published and build-copied bundles put Host beside Admin, not above it.
        // Require both package markers so a random directory named Host does not
        // redirect a standalone development host into an unrelated parent folder.
        bool bundled = parent is not null && Path.GetFileName(directory).Equals("Host", StringComparison.OrdinalIgnoreCase)
            && File.Exists(Path.Combine(directory, "NPEduTools.Host.exe"))
            && File.Exists(Path.Combine(parent, "NPEduTools.App.exe"));
        return FromAppDirectory(bundled ? parent! : directory);
    }
}

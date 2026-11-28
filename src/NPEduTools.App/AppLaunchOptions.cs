namespace NPEduTools.App;

public sealed record AppLaunchOptions(string Pipe, string? Upstream, bool AtLogin, bool GuardRecovery = false,
    bool DebugExternalHost = false)
{
    public static AppLaunchOptions Parse(string[] args, string defaultPipe)
    {
        string pipe = defaultPipe;
        string? upstream = null;
        bool login = false, recovery = false;
        bool debugExternalHost = false;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (int i = 0; i < args.Length; i++)
        {
            string argument = args[i];
            if (!seen.Add(argument)) throw new ArgumentException("重复的启动参数。");
            if (argument == "--startup") { login = true; continue; }
            if (argument == "--guard-recovery") { login = true; recovery = true; continue; }
#if DEBUG
            if (argument == "--debug-external-host") { debugExternalHost = true; continue; }
#endif
            if (argument is not ("--pipe" or "--classisland-pipe") || ++i >= args.Length ||
                args[i].Length is 0 or > 200 || args[i].IndexOfAny(['/', '\\', ':']) >= 0 || args[i].StartsWith("--", StringComparison.Ordinal))
                throw new ArgumentException("启动参数无效。支持 --startup、--pipe NAME 和 --classisland-pipe NAME。");
            if (argument == "--pipe") pipe = args[i]; else upstream = args[i];
        }
        if (debugExternalHost && (login || !seen.Contains("--pipe") || !IsDebugPipe(pipe) || !IsDebugPipe(upstream) ||
            string.Equals(pipe, upstream, StringComparison.OrdinalIgnoreCase)))
            throw new ArgumentException("外部后台调试需要显式指定两个不同的 NPEduTools.Debug.* 管道，且不能从登录启动或守护恢复。");

        return new(pipe, upstream, login, recovery, debugExternalHost);
    }

    private static bool IsDebugPipe(string? pipe) => pipe is not null &&
        pipe.Length > "NPEduTools.Debug.".Length && pipe.StartsWith("NPEduTools.Debug.", StringComparison.Ordinal);
}

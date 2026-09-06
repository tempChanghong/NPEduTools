namespace NPEduTools.App;

public sealed record AppLaunchOptions(string Pipe, string? Upstream, bool AtLogin)
{
    public static AppLaunchOptions Parse(string[] args, string defaultPipe)
    {
        string pipe = defaultPipe;
        string? upstream = null;
        bool login = false;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (int i = 0; i < args.Length; i++)
        {
            string argument = args[i];
            if (!seen.Add(argument)) throw new ArgumentException("重复的启动参数。");
            if (argument == "--startup") { login = true; continue; }
            if (argument is not ("--pipe" or "--classisland-pipe") || ++i >= args.Length ||
                args[i].Length is 0 or > 200 || args[i].IndexOfAny(['/', '\\', ':']) >= 0 || args[i].StartsWith("--", StringComparison.Ordinal))
                throw new ArgumentException("启动参数无效。支持 --startup、--pipe NAME 和 --classisland-pipe NAME。");
            if (argument == "--pipe") pipe = args[i]; else upstream = args[i];
        }
        return new(pipe, upstream, login);
    }
}

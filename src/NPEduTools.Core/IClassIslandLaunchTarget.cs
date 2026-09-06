namespace NPEduTools.Core;

public sealed class LaunchTargetException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

public interface IClassIslandLaunchTarget
{
    string ValidateExecutable(string path);
    bool IsRunning(string executablePath);
    int Start(string executablePath);
}

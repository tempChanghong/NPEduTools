using System.Diagnostics;
using System.Reflection;
using NPEduTools.Core;

namespace NPEduTools.Host;

internal static class HostCrashReport
{
    internal static void Register(string dataDirectory)
    {
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            if (!args.IsTerminating || args.ExceptionObject is not Exception error) return;
            try
            {
                // Exception messages can contain site addresses, file paths or credentials.
                var causes = new List<object>();
                for (Exception? cause = error; cause is not null && causes.Count < 8; cause = cause.InnerException)
                {
                    var frames = new StackTrace(cause, false).GetFrames().Take(48).Select(frame =>
                    {
                        var method = frame.GetMethod();
                        return new { Type = method?.DeclaringType?.FullName, Method = method?.Name, IlOffset = frame.GetILOffset() };
                    }).ToArray();
                    causes.Add(new { ExceptionType = cause.GetType().FullName, cause.HResult, Frames = frames });
                }
                var assembly = typeof(HostCrashReport).Assembly;
                var files = new GuardFiles(Path.Combine(dataDirectory, "diagnostics"));
                files.Write("last-host-crash.json", new
                {
                    Utc = DateTimeOffset.UtcNow, Environment.ProcessId,
                    Version = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion,
                    Causes = causes
                });
            }
            catch (Exception)
            {
                // A best-effort final write must not replace the original fatal exception.
            }
        };
    }
}

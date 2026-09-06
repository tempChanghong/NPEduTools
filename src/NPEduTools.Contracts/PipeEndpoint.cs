using System.Diagnostics;
using System.Runtime.Versioning;
using System.Security.Principal;

namespace NPEduTools.Contracts;

public static class PipeEndpoint
{
    [SupportedOSPlatform("windows")]
    public static string DefaultName
    {
        get
        {
            using var identity = WindowsIdentity.GetCurrent();
            using var process = Process.GetCurrentProcess();
            return $"NPEduTools.v1.{identity.User!.Value}.{process.SessionId}";
        }
    }
}

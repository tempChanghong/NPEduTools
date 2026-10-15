using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Xml.Linq;
using NPEduTools.Core;

namespace NPEduTools.Tests;

[SupportedOSPlatform("windows")]
public sealed class GuardMaintenanceProcessTests
{
    [Theory]
    [InlineData("missing")]
    [InlineData("damaged")]
    [InlineData("empty-generation")]
    [InlineData("foreign-bundle")]
    [InlineData("invalid-arguments")]
    public async Task Maintenance_cannot_report_success_without_a_verified_instance(string condition)
    {
        string pipe = "NPEduTools.Test.MaintenanceResult." + Guid.NewGuid().ToString("N");
        var files = GuardFiles.ForPipe(pipe);
        try
        {
            if (condition == "damaged")
            {
                Directory.CreateDirectory(StateDirectory(pipe));
                File.WriteAllText(Path.Combine(StateDirectory(pipe), "registration.json"), "{broken");
            }
            else if (condition != "missing")
                files.Write("registration.json", new GuardRegistration(
                    condition == "empty-generation" ? Guid.Empty : Guid.NewGuid(), GuardProcess.Current(), null,
                    condition == "foreign-bundle" ? Path.Combine(Bundle, "another-installation") : Bundle));
            string[] arguments = condition == "invalid-arguments" ? ["--pipe", pipe, "--maintenance", "--extra"] : ["--pipe", pipe, "--maintenance"];
            Assert.Equal(1, await Execute(arguments));
            Assert.False(files.Exists("stop.json"));
        }
        finally { DeleteState(pipe); }
    }

    [Fact]
    public async Task Successful_maintenance_persists_the_scoped_stop_and_returns_zero()
    {
        string pipe = "NPEduTools.Test.MaintenanceResult." + Guid.NewGuid().ToString("N");
        var files = GuardFiles.ForPipe(pipe);
        var registration = new GuardRegistration(Guid.NewGuid(), GuardProcess.Current(), null, Bundle);
        try
        {
            files.Write("registration.json", registration);
            Assert.Equal(0, await Execute(["--pipe", pipe, "--maintenance"]));
            Assert.Equal("Maintenance", files.StopReason(registration.Generation));
            Assert.Equal(registration, files.Read<GuardRegistration>("registration.json"));
        }
        finally { DeleteState(pipe); }
    }

    [Fact]
    public async Task Task_ownership_failure_returns_a_controlled_error_and_preserves_the_foreign_task()
    {
        string pipe = "NPEduTools.Test.MaintenanceResult." + Guid.NewGuid().ToString("N");
        var files = GuardFiles.ForPipe(pipe);
        var registration = new GuardRegistration(Guid.NewGuid(), GuardProcess.Current(), null, Bundle);
        var tasks = new GuardTasks(Bundle, pipe, null);
        dynamic? service = null, folder = null, task = null;
        bool registered = false;
        try
        {
            files.Write("registration.json", registration);
            var definition = XDocument.Parse(tasks.Definition("Host", false));
            var ns = definition.Root!.Name.Namespace;
            definition.Descendants(ns + "Source").Single().Value = "Other.Product";
            definition.Descendants(ns + "Enabled").Single().Value = "false";
            service = Activator.CreateInstance(Type.GetTypeFromProgID("Schedule.Service")!)!;
            service.Connect(); folder = service.GetFolder("\\");
            task = folder.RegisterTask(tasks.Name("Host"), definition.ToString(), 6, null, null, 3, null);
            registered = true;
            Marshal.FinalReleaseComObject(task); task = null;
            Assert.Equal(2, await Execute(["--pipe", pipe, "--maintenance"]));
            task = folder.GetTask(tasks.Name("Host"));
            Assert.Contains("Other.Product", (string)task.Xml);
            Assert.Equal("Maintenance", files.StopReason(registration.Generation));
        }
        finally
        {
            if (task is not null) Marshal.FinalReleaseComObject(task);
            if (registered) folder!.DeleteTask(tasks.Name("Host"), 0);
            if (folder is not null) Marshal.FinalReleaseComObject(folder);
            if (service is not null) Marshal.FinalReleaseComObject(service);
            DeleteState(pipe);
        }
    }

    private static string Executable => Path.Combine(AppContext.BaseDirectory, "GuardExecutable", "svchost.exe");
    private static string Bundle => Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory);
    private static string StateDirectory(string pipe) => Path.Combine(GuardFiles.DataDirectory(pipe), "guard");
    private static async Task<int> Execute(string[] arguments)
    {
        Assert.True(File.Exists(Executable), "Fresh guardian test bundle missing.");
        var start = new ProcessStartInfo(Executable) { UseShellExecute = false, CreateNoWindow = true };
        foreach (string argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10)); }
        finally { if (!process.HasExited) { process.Kill(); await process.WaitForExitAsync(); } }
        return process.ExitCode;
    }
    private static void DeleteState(string pipe)
    {
        string target = Path.GetFullPath(GuardFiles.DataDirectory(pipe));
        string boundary = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "NPEduTools", "instances")) + Path.DirectorySeparatorChar;
        if (!target.StartsWith(boundary, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Private maintenance state escaped its test boundary.");
        if (Directory.Exists(target)) Directory.Delete(target, true);
    }
}

using System.Diagnostics;
using System.Runtime.Versioning;
using System.Runtime.InteropServices;
using NPEduTools.Core;
using Xunit.Abstractions;

namespace NPEduTools.Tests;

[SupportedOSPlatform("windows")]
public sealed class GuardProcessTests(ITestOutputHelper output)
{
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr FindWindow(string? className, string caption);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint pid);
    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr SendMessageTimeout(IntPtr window, uint message, IntPtr wParam, IntPtr lParam, uint flags, uint timeout, out UIntPtr result);
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task Independent_guard_recovers_owned_crashes_then_honors_maintenance_or_simulated_session_end(bool sessionEnding)
    {
        string pipe = "NPEduTools.Test.Guard." + Guid.NewGuid().ToString("N");
        string bundle = Path.Combine(Path.GetTempPath(), "NPEduTools.GuardProcessTests", Guid.NewGuid().ToString("N"));
        var files = GuardFiles.ForPipe(pipe);
        Process? app = null, host = null, guard = null;
        GuardTasks? tasks = null;
        var recovered = new List<GuardProcess>();
        try
        {
            Copy(Path.Combine(AppContext.BaseDirectory, "GuardFixture"), bundle);
            Copy(Path.Combine(AppContext.BaseDirectory, "GuardFixture"), Path.Combine(bundle, "Host"));
            Copy(Path.Combine(AppContext.BaseDirectory, "GuardExecutable"), Path.Combine(bundle, "Guard"));
            File.Copy(Path.Combine(bundle, "NPEduTools.GuardFixture.exe"), Path.Combine(bundle, "NPEduTools.App.exe"));
            File.Copy(Path.Combine(bundle, "Host", "NPEduTools.GuardFixture.exe"), Path.Combine(bundle, "Host", "NPEduTools.Host.exe"));
            tasks = new GuardTasks(bundle, pipe, null);
            tasks.Register(highest: false); // Current-user synthetic tasks; no installed App or sensors.
            tasks.Run("App");
            await Until(() => files.Read<GuardRegistration>("registration.json") is not null);
            app = Process.GetProcessById(files.Read<GuardRegistration>("registration.json")!.App.Id);
            tasks.Run("Host");
            await Until(() => files.Read<GuardLease>("lease.json") is not null);
            host = Process.GetProcessById(files.Read<GuardLease>("lease.json")!.Host.Id);
            tasks.Run("Guard");
            await Until(() => files.Read<GuardStatus>("status.json") is { GuardPid: > 0 });
            guard = Process.GetProcessById(files.Read<GuardStatus>("status.json")!.GuardPid);
            await Until(() => files.Read<GuardStatus>("status.json")?.Phase == "Armed");
            var registration = files.Read<GuardRegistration>("registration.json")!;
            var processes = new GuardProcesses(Path.Combine(bundle, "NPEduTools.App.exe"), Path.Combine(bundle, "Host", "NPEduTools.Host.exe"), pipe, null);
            var originalHost = files.Read<GuardLease>("lease.json")!.Host;
            Assert.True(processes.Matches(originalHost, false));
            Assert.False(processes.Matches(originalHost with { StartedUtcTicks = originalHost.StartedUtcTicks + 1 }, false));
            host.Kill(entireProcessTree: true); await host.WaitForExitAsync();
            await Until(() => files.Read<GuardLease>("lease.json")?.Host is { } p && p != originalHost && processes.Matches(p, false));
            recovered.Add(files.Read<GuardLease>("lease.json")!.Host);
            using (var freshHost = Process.GetProcessById(recovered[^1].Id))
                Assert.NotEqual(guard.Id, Parent(freshHost)); // Recovery is owned by Task Scheduler.
            Assert.False(app.HasExited); // Actual independent guard survives its monitored Host's death.
            app.Kill(entireProcessTree: true); await app.WaitForExitAsync();
            await Until(() => files.Read<GuardRegistration>("registration.json")?.App is { } p && p != registration.App && processes.Matches(p, true) &&
                files.Read<string[]>("fixture-app-args.json")?.Contains("--guard-recovery") == true && files.Read<GuardStatus>("status.json")?.Restarts == 2);
            recovered.Add(files.Read<GuardRegistration>("registration.json")!.App);
            Assert.Contains("--guard-recovery", files.Read<string[]>("fixture-app-args.json")!);
            Assert.Equal(2, files.Read<GuardStatus>("status.json")!.Restarts);
            using (var freshApp = Process.GetProcessById(recovered[^1].Id)) Assert.NotEqual(guard.Id, Parent(freshApp));
            Assert.NotEqual(app.Id, Parent(guard)); Assert.NotEqual(host.Id, Parent(guard));
            int oldGuard = guard.Id;
            guard.Kill(entireProcessTree: true); await guard.WaitForExitAsync(); guard.Dispose(); guard = null;
            await Until(() => files.Read<GuardStatus>("status.json") is { } s && s.GuardPid != oldGuard && s.Phase == "Armed");
            guard = Process.GetProcessById(files.Read<GuardStatus>("status.json")!.GuardPid);
            if (sessionEnding)
            {
                var window = FindWindow(null, "NPEduTools Guard " + pipe);
                Assert.NotEqual(IntPtr.Zero, window);
                GetWindowThreadProcessId(window, out uint pid); Assert.Equal((uint)guard.Id, pid);
                // Message goes only to our isolated guard window. It does not log off Windows.
                Assert.NotEqual(IntPtr.Zero, SendMessageTimeout(window, 0x0011, IntPtr.Zero, IntPtr.Zero, 2, 3000, out _));
                Assert.False(files.Stopped(registration.Generation)); Assert.False(guard.HasExited);
                Assert.True(files.SessionEnding(registration.Generation));
                Assert.NotEqual(IntPtr.Zero, SendMessageTimeout(window, 0x0016, IntPtr.Zero, IntPtr.Zero, 2, 3000, out _));
                Assert.False(files.SessionEnding(registration.Generation)); Assert.False(guard.HasExited);
                // A canceled shutdown must preserve later abnormal-exit recovery.
                using var liveHost = Process.GetProcessById(recovered[0].Id);
                liveHost.Kill(); await liveHost.WaitForExitAsync();
                await Until(() => files.Read<GuardLease>("lease.json")?.Host is { } p && p != recovered[0] && processes.Matches(p, false), 24000);
                recovered.Add(files.Read<GuardLease>("lease.json")!.Host);
                Assert.NotEqual(IntPtr.Zero, SendMessageTimeout(window, 0x0011, IntPtr.Zero, IntPtr.Zero, 2, 3000, out _));
                Assert.NotEqual(IntPtr.Zero, SendMessageTimeout(window, 0x0016, (IntPtr)1, IntPtr.Zero, 2, 3000, out _));
                Assert.True(files.Stopped(registration.Generation));
            }
            else files.Stop(registration.Generation, "Maintenance");
            await Until(() => guard.HasExited);
            Assert.Equal("Stopped", files.Read<GuardStatus>("status.json")!.Phase);
        }
        finally
        {
            if (files.Read<GuardRegistration>("registration.json") is { } finalRegistration) files.Stop(finalRegistration.Generation, "Maintenance");
            if (guard is { HasExited: false }) { guard.Kill(); await guard.WaitForExitAsync(); }
            foreach (var identity in recovered.Concat(new[] { files.Read<GuardLease>("lease.json")?.Host, files.Read<GuardRegistration>("registration.json")?.App }.OfType<GuardProcess>()).Distinct())
            {
                var processes = new GuardProcesses(Path.Combine(bundle, "NPEduTools.App.exe"), Path.Combine(bundle, "Host", "NPEduTools.Host.exe"), pipe, null);
                if (processes.Matches(identity, true) || processes.Matches(identity, false))
                { using var p = Process.GetProcessById(identity.Id); p.Kill(); await p.WaitForExitAsync(); }
            }
            foreach (var p in new[] { app, host }) { if (p is { HasExited: false }) { p.Kill(); await p.WaitForExitAsync(); } p?.Dispose(); }
            guard?.Dispose();
            tasks?.Unregister();
            await DeleteFixtureDirectory(bundle, Path.Combine(Path.GetTempPath(), "NPEduTools.GuardProcessTests"));
            string instanceDirectory = GuardFiles.DataDirectory(pipe);
            await DeleteFixtureDirectory(instanceDirectory, Path.Combine(Path.GetTempPath(), "NPEduTools", "instances"));
        }
    }
    [DllImport("ntdll.dll")] private static extern int NtQueryInformationProcess(IntPtr process, int information, out BasicInformation value, int length, out int returned);
    [StructLayout(LayoutKind.Sequential)] private struct BasicInformation
    { public IntPtr ExitStatus, Peb, Affinity, Priority, Pid, ParentPid; }
    private static int Parent(Process process)
    {
        Assert.Equal(0, NtQueryInformationProcess(process.Handle, 0, out var info, Marshal.SizeOf<BasicInformation>(), out _));
        return checked((int)info.ParentPid);
    }
    [Fact]
    public async Task Standby_guard_is_restarted_by_scheduler_even_when_app_and_host_are_both_gone()
    {
        string pipe = "NPEduTools.Test.Guard." + Guid.NewGuid().ToString("N");
        string bundle = Path.Combine(Path.GetTempPath(), "NPEduTools.GuardProcessTests", Guid.NewGuid().ToString("N"));
        var files = GuardFiles.ForPipe(pipe); var tasks = new GuardTasks(bundle, pipe, null);
        Process? app = null, host = null, guard = null;
        try
        {
            Copy(Path.Combine(AppContext.BaseDirectory, "GuardFixture"), bundle);
            Copy(Path.Combine(AppContext.BaseDirectory, "GuardFixture"), Path.Combine(bundle, "Host"));
            Copy(Path.Combine(AppContext.BaseDirectory, "GuardExecutable"), Path.Combine(bundle, "Guard"));
            File.Copy(Path.Combine(bundle, "NPEduTools.GuardFixture.exe"), Path.Combine(bundle, "NPEduTools.App.exe"));
            File.Copy(Path.Combine(bundle, "Host", "NPEduTools.GuardFixture.exe"), Path.Combine(bundle, "Host", "NPEduTools.Host.exe"));
            files.Write("fixture-mode.json", "Standby"); tasks.Register(false); tasks.Run("App");
            await Until(() => files.Read<GuardRegistration>("registration.json") is not null);
            app = Process.GetProcessById(files.Read<GuardRegistration>("registration.json")!.App.Id);
            tasks.Run("Host"); await Until(() => files.Read<GuardLease>("lease.json") is not null);
            host = Process.GetProcessById(files.Read<GuardLease>("lease.json")!.Host.Id);
            tasks.Run("Guard"); await Until(() => files.Read<GuardStatus>("status.json")?.Phase == "Watching");
            guard = Process.GetProcessById(files.Read<GuardStatus>("status.json")!.GuardPid);
            app.Kill(true); host.Kill(true); await app.WaitForExitAsync(); await host.WaitForExitAsync();
            await Task.Delay(3000); Assert.False(guard.HasExited);
            int firstGuard = guard.Id; guard.Kill(true); await guard.WaitForExitAsync(); guard.Dispose(); guard = null;
            // The one-minute fallback is real Task Scheduler recovery, with no surviving app watcher.
            await Until(() => files.Read<GuardStatus>("status.json") is { } s && s.GuardPid != firstGuard && s.Phase == "Watching", 95000);
            guard = Process.GetProcessById(files.Read<GuardStatus>("status.json")!.GuardPid); guard.Refresh();
            Assert.True(app.HasExited); Assert.True(host.HasExited); Assert.NotEqual(app.Id, Parent(guard));
            output.WriteLine($"Synthetic standby guard: workingSet={guard.WorkingSet64}, privateBytes={guard.PrivateMemorySize64}, parentPid={Parent(guard)}; no sensors or network.");
        }
        finally
        {
            if (files.Read<GuardRegistration>("registration.json") is { } r) files.Stop(r.Generation, "Maintenance");
            tasks.Unregister();
            foreach (var p in new[] { guard, app, host })
            { if (p is { HasExited: false }) { p.Kill(); await p.WaitForExitAsync(); } p?.Dispose(); }
            await DeleteFixtureDirectory(bundle, Path.Combine(Path.GetTempPath(), "NPEduTools.GuardProcessTests"));
            await DeleteFixtureDirectory(GuardFiles.DataDirectory(pipe), Path.Combine(Path.GetTempPath(), "NPEduTools", "instances"));
        }
    }
    private static void Copy(string source, string target)
    {
        Assert.True(Directory.Exists(source), "Fresh guard test bundle missing: " + source);
        Directory.CreateDirectory(target);
        foreach (string path in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
        { string destination = Path.Combine(target, Path.GetRelativePath(source, path)); Directory.CreateDirectory(Path.GetDirectoryName(destination)!); File.Copy(path, destination); }
    }
    private static async Task Until(Func<bool> predicate, int milliseconds = 18000)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromMilliseconds(milliseconds));
        while (!predicate()) await Task.Delay(50, deadline.Token);
    }
    private static async Task DeleteFixtureDirectory(string path, string parent)
    {
        string target = Path.GetFullPath(path), boundary = Path.TrimEndingDirectorySeparator(Path.GetFullPath(parent)) + Path.DirectorySeparatorChar;
        if (!target.StartsWith(boundary, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Fixture cleanup escaped its temporary root.");
        // Windows/AV can briefly retain an executable's image after the verified child has exited.
        // Retry cleanup only; recovery assertions have already completed and are never retried here.
        for (int attempt = 0; Directory.Exists(target); attempt++)
        {
            try { Directory.Delete(target, true); }
            catch (Exception error) when (attempt < 49 && error is IOException or UnauthorizedAccessException) { await Task.Delay(100); }
        }
    }
}

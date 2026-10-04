using System.Diagnostics;
using System.Runtime.Versioning;
using System.Runtime.InteropServices;
using NPEduTools.Core;

namespace NPEduTools.Tests;

[SupportedOSPlatform("windows")]
public sealed class GuardProcessTests
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
        var recovered = new List<GuardProcess>();
        try
        {
            Copy(Path.Combine(AppContext.BaseDirectory, "GuardFixture"), bundle);
            Copy(Path.Combine(AppContext.BaseDirectory, "GuardFixture"), Path.Combine(bundle, "Host"));
            Copy(Path.Combine(AppContext.BaseDirectory, "GuardExecutable"), Path.Combine(bundle, "Guard"));
            File.Copy(Path.Combine(bundle, "NPEduTools.GuardFixture.exe"), Path.Combine(bundle, "NPEduTools.App.exe"));
            File.Copy(Path.Combine(bundle, "Host", "NPEduTools.GuardFixture.exe"), Path.Combine(bundle, "Host", "NPEduTools.Host.exe"));
            app = Start(Path.Combine(bundle, "NPEduTools.App.exe"), pipe);
            await Until(() => files.Read<GuardRegistration>("registration.json") is not null);
            host = Start(Path.Combine(bundle, "Host", "NPEduTools.Host.exe"), pipe);
            guard = Start(Path.Combine(bundle, "Guard", "NPEduTools.Guard.exe"), pipe);
            await Until(() => files.Read<GuardStatus>("status.json")?.Phase == "Armed");
            var registration = files.Read<GuardRegistration>("registration.json")!;
            var processes = new GuardProcesses(Path.Combine(bundle, "NPEduTools.App.exe"), Path.Combine(bundle, "Host", "NPEduTools.Host.exe"), pipe, null);
            var originalHost = files.Read<GuardLease>("lease.json")!.Host;
            Assert.True(processes.Matches(originalHost, false));
            Assert.False(processes.Matches(originalHost with { StartedUtcTicks = originalHost.StartedUtcTicks + 1 }, false));
            host.Kill(); await host.WaitForExitAsync();
            await Until(() => files.Read<GuardLease>("lease.json")?.Host is { } p && p != originalHost && processes.Matches(p, false));
            recovered.Add(files.Read<GuardLease>("lease.json")!.Host);
            Assert.False(app.HasExited); // Actual independent guard survives its monitored Host's death.
            app.Kill(); await app.WaitForExitAsync();
            await Until(() => files.Read<GuardRegistration>("registration.json")?.App is { } p && p != registration.App && processes.Matches(p, true) &&
                files.Read<string[]>("fixture-app-args.json")?.Contains("--guard-recovery") == true && files.Read<GuardStatus>("status.json")?.Restarts == 2);
            recovered.Add(files.Read<GuardRegistration>("registration.json")!.App);
            Assert.Contains("--guard-recovery", files.Read<string[]>("fixture-app-args.json")!);
            Assert.Equal(2, files.Read<GuardStatus>("status.json")!.Restarts);
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
            if (guard is { HasExited: false }) { guard.Kill(); await guard.WaitForExitAsync(); }
            foreach (var identity in recovered.Concat(new[] { files.Read<GuardLease>("lease.json")?.Host, files.Read<GuardRegistration>("registration.json")?.App }.OfType<GuardProcess>()).Distinct())
            {
                var processes = new GuardProcesses(Path.Combine(bundle, "NPEduTools.App.exe"), Path.Combine(bundle, "Host", "NPEduTools.Host.exe"), pipe, null);
                if (processes.Matches(identity, true) || processes.Matches(identity, false))
                { using var p = Process.GetProcessById(identity.Id); p.Kill(); await p.WaitForExitAsync(); }
            }
            foreach (var p in new[] { app, host }) { if (p is { HasExited: false }) { p.Kill(); await p.WaitForExitAsync(); } p?.Dispose(); }
            guard?.Dispose();
            await DeleteFixtureDirectory(bundle, Path.Combine(Path.GetTempPath(), "NPEduTools.GuardProcessTests"));
            string instanceDirectory = GuardFiles.DataDirectory(pipe);
            await DeleteFixtureDirectory(instanceDirectory, Path.Combine(Path.GetTempPath(), "NPEduTools", "instances"));
        }
    }
    private static Process Start(string executable, string pipe)
    {
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden };
        start.ArgumentList.Add("--pipe"); start.ArgumentList.Add(pipe);
        return Process.Start(start)!;
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

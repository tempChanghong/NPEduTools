using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Runtime.Versioning;
using NPEduTools.Contracts;

namespace NPEduTools.Core;

public sealed record GuardProcess(int Id, long StartedUtcTicks)
{
    public static GuardProcess Current() { using var p = Process.GetCurrentProcess(); return new(p.Id, p.StartTime.ToUniversalTime().Ticks); }
}
public sealed record GuardRegistration(Guid Generation, GuardProcess App, string? Upstream, string? BundleDirectory = null);
public sealed record GuardLease(Guid Generation, GuardProcess Host, long Sequence, bool Armed);
public sealed record GuardStatus(Guid Generation, string Phase, int Restarts, string Message, DateTimeOffset UpdatedAt, int GuardPid);

// Local lifecycle records contain no commands, credentials, school identity or audio.
public sealed class GuardFiles(string directory)
{
    [SupportedOSPlatform("windows")]
    public static string DataDirectory(string pipe) => pipe == PipeEndpoint.DefaultName
        ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NPEduTools", "config")
        : Path.Combine(Path.GetTempPath(), "NPEduTools", "instances", Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(pipe)))[..24]);
    [SupportedOSPlatform("windows")]
    public static GuardFiles ForPipe(string pipe) => new(Path.Combine(DataDirectory(pipe), "guard"));
    public T? Read<T>(string name) where T : class
    {
        try
        {
            using var f = new FileStream(Path.Combine(directory, name), FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (f.Length > 32768) return null;
            var value = JsonSerializer.Deserialize<T>(f);
            if (value is GuardRegistration r && (r.Generation == Guid.Empty || !Valid(r.App) ||
                r.Upstream is { } upstream && (upstream.Length is 0 or > 200 || upstream.IndexOfAny(['/', '\\', ':']) >= 0 || upstream.StartsWith("--", StringComparison.Ordinal)))) return null;
            if (value is GuardLease lease && (lease.Generation == Guid.Empty || !Valid(lease.Host) || lease.Sequence <= 0)) return null;
            return value;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) { return null; }
    }
    public void Write<T>(string name, T value)
    {
        Directory.CreateDirectory(directory);
        string destination = Path.Combine(directory, name), temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var f = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { JsonSerializer.Serialize(f, value); f.Flush(true); }
            File.Move(temporary, destination, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    public bool Stopped(Guid generation) => Read<GuardStop>("stop.json")?.Generation == generation;
    public bool SessionEnding(Guid generation) => Read<GuardSessionQuery>("session-query.json") is { Pending: true } query && query.Generation == generation;
    public void SessionQuery(Guid generation, bool pending) => Write("session-query.json", new GuardSessionQuery(generation, pending));
    public void Stop(Guid generation, string reason) => Write("stop.json", new GuardStop(generation, reason));
    public void StopCurrent(string reason)
    { if (Read<GuardRegistration>("registration.json") is { } r) Stop(r.Generation, reason); }
    private sealed record GuardStop(Guid Generation, string Reason);
    private sealed record GuardSessionQuery(Guid Generation, bool Pending);
    private static bool Valid(GuardProcess? process) => process is { Id: > 0, StartedUtcTicks: > 0 } && process.StartedUtcTicks <= DateTime.MaxValue.Ticks;
}

public sealed class GuardRecoveryPolicy
{
    private double _armedAt = double.NegativeInfinity, _due = double.PositiveInfinity;
    private GuardProcess? _host;
    private long _sequence = -1;
    public int Restarts { get; private set; }
    public bool Limited => Restarts >= 3;
    public void Observe(GuardLease lease, double seconds)
    {
        if (lease.Sequence <= 0 || lease.Host is null || lease.Host.Id <= 0 || lease.Host.StartedUtcTicks <= 0) return;
        if (_host == lease.Host && lease.Sequence <= _sequence) return; // Old files never renew eligibility.
        _host = lease.Host; _sequence = lease.Sequence;
        _armedAt = lease.Armed ? seconds : double.NegativeInfinity;
        if (!lease.Armed) _due = double.PositiveInfinity;
    }
    public bool Armed(double seconds) => seconds >= _armedAt && seconds - _armedAt <= 30;
    public void Suspend() { _armedAt = double.NegativeInfinity; _due = double.PositiveInfinity; }
    public bool MayRestart(bool processMissing, bool interactive, double seconds)
    {
        if (!processMissing || !interactive || !Armed(seconds) || Limited) { _due = double.PositiveInfinity; return false; }
        if (double.IsPositiveInfinity(_due)) _due = seconds + new[] { 2d, 5d, 15d }[Restarts];
        return seconds >= _due;
    }
    public void Attempted() { Restarts++; _due = double.PositiveInfinity; }
}

public interface IGuardProcesses
{
    bool Matches(GuardProcess identity, bool app);
    bool IsAlive(GuardProcess identity);
    GuardProcess Start(bool app);
}

public sealed class GuardProcesses(string appPath, string hostPath, string pipe, string? upstream) : IGuardProcesses
{
    public bool IsAlive(GuardProcess identity)
    {
        try
        {
            using var p = Process.GetProcessById(identity.Id);
            return !p.HasExited && p.StartTime.ToUniversalTime().Ticks == identity.StartedUtcTicks;
        }
        catch (ArgumentException) { return false; }
        catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception) { return true; }
    }
    public bool Matches(GuardProcess identity, bool app)
    {
        try
        {
            using var p = Process.GetProcessById(identity.Id);
            using var self = Process.GetCurrentProcess();
            return !p.HasExited && p.StartTime.ToUniversalTime().Ticks == identity.StartedUtcTicks &&
                p.SessionId == self.SessionId &&
                string.Equals(Path.GetFullPath(p.MainModule!.FileName), Path.GetFullPath(app ? appPath : hostPath), StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception or IOException) { return false; }
    }
    public GuardProcess Start(bool app)
    {
        string target = app ? appPath : hostPath;
        var start = new ProcessStartInfo(target) { UseShellExecute = false, CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden, WorkingDirectory = Path.GetDirectoryName(target)! };
        start.ArgumentList.Add("--pipe"); start.ArgumentList.Add(pipe);
        if (upstream is not null) { start.ArgumentList.Add("--classisland-pipe"); start.ArgumentList.Add(upstream); }
        if (app) start.ArgumentList.Add("--guard-recovery");
        using var p = Process.Start(start) ?? throw new IOException("Guard child did not start.");
        return new(p.Id, p.StartTime.ToUniversalTime().Ticks);
    }
}

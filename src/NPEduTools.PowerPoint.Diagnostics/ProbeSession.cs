using System.Diagnostics;
using System.Reflection;
using System.Runtime.Versioning;
using System.Text.Json;

namespace NPEduTools.PowerPoint.Diagnostics;

internal sealed record SnapshotFrame(ShowSnapshot Snapshot, long ReceivedAt);

[SupportedOSPlatform("windows")]
internal sealed class ProbeSession
{
    private readonly Func<ProcessStartInfo> _start;
    private readonly TimeSpan _readTimeout;
    public ProbeSession(Func<ProcessStartInfo>? start = null, TimeSpan? readTimeout = null)
    { _start = start ?? (() => StartInfo()); _readTimeout = readTimeout ?? TimeSpan.FromSeconds(5); }
    private SnapshotFrame _latest = new(new(DateTimeOffset.UtcNow, "Starting"), Stopwatch.GetTimestamp());
    public SnapshotFrame Latest => Volatile.Read(ref _latest);

    internal static ProcessStartInfo StartInfo(string mode = "--com-worker")
    {
        string executable = Environment.ProcessPath ?? throw new InvalidOperationException("Missing executable path.");
        var start = new ProcessStartInfo(executable)
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardInput = true };
        if (Path.GetFileNameWithoutExtension(executable).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            start.ArgumentList.Add(Assembly.GetExecutingAssembly().Location);
        start.ArgumentList.Add(mode);
        return start;
    }

    public async Task RunAsync(Action<ShowSnapshot> publish, CancellationToken cancellationToken)
    {
        void Update(ShowSnapshot snapshot)
        {
            Volatile.Write(ref _latest, new(snapshot, Stopwatch.GetTimestamp()));
            publish(snapshot);
        }
        while (!cancellationToken.IsCancellationRequested)
        {
            Process? worker = null;
            using var leaseCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            Task lease = Task.CompletedTask;
            try
            {
                worker = Process.Start(_start()) ?? throw new InvalidOperationException("Worker failed to start.");
                lease = RenewAsync(worker, leaseCancellation.Token);
                while (!cancellationToken.IsCancellationRequested)
                {
                    // A fresh worker may take longer to load; subsequent reads must stay responsive.
                    string? line = await worker.StandardOutput.ReadLineAsync(cancellationToken).AsTask()
                        .WaitAsync(_readTimeout, cancellationToken);
                    if (line is null || line.Length > 65536) throw new IOException("Invalid worker output.");
                    var snapshot = JsonSerializer.Deserialize<ShowSnapshot>(line) ?? throw new IOException("Empty worker output.");
                    Update(snapshot);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
            catch (Exception error) when (error is not OutOfMemoryException)
            { Update(new(DateTimeOffset.UtcNow, error is TimeoutException ? "ProbeTimeout" : "WorkerUnavailable", Error: error.GetType().Name)); }
            finally
            {
                leaseCancellation.Cancel();
                try { await lease; } catch (Exception error) when (error is OperationCanceledException or IOException or InvalidOperationException) { }
                if (worker is not null)
                {
                    // This Process object was created above; never search for or kill POWERPNT.
                    try { if (!worker.HasExited) worker.Kill(); }
                    catch (InvalidOperationException) { }
                    worker.Dispose();
                }
            }
            if (!cancellationToken.IsCancellationRequested)
            {
                try { await Task.Delay(1000, cancellationToken); }
                catch (OperationCanceledException) { }
            }
        }
    }

    private static async Task RenewAsync(Process worker, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            await worker.StandardInput.WriteLineAsync("lease".AsMemory(), cancellationToken);
            await worker.StandardInput.FlushAsync(cancellationToken);
            await Task.Delay(1000, cancellationToken);
        }
    }

    public static int Worker()
    {
        long lastLease = Stopwatch.GetTimestamp();
        using var timer = new Timer(_ =>
        {
            if (Stopwatch.GetElapsedTime(Interlocked.Read(ref lastLease)).TotalSeconds > 8) Environment.Exit(124);
        }, null, 1000, 1000);
        new Thread(() =>
        {
            try
            {
                while (Console.ReadLine() is not null) Interlocked.Exchange(ref lastLease, Stopwatch.GetTimestamp());
            }
            catch (IOException) { }
            Environment.Exit(0);
        }) { IsBackground = true, Name = "PowerPoint diagnostic lease" }.Start();
        var reader = new PowerPointReader();
        while (true)
        {
            Console.WriteLine(JsonSerializer.Serialize(reader.Read()));
            Console.Out.Flush();
            var started = Stopwatch.GetTimestamp();
            while (Stopwatch.GetElapsedTime(started).TotalMilliseconds < 250)
            { Native.Pump(); Thread.Sleep(10); }
        }
    }
}

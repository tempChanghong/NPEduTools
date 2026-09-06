using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;

namespace NPEduTools.PowerPoint.Diagnostics;

[SupportedOSPlatform("windows")]
internal static class StepExperiment
{
    public static int Worker(Guid requestId)
    {
        // Even a blocked COM call cannot leave this experiment worker alive indefinitely.
        using var deadline = new Timer(_ => Environment.Exit(124), null, 8000, Timeout.Infinite);
        if (Console.ReadLine() != "execute") return 2;
        Console.WriteLine(JsonSerializer.Serialize(PowerPointStep.Execute(requestId)));
        return 0;
    }

    public static int Run()
    {
        Guid id = Guid.NewGuid();
        Process? worker = null;
        bool locked = false;
        using var mutex = new Mutex(false, $"Local\\NPEduTools.PowerPoint.Step.{Process.GetCurrentProcess().SessionId}");
        try
        {
            try { locked = mutex.WaitOne(0); } catch (AbandonedMutexException) { locked = true; }
            if (!locked)
            {
                Console.WriteLine(JsonSerializer.Serialize(new StepResult(id, DateTimeOffset.UtcNow, "Refused", "AnotherStepRunning")));
                return 3;
            }
            string folder = Path.Combine(AppContext.BaseDirectory, "diagnostics");
            Directory.CreateDirectory(folder);
            string log = Path.Combine(folder, $"step-{id:N}.step.jsonl");
            using var stream = new FileStream(log, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
            using var writer = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true };
            writer.WriteLine(JsonSerializer.Serialize(new { requestId = id, at = DateTimeOffset.UtcNow, stage = "Intent", mode = "ExplicitStepExperiment" }));
            stream.Flush(true);
            StepResult result;
            bool dispatched = false;
            try
            {
                var start = ProbeSession.StartInfo("--step-worker");
                start.ArgumentList.Add(id.ToString());
                worker = Process.Start(start) ?? throw new IOException("Worker failed to start.");
                dispatched = true;
                worker.StandardInput.WriteLine("execute");
                worker.StandardInput.Flush();
                string? line = worker.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(7)).GetAwaiter().GetResult();
                if (line is null || line.Length > 65536) throw new IOException("Invalid step result.");
                result = JsonSerializer.Deserialize<StepResult>(line) ?? throw new IOException("Missing step result.");
                if (result.RequestId != id || result.Outcome is not ("Succeeded" or "NoOp" or "Refused" or "Unknown"))
                    throw new IOException("Unexpected step result.");
            }
            catch (Exception error) when (error is not OutOfMemoryException)
            { result = new(id, DateTimeOffset.UtcNow, dispatched ? "Unknown" : "Refused", error.GetType().Name); }
            // Records are diagnostic evidence, never a replay queue. A missing Result stays uncertain.
            try { writer.WriteLine(JsonSerializer.Serialize(result)); stream.Flush(true); }
            catch (IOException) { Console.Error.WriteLine("执行结果无法保存，请核对 PowerPoint 当前状态；不会重试。"); }
            Console.WriteLine(JsonSerializer.Serialize(result));
            Console.Error.WriteLine($"实验记录：{log}");
            return result.Outcome is "Succeeded" or "NoOp" ? 0 : result.Outcome == "Unknown" ? 4 : 3;
        }
        finally
        {
            if (worker is not null)
            {
                try { if (!worker.HasExited) worker.Kill(); } catch (InvalidOperationException) { }
                worker.Dispose();
            }
            if (locked) mutex.ReleaseMutex();
        }
    }
}

using System.Diagnostics;
using System.Reflection;
using System.Text.Json.Nodes;
using NPEduTools.Host;
using NPEduTools.Contracts;
using System.Text.Json;

internal static class StudyReminderRecoveryAcceptance
{
    private sealed class Clock : TimeProvider
    { public override long TimestampFrequency => 1000; public override long GetTimestamp() => 0; }
    public static int ReadInFreshProcess(string fixturePath)
    {
        try
        {
            string path = Path.GetFullPath(fixturePath), directory = Path.GetDirectoryName(path)!;
            string temp = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            string name = Path.GetFileName(directory), prefix = "npep-study-acceptance-";
            if (!directory.StartsWith(temp, StringComparison.OrdinalIgnoreCase) || !name.StartsWith(prefix, StringComparison.Ordinal) ||
                !Guid.TryParseExact(name[prefix.Length..], "N", out _) || Path.GetFileName(path) != "reminder-child.json" || new FileInfo(path).Length > 8192)
                throw new InvalidDataException("ISOLATED_REMINDER_FIXTURE_REQUIRED");
            var fixture = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
            if (fixture["isolatedStudyReminder"]?.GetValue<bool>() != true || fixture["scope"]?.GetValue<string>() is not { Length: > 0 and < 4096 } scope)
                throw new InvalidDataException("ISOLATED_REMINDER_FIXTURE_REQUIRED");
            var store = new StudyReminderStore(directory, new Clock());
            if (store.Error is not null || store.CanRemind(scope, 600)) throw new InvalidDataException("COOLDOWN_NOT_RECOVERED");
            if (fixture["stageWindow"] is not null)
            {
                var window = fixture["stageWindow"]!.Deserialize<SchoolNoiseWindow>(Protocol.Json);
                string phase = fixture["stagePhase"]!.GetValue<string>();
                if (window is null || window.End <= window.Start || phase is not ("Preparing" or "Active" or "Ended") ||
                    new StudyStageReminderStore(directory).Claim(scope, window, phase) != StudyStageClaim.Duplicate)
                    throw new InvalidDataException("STAGE_CLAIM_NOT_RECOVERED");
            }
            Console.WriteLine("PASS fresh process reads durable cooldown; no Host, sensor or network startup"); return 0;
        }
        catch (Exception e) when (e is IOException or InvalidDataException or System.Text.Json.JsonException)
        { Console.Error.WriteLine("Isolated reminder recovery failed: " + e.GetType().Name); return 1; }
    }
    public static async Task VerifySeparateProcessesAsync(string directory, string scope, SchoolNoiseWindow? stageWindow = null, string? stagePhase = null)
    {
        string fixturePath = Path.Combine(directory, "reminder-child.json");
        File.WriteAllText(fixturePath, new JsonObject { ["isolatedStudyReminder"] = true, ["scope"] = scope,
            ["stageWindow"] = JsonSerializer.SerializeToNode(stageWindow, Protocol.Json), ["stagePhase"] = stagePhase }.ToJsonString());
        byte[] saved = File.ReadAllBytes(Path.Combine(directory, "study-reminder-state.json"));
        byte[]? savedStage = stageWindow is null ? null : File.ReadAllBytes(Path.Combine(directory, "study-stage-reminders.json"));
        try
        {
            for (int i = 0; i < 2; i++)
            {
                var start = new ProcessStartInfo("dotnet") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
                start.ArgumentList.Add(Assembly.GetExecutingAssembly().Location); start.ArgumentList.Add("--study-reminder-local"); start.ArgumentList.Add(fixturePath);
                using var process = Process.Start(start) ?? throw new IOException("REMINDER_CHILD_START_FAILED");
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                try
                {
                    var stdout = process.StandardOutput.ReadToEndAsync(timeout.Token); var stderr = process.StandardError.ReadToEndAsync(timeout.Token);
                    await process.WaitForExitAsync(timeout.Token); string output = await stdout; await stderr;
                    if (process.ExitCode != 0 || !output.StartsWith("PASS fresh process", StringComparison.Ordinal)) throw new InvalidDataException("REMINDER_CHILD_FAILED");
                }
                finally { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            }
            if (!saved.AsSpan().SequenceEqual(File.ReadAllBytes(Path.Combine(directory, "study-reminder-state.json")))) throw new InvalidDataException("READ_ONLY_RECOVERY_CHANGED_JOURNAL");
            if (savedStage is not null && !savedStage.AsSpan().SequenceEqual(File.ReadAllBytes(Path.Combine(directory, "study-stage-reminders.json"))))
                throw new InvalidDataException("READ_ONLY_RECOVERY_CHANGED_STAGE_JOURNAL");
        }
        finally { File.Delete(fixturePath); } // Exact one fixture created above, no recursive cleanup.
    }
}

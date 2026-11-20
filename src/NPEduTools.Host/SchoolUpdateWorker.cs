using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using NPEduTools.Contracts;
using NPEduTools.Integrations.Npep;

namespace NPEduTools.Host;

internal sealed class SchoolUpdateWorker(Process process, Guid id) : IPreparedSchoolUpdate
{
    private static readonly UTF8Encoding Wire = new(false, true);
    internal static string ProductPath(string name) =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "NPDownloader", name);
    internal static async Task<IPreparedSchoolUpdate> PrepareAsync(ManagedUpdatePlan plan, string version, CancellationToken token)
    {
        string executable = ProductPath("NPDownloader.Worker.exe");
        if (!File.Exists(executable)) throw new SchoolUpdateException("DOWNLOADER_UNAVAILABLE", true);
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true,
            StandardInputEncoding = Wire, StandardOutputEncoding = Wire,
            WorkingDirectory = Path.GetDirectoryName(executable)!
        };
        Process child;
        try { child = Process.Start(start) ?? throw new IOException(); }
        catch (Exception e) when (e is IOException or System.ComponentModel.Win32Exception or UnauthorizedAccessException)
        { throw new SchoolUpdateException("DOWNLOADER_UNAVAILABLE", true); }
        var worker = new SchoolUpdateWorker(child, plan.Id);
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
            deadline.CancelAfter(TimeSpan.FromMinutes(15));
            await worker.SendAsync(new { type = "prepare", operationId = plan.Id, targetVersion = plan.TargetVersion,
                currentVersion = version, release = plan.Release, previousRelease = plan.PreviousRelease }, deadline.Token);
            await worker.ExpectAsync("ready", deadline.Token);
            return worker;
        }
        catch (Exception e)
        {
            await worker.DisposeAsync();
            if (e is IOException or JsonException or InvalidOperationException || e is OperationCanceledException && !token.IsCancellationRequested)
                throw new SchoolUpdateException("WORKER_UNAVAILABLE", true);
            throw;
        }
    }
    private async Task SendAsync(object message, CancellationToken token)
    {
        await process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(message, Protocol.Json).AsMemory(), token);
        await process.StandardInput.FlushAsync(token);
    }
    private async Task ExpectAsync(string type, CancellationToken token)
    {
        var line = new StringBuilder();
        char[] next = new char[1];
        while (line.Length < 1024 && await process.StandardOutput.ReadAsync(next.AsMemory(), token) != 0 && next[0] != '\n')
            line.Append(next[0]);
        if (line.Length >= 1024 || JsonNode.Parse(line.ToString()) is not JsonObject result ||
            !Guid.TryParseExact(result["operationId"]?.GetValue<string>(), "D", out var rid) || rid != id)
            throw new SchoolUpdateException("INVALID_WORKER_RESPONSE");
        if (result["type"]?.GetValue<string>() != type)
        {
            string reason = result["reason"]?.GetValue<string>() ?? "INVALID_WORKER_RESPONSE";
            throw new SchoolUpdateException(reason, type == "ready" && reason is ("DOWNLOAD_FAILED" or "BUSY"));
        }
    }
    public async Task ApplyAsync(CancellationToken token)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(4));
        try
        {
            await SendAsync(new { type = "apply", operationId = id }, deadline.Token);
            await ExpectAsync("accepted", deadline.Token);
        }
        catch (Exception e) when (e is IOException or JsonException or InvalidOperationException ||
            e is OperationCanceledException && !token.IsCancellationRequested)
        { throw new SchoolUpdateException("HANDOFF_UNCONFIRMED"); }
    }
    public ValueTask DisposeAsync()
    {
        try { process.StandardInput.Close(); }
        catch (IOException) { }
        finally { process.Dispose(); }
        return ValueTask.CompletedTask;
    }
    internal static ManagedUpdateReceipt? ReadResult(Guid id)
    {
        string path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "NPDownloader", "Managed", "results", id.ToString("D") + ".json");
        if (!File.Exists(path) || new FileInfo(path).Length > 4096) return null;
        var r = JsonNode.Parse(File.ReadAllText(path));
        if (!Guid.TryParseExact(r?["operationId"]?.GetValue<string>(), "D", out var op) || op != id ||
            r?["state"]?.GetValue<string>() is not ("FAILED" or "AWAITING_RESTART")) return null;
        string? reason = r["reason"]?.GetValue<string>();
        if (reason is not null && (reason.Length is < 1 or > 64 || !reason.All(c => c is >= 'A' and <= 'Z' or >= '0' and <= '9' or '_'))) return null;
        return new(id, r["state"]!.GetValue<string>(), reason);
    }
}

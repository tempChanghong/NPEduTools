using System.Text.Json;
using NPEduTools.Contracts;

namespace NPEduTools.Host;

public sealed record LaunchDocument(int SchemaVersion, LaunchSettings Settings, List<LaunchExecution> Executions);

/// <summary>Called only while LaunchService owns its gate. No external mutation precedes a durable intent.</summary>
public sealed class LaunchStore
{
    private readonly string _path;
    private readonly FileStream _ownership;
    private bool _recovered;
    public string? Warning { get; private set; }
    public LaunchDocument Document { get; private set; } = new(1, new(0, null), []);

    public LaunchStore(string directory)
    {
        Directory.CreateDirectory(directory);
        _path = Path.Combine(directory, "classisland.json");
        // Distinct test Hosts must never share business storage, even when their pipe names differ.
        _ownership = new FileStream(Path.Combine(directory, "writer.lock"), FileMode.OpenOrCreate,
            FileAccess.ReadWrite, FileShare.None);
        try
        {
            if (!File.Exists(_path))
            {
                if (File.Exists(_path + ".bak")) RecoverBackup();
            }
            else
            {
                try { Document = Read(_path); }
                catch (Exception ex) when (ex is IOException or JsonException or InvalidDataException)
                {
                    File.Copy(_path, _path + ".corrupt-" + Guid.NewGuid().ToString("N"));
                    RecoverBackup();
                }
            }
        }
        catch { _ownership.Dispose(); throw; }
    }

    private void RecoverBackup()
    {
        Document = Read(_path + ".bak");
        _recovered = true;
        Warning = "数据文件异常，已读取备份供查看。启动已停用，请核实可能丢失的操作记录；原文件已保留（若存在）。";
    }

    private static LaunchDocument Read(string path)
    {
        if (new FileInfo(path).Length > 2 * 1024 * 1024) throw new InvalidDataException("Store too large.");
        var result = JsonSerializer.Deserialize<LaunchDocument>(File.ReadAllBytes(path), Protocol.Json);
        if (result is null || result.SchemaVersion != 1 || result.Settings is null || result.Settings.Revision < 0 ||
            result.Settings.ExecutablePath?.Length > 2048 || result.Executions is null || result.Executions.Count > 200 ||
            result.Executions.Any(x => x is null || x.RequestId == Guid.Empty || string.IsNullOrWhiteSpace(x.ExecutablePath) ||
                x.ExecutablePath.Length > 2048 || x.Outcome is not ("Running" or "Succeeded" or "Failed" or "TimedOut" or "Unknown")) ||
            result.Executions.Select(x => x.RequestId).Distinct().Count() != result.Executions.Count)
            throw new InvalidDataException("Unsupported or invalid launch store.");
        return result;
    }

    public void Save(LaunchDocument next)
    {
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(next, Protocol.Json);
        if (bytes.Length > 2 * 1024 * 1024) throw new InvalidDataException("Store too large.");
        string temporary = _path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }
            if (File.Exists(_path) && !_recovered) File.Replace(temporary, _path, _path + ".bak");
            else File.Move(temporary, _path, overwrite: true);
            _recovered = false;
            Document = next;
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    public void Dispose() => _ownership.Dispose();
}

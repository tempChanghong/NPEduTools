using System.Text.Json.Nodes;

namespace NPEduTools.Integrations.Npep;

// Device-local write-ahead outbox. One owner (the control loop); no replay after interruption.
internal sealed class NpepControlJournal
{
    private readonly string _path;
    internal JsonObject Entries { get; private set; } = new();
    internal NpepControlJournal(string directory)
    {
        _path = Path.Combine(directory, "runtime-control-outbox.json");
        if (File.Exists(_path + ".pending") || Directory.Exists(_path)) throw new NpepException("STORAGE_UNAVAILABLE");
        if (!File.Exists(_path)) return;
        if (new FileInfo(_path).Length > 4 * 1024 * 1024) throw new NpepException("STORAGE_UNAVAILABLE");
        Entries = NpepProtocol.Parse(File.ReadAllBytes(_path));
        if (Entries.Count > 256) throw new NpepException("STORAGE_UNAVAILABLE");
        foreach (var entry in Entries)
        {
            if (!Guid.TryParseExact(entry.Key, "D", out _) || entry.Value is not JsonObject item || item["operation"] is not JsonObject op || op.Text("operationId") != entry.Key)
                throw new NpepException("STORAGE_UNAVAILABLE");
            NpepRuntimeProtocol.Validate("operation", op);
        }
    }
    internal void Save(string id, JsonObject item)
    {
        var next = Entries.Copy(); next[id] = item.DeepClone();
        if (next.Count > 256) throw new NpepException("STORAGE_UNAVAILABLE");
        byte[] bytes = System.Text.Encoding.UTF8.GetBytes(next.ToJsonString());
        if (bytes.Length > 4 * 1024 * 1024) throw new NpepException("STORAGE_UNAVAILABLE");
        using (var file = new FileStream(_path + ".pending", FileMode.CreateNew, FileAccess.Write, FileShare.None)) { file.Write(bytes); file.Flush(true); }
        File.Move(_path + ".pending", _path, true); Entries = next;
    }
}

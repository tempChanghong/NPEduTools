using System.Text.Json.Nodes;

namespace NPEduTools.Integrations.Npep;

// Single in-flight operation. An intent is flushed BEFORE a grant or local command.
// An intent without a durable result is UNKNOWN after any interruption, never replayed.
internal sealed class NpepPlanJournal
{
    private readonly string _path;
    internal JsonObject? State { get; private set; }
    internal NpepPlanJournal(string directory)
    {
        _path = Path.Combine(directory, "exam-plan-journal.json");
        if (File.Exists(_path + ".pending")) throw new InvalidDataException("Incomplete plan journal");
        if (!File.Exists(_path)) return;
        if (new FileInfo(_path).Length > 131072) throw new InvalidDataException();
        State = NpepProtocol.Parse(File.ReadAllBytes(_path));
        if (State.Number("version") != 1 || !Guid.TryParse(State.Text("operationId"), out _) ||
            State.Text("stage") is not ("prepare" or "start") || State["context"] is not JsonObject ||
            State["result"] is not (null or JsonObject)) throw new InvalidDataException();
    }
    internal void Save(JsonObject state)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(state.ToJsonString());
        if (bytes.Length > 131072) throw new InvalidDataException();
        using (var file = new FileStream(_path + ".pending", FileMode.CreateNew, FileAccess.Write, FileShare.None))
        { file.Write(bytes); file.Flush(true); }
        File.Move(_path + ".pending", _path, true);
        State = (JsonObject)state.DeepClone();
    }
}

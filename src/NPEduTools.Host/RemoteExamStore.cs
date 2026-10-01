using System.Text.Json;
using NPEduTools.Contracts;

namespace NPEduTools.Host;

/// <summary>Independent, write-ahead N3 pause/operation history; never edits classroom-mode.json.</summary>
public sealed class RemoteExamStore : IRemoteExamStore
{
    public const int Capacity = 256;
    private readonly string _path;
    private RemoteExamDocument _state = new(Operations: []);
    public RemoteExamDocument State => Volatile.Read(ref _state);

    public RemoteExamStore(string directory)
    {
        _path = Path.Combine(directory, "remote-exam-runtime.json");
        try
        {
            // A remaining write intent is ambiguous even if the older document still parses.
            if (File.Exists(_path + ".pending") || Directory.Exists(_path + ".pending") || Directory.Exists(_path))
                throw new InvalidDataException();
            if (!File.Exists(_path)) return;
            if (new FileInfo(_path).Length > 2 * 1024 * 1024) throw new InvalidDataException();
            var state = JsonSerializer.Deserialize<RemoteExamDocument>(File.ReadAllText(_path), Protocol.Json)
                ?? throw new InvalidDataException();
            Validate(state);
            _state = state;
            if (state.Operations!.Any(x => InProgress(x.Outcome)))
                Save(state with
                {
                    AutomaticPaused = true,
                    PauseOperationId = state.Operations!.Single(x => InProgress(x.Outcome)).Intent.OperationId,
                    Operations = state.Operations!.Select(x => InProgress(x.Outcome)
                        ? x with { Outcome = "UNKNOWN", Reason = "HOST_INTERRUPTED", UpdatedAt = DateTimeOffset.UtcNow }
                        : x).ToArray()
                });
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or JsonException or OverflowException)
        { FailClosed(); }
    }

    public void Save(RemoteExamDocument state)
    {
        if (State.StorageError is not null) throw new IOException("N3 storage is unavailable.");
        state = state with { Revision = checked(State.Revision + 1) };
        Validate(state);
        try
        {
            byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(state, Protocol.Json);
            if (bytes.Length > 2 * 1024 * 1024) throw new IOException("N3 journal exceeds its size limit.");
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            using (var stream = new FileStream(_path + ".pending", FileMode.Create, FileAccess.Write, FileShare.None))
            {
                stream.Write(bytes);
                stream.Flush(true);
            }
            File.Move(_path + ".pending", _path, true);
            Volatile.Write(ref _state, state);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { FailClosed(); throw; }
    }

    private void FailClosed() => Volatile.Write(ref _state,
        State with { AutomaticPaused = true, StorageError = "STORAGE_UNAVAILABLE" });

    public static bool InProgress(string outcome) => outcome is "RECEIVED" or "CHECKING" or "RUNNING" or "WAITING_LOCAL";
    public static bool Unresolved(RemoteExamEntry entry) => entry.ResolvedAt is null &&
        (InProgress(entry.Outcome) || entry.Outcome is "UNKNOWN" or "PARTIAL");

    private static void Validate(RemoteExamDocument state)
    {
        if (state.Version != 1 || state.Revision < 0 || state.StorageError is not null ||
            state.Operations is null || state.Operations.Length > Capacity ||
            state.Operations.Any(x => x is null || x.Intent is null || x.Intent.OperationId == Guid.Empty ||
                x.Intent.ExpectedRuntimeRevision < 0 || x.Intent.ExpectedModeRevision < 0 ||
                x.Intent.Target is not ("Exam" or "Daily") || x.Intent.Target == "Daily" && !x.Intent.SwitchMode ||
                x.Outcome is not ("RECEIVED" or "CHECKING" or "RUNNING" or "WAITING_LOCAL" or "SUCCEEDED" or "REJECTED" or "PARTIAL" or "UNKNOWN") ||
                x.Step is not ("Check" or "PauseRecording" or "PrepareExam" or "SetStartup" or "CloseClassIsland" or "CloseExam" or "StartClassIsland" or "Verify") ||
                x.UpdatedAt == default || x.Reason is { Length: > 128 } ||
                (x.ResolvedAt is not null && (InProgress(x.Outcome) || x.ResolvedAt < x.UpdatedAt)) ||
                (x.LocallyEndedAt is not null && (x.ResolvedAt is null || x.LocallyEndedAt < x.UpdatedAt)) ||
                (x.PauseEstablished && x.Configuration is null) ||
                (x.Outcome == "SUCCEEDED" && !x.PauseEstablished) ||
                (x.Configuration is { } c && (string.IsNullOrWhiteSpace(c.ClassIslandPath) || c.ClassIslandPath.Length > 2048 ||
                    string.IsNullOrWhiteSpace(c.ExamAwarePath) || c.ExamAwarePath.Length > 2048 ||
                    c.ClassIslandRevision < 0 || c.ExamAwareRevision < 0))) ||
            state.Operations.Select(x => x.Intent.OperationId).Distinct().Count() != state.Operations.Length ||
            state.Operations.Count(Unresolved) > 1 ||
            (state.AutomaticPaused != (state.PauseOperationId is not null)) ||
            (state.PauseOperationId is { } owner && (!state.AutomaticPaused ||
                !state.Operations.Any(x => x.Intent.OperationId == owner && x.LocallyEndedAt is null))) ||
            (!state.AutomaticPaused && state.Operations.Any(x => x.ResolvedAt is null &&
                (x.PauseEstablished || x.Outcome is "UNKNOWN" or "PARTIAL"))))
            throw new InvalidDataException("Invalid N3 execution journal.");
    }
}

namespace NPEduTools.Contracts;
// Local-only bounded image channel; never camera or audio data.
public sealed record StudySeatingState(string State, string? Version, int Revision, string? Hash, int Chunks, int ByteCount, int Width, int Height);
public sealed record StudySeatingRead(string Version, int Revision, string Hash, int Index);
public sealed record StudySeatingChunk(int Revision, string Hash, int Index, int Count, string Data);
public sealed record StudySeatingPulse(Guid InstanceId, Guid AppInstanceId, long Sequence, string Version,
    int Revision, string? AppliedHash, bool Visible, int? RenderAgeMs);
// PNG bytes use an owned, bounded local transfer file; the Pipe keeps its 64 KiB limit.
public sealed record StudySeatingUploadCommand(Guid TicketId, Guid UploadId, string Version, int ExpectedRevision,
    string Hash, int ByteCount, int Width, int Height);
public sealed record StudySeatingUploadState(Guid TicketId, string Version, string ClassId, string ClassName,
    int Revision, string? Hash, string State);

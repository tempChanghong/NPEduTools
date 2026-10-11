namespace NPEduTools.Contracts;
// Local-only bounded image channel; never camera or audio data.
public sealed record StudySeatingState(string State, string? Version, int Revision, string? Hash, int Chunks, int ByteCount, int Width, int Height);
public sealed record StudySeatingRead(string Version, int Revision, string Hash, int Index);
public sealed record StudySeatingChunk(int Revision, string Hash, int Index, int Count, string Data);
public sealed record StudySeatingPulse(Guid InstanceId, Guid AppInstanceId, long Sequence, string Version,
    int Revision, string? AppliedHash, bool Visible, int? RenderAgeMs);

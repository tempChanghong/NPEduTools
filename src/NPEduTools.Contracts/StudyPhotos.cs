using System.Text.Json.Nodes;
namespace NPEduTools.Contracts;
public sealed record StudyPhotoState(string State, string? Reason, int Pending,
    string? LastCapturedAt = null, string? LastStoredAt = null);
// Local queue only; image bytes travel through the opt-in photo endpoint, never noise-exchange or Named Pipe status.
public sealed record StudyPhotoPacket(string Scope, string Camera, string Microphone, JsonObject Metadata, byte[] Bytes);
public sealed record StudyCameraFrame(byte[] Bytes, int Width, int Height);

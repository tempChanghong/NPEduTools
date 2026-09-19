namespace NPEduTools.Contracts;

public sealed record ExamAwareStatus(string? ExecutablePath, long Revision, string BridgeState,
    string Message, string? AppVersion, bool? AutoStartRegistered, bool? Packaged, int? Port);
public sealed record ExamAwarePairing(int Version, string Host, int Port, string Key);
public sealed record ExamAwareHello(int Version, string Type, string Nonce, string Proof);
public sealed record ExamAwareFrame(int Version, string Type, long Sequence, string Payload, string Proof);
public sealed record ExamAwareSample(string Name, string Version, string Platform, bool Packaged, bool? AutoStartRegistered);

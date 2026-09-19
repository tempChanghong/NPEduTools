namespace NPEduTools.Contracts;

public sealed record ExamAwareStatus(string? ExecutablePath, long Revision, string BridgeState,
    string Message, string? AppVersion, bool? AutoStartRegistered, bool? Packaged, int? Port, ExamAwareQuitState? Quit = null,
    bool CanSetAutoStart = false, ExamAwareAutoStartState? AutoStartChange = null);
public sealed record ExamAwareAutoStartState(Guid RequestId, string State, string Message, bool Requested, bool? Registered = null);
public sealed record ExamAwareQuitState(Guid RequestId, string State, string Message, int ProcessId);
public sealed record ExamAwarePairing(int Version, string Host, int Port, string Key);
public sealed record ExamAwareHello(int Version, string Type, string Nonce, string Proof);
public sealed record ExamAwareFrame(int Version, string Type, long Sequence, string Payload, string Proof);
public sealed record ExamAwareSample(string Name, string Version, string Platform, bool Packaged, bool? AutoStartRegistered, int ProcessId = 0, bool CanSetAutoStart = false);
public sealed record ExamAwareQuitCommand(Guid RequestId, string Action, long IssuedAt, long ExpiresAt);
public sealed record ExamAwareQuitAck(Guid RequestId, string State);
public sealed record ExamAwareAutoStartCommand(Guid RequestId, string Action, long IssuedAt, long ExpiresAt, bool Enabled);
public sealed record ExamAwareAutoStartAck(Guid RequestId, string State, bool? Registered = null);

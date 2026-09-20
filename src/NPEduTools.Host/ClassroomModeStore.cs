using System.Text.Json;
using NPEduTools.Contracts;

namespace NPEduTools.Host;

/// <summary>The durable pause is loaded before the recording scheduler starts.</summary>
public sealed class ClassroomModeStore
{
    private readonly string _path;
    private ClassroomModeState _state = new();
    public ClassroomModeState State => Volatile.Read(ref _state);
    public ClassroomModeStore(string directory)
    {
        _path = Path.Combine(directory, "classroom-mode.json");
        try
        {
            if (!File.Exists(_path)) return;
            var state = JsonSerializer.Deserialize<ClassroomModeState>(File.ReadAllText(_path), Protocol.Json)
                ?? throw new InvalidDataException("模式记录为空。");
            if (state.Revision < 0 || state.Mode is not ("Unconfigured" or "Daily" or "Exam") ||
                state.Phase is not ("Idle" or "Checking" or "Switching" or "Running" or "Incomplete") ||
                state.RecentRequests is null || state.RecentRequests.Length > 64 ||
                (state.Mode == "Exam" && !state.AutomaticPaused) ||
                (state.Phase == "Incomplete" && !state.AutomaticPaused) ||
                (state.Phase == "Switching" && state.Recovery is null) ||
                (state.Phase == "Running" && (state.Runtime is null || !state.AutomaticPaused)) ||
                (state.Runtime is { } runtime && (state.Recovery is null || runtime.Target is not ("Daily" or "Exam") ||
                    runtime.Startup is null || runtime.StartupCheckedAt == default ||
                    runtime.Startup.ClassIslandEnabled != (runtime.Target == "Daily") ||
                    runtime.Startup.ExamAwareEnabled != (runtime.Target == "Exam") || !state.AutomaticPaused)) ||
                (state.Recovery is { } recovery && (recovery.PreviousMode is not ("Unconfigured" or "Daily" or "Exam") ||
                    (recovery.PreviousMode == "Exam" && !recovery.PreviousPause) || recovery.Startup is null ||
                    string.IsNullOrWhiteSpace(recovery.Startup.ClassIslandPath) || string.IsNullOrWhiteSpace(recovery.Startup.ExamAwarePath) ||
                    recovery.Startup.ClassIslandRevision < 0 || recovery.Startup.ExamAwareRevision < 0)))
                throw new InvalidDataException("模式记录内容不完整。");
            _state = state with { Actual = null, CheckedAt = null, MatchesMode = null };
            if (state.Busy)
                Save(_state with { Phase = "Incomplete", AutomaticPaused = true,
                    Message = "上次切换被中断。自动录课保持暂停，请核实后恢复或重新切换。" });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            _state = new(Phase: "Unavailable", AutomaticPaused: true,
                Message: "无法读取模式记录，自动录课保持暂停。请检查 classroom-mode.json，原文件已保留。");
        }
    }
    public void Save(ClassroomModeState state)
    {
        state = state with { Revision = checked(State.Revision + 1) };
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            string temp = _path + ".pending";
            using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(stream, state, Protocol.Json);
                stream.Flush(true);
            }
            File.Move(temp, _path, true);
            Volatile.Write(ref _state, state);
        }
        catch
        {
            Volatile.Write(ref _state, State with { Phase = "Unavailable", AutomaticPaused = true,
                Message = "模式记录无法保存，自动录课保持暂停。请检查磁盘后重启后台并核实状态。" });
            throw;
        }
    }
}

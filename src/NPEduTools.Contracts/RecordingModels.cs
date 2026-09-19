using System.Text.Json;

namespace NPEduTools.Contracts;

public sealed record RecordingDisplay(string Id, string Label, int Left, int Top, int Width, int Height, bool Primary);
public sealed record RecordingDevice(string Id, string Label);
public sealed record RecordingEnvironment(bool Ready, string? Error, RecordingDisplay[] Displays,
    RecordingDevice[] Microphones, RecordingDevice[] Speakers);
public sealed record RecordingOptions(string Display, string OutputDirectory, int FramesPerSecond = 8,
    int MaximumHeight = 1080, bool SystemAudio = true, bool Microphone = true,
    string SpeakerId = "default", string MicrophoneId = "default");
public sealed record RecorderControl(string Owner, Guid SessionId, string OccurrenceId, long HardDeadline, long LeaseDeadline)
{
    public bool Matches(RecorderControl? other) => other is not null && Owner == other.Owner && SessionId == other.SessionId && OccurrenceId == other.OccurrenceId;
}
public sealed record RecorderCommand(string Action, RecordingOptions? Options = null, RecorderControl? Control = null, Guid? ClientId = null);
public sealed record AutomaticRecordingCommand(string Action, Guid ClientId, RecordingOptions? Options = null);
public sealed record RecordingExecution(string Key, Guid ProfileId, DateOnly Date, bool Fixed, DateTimeOffset Start,
    DateTimeOffset End, string Subject, Guid SessionId, string Phase, string Reason, string? OutputFile = null, string? RecoveryDirectory = null);
public sealed record AutomaticRecordingState(bool Enabled, Guid ClientId, string Message, DateOnly? SkipDate,
    RecordingExecution[] Recent, string? Error = null);
public sealed record RecordingState(string Phase, string Message, double Seconds = 0, long Frames = 0,
    long Bytes = 0, long DroppedFrames = 0, long AudioOverruns = 0, string? OutputFile = null,
    string? RecoveryDirectory = null, string? Error = null, RecorderControl? Control = null)
{
    public bool Active => Phase is "Starting" or "Recording" or "Pausing" or "Paused" or "Saving";
    public bool Busy => Phase is "Starting" or "Pausing" or "Saving";
}

public static class RecordingContract
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    public static string? Validate(RecordingOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.Display) || options.Display.Length > 256) return "请选择要录制的屏幕。";
        if (options.FramesPerSecond is not (8 or 15)) return "帧率请选择 8 或 15。";
        if (options.MaximumHeight is not (720 or 1080)) return "画质请选择 720p 或 1080p。";
        if (string.IsNullOrWhiteSpace(options.OutputDirectory) || options.OutputDirectory.Length > 1024 ||
            !Path.IsPathFullyQualified(options.OutputDirectory) || options.OutputDirectory.Any(char.IsControl)) return "请选择完整的保存目录。";
        if (string.IsNullOrWhiteSpace(options.SpeakerId) || options.SpeakerId.Length > 1024 ||
            string.IsNullOrWhiteSpace(options.MicrophoneId) || options.MicrophoneId.Length > 1024) return "音频设备选项无效。";
        return null;
    }
    public static (int Width, int Height) OutputSize(int width, int height, int maximumHeight)
    {
        if (width < 2 || height < 2 || width > 8192 || height > 8192 || (long)width * height > 33554432)
            throw new ArgumentOutOfRangeException(nameof(width), "采集区域尺寸不受支持。");
        double scale = Math.Min(1, Math.Min(maximumHeight / (double)height, maximumHeight * 16.0 / 9 / width));
        return (Math.Max(2, (int)(width * scale) / 2 * 2), Math.Max(2, (int)(height * scale) / 2 * 2));
    }
}

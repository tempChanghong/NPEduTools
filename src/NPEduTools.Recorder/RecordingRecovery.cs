using System.Text.Json;
using System.Text.RegularExpressions;
using NPEduTools.Contracts;

namespace NPEduTools.Recorder;

internal static partial class RecordingRecovery
{
    public static async Task<string> RecoverAsync(string directory)
    {
        directory = Path.GetFullPath(directory);
        if (File.GetAttributes(directory).HasFlag(FileAttributes.ReparsePoint))
            throw new InvalidDataException("请选择实际的片段目录。");
        string manifest = Path.Combine(directory, "session.json");
        if (new FileInfo(manifest).Length > 1024 * 1024) throw new InvalidDataException("片段清单过大。");
        using var document = JsonDocument.Parse(await File.ReadAllTextAsync(manifest));
        var root = document.RootElement;
        if (root.GetProperty("version").GetInt32() != 1) throw new InvalidDataException("不支持的片段清单版本。");
        int width = root.GetProperty("width").GetInt32(), height = root.GetProperty("height").GetInt32();
        if (width is < 2 or > 1920 || height is < 2 or > 1080) throw new InvalidDataException("片段尺寸无效。");
        var options = root.GetProperty("options").Deserialize<RecordingOptions>(RecordingContract.Json)!;
        var parts = root.GetProperty("parts").EnumerateArray().Select(p => p.GetString()!).ToArray();
        if (parts.Length is < 1 or > 9999 || parts.Distinct(StringComparer.OrdinalIgnoreCase).Count() != parts.Length)
            throw new InvalidDataException("片段清单为空或重复。");
        long bytes = 0;
        foreach (var part in parts)
        {
            if (!PartName().IsMatch(part)) throw new InvalidDataException("清单包含无效片段名称。");
            var file = new FileInfo(Path.Combine(directory, part));
            if (!file.Exists || file.Length == 0 || file.Attributes.HasFlag(FileAttributes.ReparsePoint))
                throw new InvalidDataException("片段不存在、为空或是链接：" + part);
            bytes += file.Length;
        }
        if (MediaTools.FreeSpace(directory) < bytes + 64L * 1024 * 1024)
            throw new IOException("剩余空间不足，请释放空间后恢复。");
        string name = "恢复-" + Guid.NewGuid().ToString("N"), list = Path.Combine(directory, name + ".txt");
        string temporary = Path.Combine(directory, name + ".partial.mp4"), output = Path.Combine(directory, name + ".mp4");
        await File.WriteAllLinesAsync(list, parts.Select(p => $"file '{p}'"));
        await MediaTools.RunAsync("ffmpeg", ["-hide_banner", "-loglevel", "error", "-n", "-f", "concat", "-safe", "1", "-i", list,
            "-map", "0", "-c", "copy", "-movflags", "+faststart", temporary], directory, TimeSpan.FromMinutes(2));
        await MediaTools.VerifyAsync(temporary, options.SystemAudio || options.Microphone, width, height);
        // Recovery is exceptional: decode every recovered frame before advertising a usable file.
        await MediaTools.RunAsync("ffmpeg", ["-v", "error", "-xerror", "-i", temporary, "-f", "null", "-"], directory, TimeSpan.FromMinutes(10));
        File.Move(temporary, output);
        return output;
    }

    [GeneratedRegex(@"\Apart-[0-9]{4}\.mkv\z", RegexOptions.CultureInvariant)]
    private static partial Regex PartName();
}

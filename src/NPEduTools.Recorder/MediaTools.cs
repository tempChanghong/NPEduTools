using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using NAudio.CoreAudioApi;
using NPEduTools.Contracts;
using Forms = System.Windows.Forms;

namespace NPEduTools.Recorder;

internal static class MediaTools
{
    public static string Tool(string name) => Path.Combine(AppContext.BaseDirectory, "Tools", name + ".exe");
    public static RecordingEnvironment Probe()
    {
        var displays = Forms.Screen.AllScreens.Select((s, i) => new RecordingDisplay(s.DeviceName,
            $"屏幕 {i + 1}{(s.Primary ? "（主屏）" : "")} · {s.Bounds.Width} × {s.Bounds.Height}",
            s.Bounds.X, s.Bounds.Y, s.Bounds.Width, s.Bounds.Height, s.Primary)).ToArray();
        RecordingDevice[] microphones = [], speakers = [];
        try
        {
            using var devices = new MMDeviceEnumerator();
            microphones = Devices(devices, DataFlow.Capture);
            speakers = Devices(devices, DataFlow.Render);
        }
        catch (Exception error) when (error is System.Runtime.InteropServices.COMException or InvalidOperationException) { }
        bool ready = File.Exists(Tool("ffmpeg")) && File.Exists(Tool("ffprobe"));
        return new(ready, ready ? null : "尚未安装录制组件。请按程序包内 RECORDING-TOOLS-INSTALL.md 安装 FFmpeg，然后重新检测。", displays, microphones, speakers);
    }
    private static RecordingDevice[] Devices(MMDeviceEnumerator enumerator, DataFlow flow)
    {
        var result = new List<RecordingDevice>();
        foreach (var device in enumerator.EnumerateAudioEndPoints(flow, DeviceState.Active))
        {
            using (device) result.Add(new(device.ID, device.FriendlyName));
        }
        return result.ToArray();
    }
    public static ProcessStartInfo StartInfo(string tool, IEnumerable<string> arguments, string? directory = null)
    {
        var info = new ProcessStartInfo(Tool(tool))
        {
            UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            WorkingDirectory = directory ?? AppContext.BaseDirectory
        };
        foreach (var argument in arguments) info.ArgumentList.Add(argument);
        return info;
    }
    public static async Task<string> RunAsync(string tool, IEnumerable<string> arguments, string directory, TimeSpan timeout)
    {
        using var process = Process.Start(StartInfo(tool, arguments, directory)) ?? throw new IOException("无法启动媒体处理进程。");
        using var job = new ProcessJob();
        job.Add(process);
        var output = process.StandardOutput.ReadToEndAsync();
        var errors = process.StandardError.ReadToEndAsync();
        try { await process.WaitForExitAsync().WaitAsync(timeout); }
        catch { if (!process.HasExited) process.Kill(true); await process.WaitForExitAsync(); throw; }
        string error = await errors;
        if (process.ExitCode != 0) throw new IOException($"媒体处理失败（{process.ExitCode}）：{error[..Math.Min(error.Length, 700)]}");
        return await output;
    }
    public static async Task<double> VerifyAsync(string path, bool audio, int width, int height)
    {
        string result = await RunAsync("ffprobe", ["-v", "error", "-show_entries", "format=duration:stream=codec_type,codec_name,width,height", "-of", "json", path],
            Path.GetDirectoryName(path)!, TimeSpan.FromSeconds(15));
        using var json = JsonDocument.Parse(result);
        var streams = json.RootElement.GetProperty("streams").EnumerateArray().ToArray();
        bool video = streams.Any(s => s.GetProperty("codec_type").GetString() == "video" &&
            s.GetProperty("codec_name").GetString() == "h264" && s.GetProperty("width").GetInt32() == width && s.GetProperty("height").GetInt32() == height);
        bool sound = streams.Any(s => s.GetProperty("codec_type").GetString() == "audio" && s.GetProperty("codec_name").GetString() == "aac");
        if (!video || (audio && !sound) ||
            !double.TryParse(json.RootElement.GetProperty("format").GetProperty("duration").GetString(), CultureInfo.InvariantCulture, out double duration) ||
            !double.IsFinite(duration) || duration <= 0) throw new InvalidDataException("成片检查未通过，原始片段已保留。");
        return duration;
    }
    public static long FreeSpace(string directory)
    {
        try { return new DriveInfo(Path.GetPathRoot(directory)!).AvailableFreeSpace; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException) { return long.MaxValue; }
    }
}

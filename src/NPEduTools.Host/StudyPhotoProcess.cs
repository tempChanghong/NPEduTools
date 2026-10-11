using System.Buffers.Binary;
using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text.Json;
using NPEduTools.Contracts;
namespace NPEduTools.Host;
[SupportedOSPlatform("windows")]
public static class StudyPhotoProcess
{
    public static async Task<StudyCameraFrame> CaptureAsync(string device, CancellationToken token)
    {
        string executable = Path.Combine(AppContext.BaseDirectory, "Camera", "NPEduTools.Camera.exe");
        if (!File.Exists(executable)) throw new IOException("PHOTO_WORKER_UNAVAILABLE");
        return await RunAsync(executable, device, false, token);
    }
    internal static async Task<StudyCameraFrame> RunAsync(string executable, string device, bool synthetic, CancellationToken token)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token); deadline.CancelAfter(TimeSpan.FromSeconds(5));
        using var job = new NPEduTools.Recorder.ProcessJob();
        using var child = new Process { StartInfo = new(executable) { UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true } };
        if (synthetic) child.StartInfo.ArgumentList.Add("--synthetic");
        child.Start();
        try
        {
            job.Add(child);
            // Drain a bounded fixed-code error channel without logging identifiers or pixels.
            var errors = DrainAsync(child.StandardError.BaseStream, deadline.Token);
            await child.StandardInput.BaseStream.WriteAsync(JsonSerializer.SerializeToUtf8Bytes(new { device }), deadline.Token);
            child.StandardInput.Close();
            var output = child.StandardOutput.BaseStream; var header = new byte[12]; await output.ReadExactlyAsync(header, deadline.Token);
            int width = BinaryPrimitives.ReadInt32LittleEndian(header), height = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(4)), length = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(8));
            if (width is <= 0 or > 1280 || height is <= 0 or > 1280 || length is <= 0 or > 262144) throw new InvalidDataException();
            var bytes = new byte[length]; await output.ReadExactlyAsync(bytes, deadline.Token);
            var tail = new byte[1]; if (await output.ReadAsync(tail, deadline.Token) != 0) throw new InvalidDataException();
            await child.WaitForExitAsync(deadline.Token); await errors;
            if (child.ExitCode != 0 || bytes.Length < 4 || bytes[0] != 255 || bytes[1] != 216 || bytes[^2] != 255 || bytes[^1] != 217) throw new InvalidDataException();
            return new(bytes, width, height);
        }
        finally { if (!child.HasExited) { child.Kill(true); await child.WaitForExitAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(2)); } }
    }
    private static async Task DrainAsync(Stream stream, CancellationToken token)
    {
        var block = new byte[1024]; int total = 0, n;
        while ((n = await stream.ReadAsync(block, token)) > 0) if ((total += n) > 4096) throw new InvalidDataException();
    }
}

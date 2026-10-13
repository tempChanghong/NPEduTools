using System.IO;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NPEduTools.App;

internal static partial class Program
{
    private static void AssertReminderPlayback(StudyReminderAudio audio)
    {
        // Decode every shipped cue without opening an audio endpoint. Missing, corrupt,
        // silent or unsupported assets must fail even on a headless Windows runner.
        foreach (string cue in new[] { "exam-alert", "exam-start", "exam-end" })
        {
            string path = Path.Combine(AppContext.BaseDirectory, "Assets", "Study", "Audio", cue + ".mp3");
            using var reader = new MediaFoundationReader(path);
            Assert(reader.WaveFormat.BitsPerSample == 16 && reader.WaveFormat.Channels == 2,
                $"unexpected reminder PCM format: {cue}");
            var buffer = new byte[8192];
            long bytes = 0; int peak = 0;
            for (int count; (count = reader.Read(buffer, 0, buffer.Length)) > 0;)
            {
                bytes += count;
                Assert(bytes < 10 * reader.WaveFormat.AverageBytesPerSecond, $"unbounded reminder: {cue}");
                for (int i = 0; i + 1 < count; i += 2)
                    peak = Math.Max(peak, Math.Abs((int)BitConverter.ToInt16(buffer, i)));
            }
            Assert(bytes >= 2 * reader.WaveFormat.AverageBytesPerSecond && peak > 100,
                $"empty, truncated or silent reminder: {cue}");
        }
        Checks.Add("All three shipped MP3 cues decoded fully to non-silent stereo PCM without opening speakers");

        Assert(audio.LastCue == "exam-alert", "preparation requested the wrong audio cue");
        if (audio.Ready) return;
        // Do not hide codec/file failures or sound-device problems on a normal PC.
        bool headless = Environment.GetEnvironmentVariable("GITHUB_ACTIONS") == "true" &&
            audio.Error == "0xC00D11BA";
        if (headless)
        {
            using var devices = new MMDeviceEnumerator();
            headless = devices.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active).Count == 0;
        }
        Assert(headless, $"reminder playback failed: ready={audio.Ready}; cue={audio.LastCue}; error={audio.Error}");
        Checks.Add("CI has no active playback endpoint (0xC00D11BA); decoding and cue timing checked, speaker playback NOT VERIFIED");
    }
}

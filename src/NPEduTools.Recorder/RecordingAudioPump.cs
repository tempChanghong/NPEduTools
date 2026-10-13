using System.Diagnostics;

namespace NPEduTools.Recorder;

// Raw PCM has no timestamps: every written 20 ms block advances FFmpeg's audio
// clock. Fill a short scheduling gap with silence instead of dropping its time
// or aborting the whole lesson. Keep prolonged/repeated overload bounded.
internal sealed class RecordingAudioPacing
{
    private long _blocks, _silenceUntil;
    private readonly Queue<(double At, double Lost)> _gaps = new();
    internal readonly record struct Step(double WaitMs, bool Silence, bool NewGap);

    public Step Next(double elapsedMs)
    {
        double lag = elapsedMs - _blocks * 20;
        bool newGap = false;
        if (lag > 500 && _blocks >= _silenceUntil)
        {
            while (_gaps.TryPeek(out var old) && elapsedMs - old.At >= 30000) _gaps.Dequeue();
            if (lag > 3000 || _gaps.Sum(g => g.Lost) + lag > 5000)
                throw new IOException("音频写入持续落后（超过恢复预算），已停止以保留当前片段。");
            _gaps.Enqueue((elapsedMs, lag));
            _silenceUntil = (long)Math.Ceiling(elapsedMs / 20);
            newGap = true;
        }
        return new(Math.Max(0, -lag), _blocks < _silenceUntil, newGap);
    }
    public void Written() => _blocks++;
}

internal static class RecordingAudioPump
{
    public static async Task RunAsync(Stream output, Action<float[]> mix, Action discard, Action recovered,
        CancellationToken token, Func<double>? elapsed = null,
        Func<double, CancellationToken, Task>? wait = null)
    {
        var clock = Stopwatch.StartNew();
        elapsed ??= () => clock.Elapsed.TotalMilliseconds;
        wait ??= (ms, ct) => Task.Delay(TimeSpan.FromMilliseconds(ms), ct);
        var pacing = new RecordingAudioPacing();
        float[] mixed = new float[1920];
        byte[] bytes = new byte[mixed.Length * sizeof(float)];
        while (!token.IsCancellationRequested)
        {
            var step = pacing.Next(elapsed());
            if (step.WaitMs > 0)
            {
                await wait(step.WaitMs, token);
                // Re-evaluate after a delayed continuation before reading audio.
                step = pacing.Next(elapsed());
            }
            if (step.NewGap) { discard(); recovered(); }
            Array.Clear(mixed);
            if (!step.Silence) mix(mixed);
            Buffer.BlockCopy(mixed, 0, bytes, 0, bytes.Length);
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
            deadline.CancelAfter(TimeSpan.FromSeconds(3));
            await output.WriteAsync(bytes.AsMemory(), deadline.Token);
            pacing.Written();
        }
    }
}

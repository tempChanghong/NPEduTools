using System.Diagnostics;

namespace NPEduTools.Recorder;

// Raw PCM has no timestamps: every written 20 ms block advances FFmpeg's audio
// clock. Fill a short scheduling gap with silence instead of dropping its time
// or aborting the whole lesson. Repeated gaps lower quality, not session lifetime.
internal sealed class RecordingAudioPacing
{
    internal const int RecoveryGraceMs = 60000;
    internal const int QualityWindowMs = 60000;
    internal const int PoorAudioThresholdMs = 30000;
    private long _blocks, _silenceUntil;
    private readonly Queue<(double At, double Lost)> _gaps = new();
    internal readonly record struct Step(double WaitMs, bool Silence, bool NewGap, double GapMs, double RecentLossMs);

    public Step Next(double elapsedMs)
    {
        double lag = elapsedMs - _blocks * 20;
        bool newGap = false;
        if (lag > 500 && _blocks >= _silenceUntil)
        {
            while (_gaps.TryPeek(out var old) && elapsedMs - old.At >= QualityWindowMs) _gaps.Dequeue();
            if (lag > RecoveryGraceMs)
                throw new IOException($"音频写入超过 {RecoveryGraceMs / 1000} 秒仍未恢复，已停止以保留当前片段。");
            _gaps.Enqueue((elapsedMs, lag));
            _silenceUntil = (long)Math.Ceiling(elapsedMs / 20);
            newGap = true;
        }
        return new(Math.Max(0, -lag), _blocks < _silenceUntil, newGap,
            newGap ? lag : 0, newGap ? _gaps.Sum(g => g.Lost) : 0);
    }
    public void Written() => _blocks++;
}

internal sealed class RecordingAudioHealth
{
    private long _writeStarted, _lostMs;
    private sealed record Recovery(long At, long LostMs, double RecentLossMs);
    private Recovery? _recovery;
    public void Writing(long? timestamp = null) => Interlocked.Exchange(ref _writeStarted, timestamp ?? Stopwatch.GetTimestamp());
    public void WriteFinished() => Interlocked.Exchange(ref _writeStarted, 0);
    public void Recovered(RecordingAudioPacing.Step step)
    {
        long lost = Interlocked.Add(ref _lostMs, (long)Math.Ceiling(step.GapMs));
        Volatile.Write(ref _recovery, new(Stopwatch.GetTimestamp(), lost, step.RecentLossMs));
    }
    public string? Message(long? timestamp = null)
    {
        long started = Interlocked.Read(ref _writeStarted);
        long now = timestamp ?? Stopwatch.GetTimestamp();
        if (started != 0 && Stopwatch.GetElapsedTime(started, now) >= TimeSpan.FromSeconds(1))
            return $"正在录制；音频写入变慢，正在等待恢复（宽限至多 {RecordingAudioPacing.RecoveryGraceMs / 1000} 秒）。";
        if (Volatile.Read(ref _recovery) is not { } recovery) return null;
        return recovery.RecentLossMs >= RecordingAudioPacing.PoorAudioThresholdMs &&
            Stopwatch.GetElapsedTime(recovery.At, now) < TimeSpan.FromMilliseconds(RecordingAudioPacing.QualityWindowMs)
            ? $"正在录制；音频曾频繁卡顿，上次恢复时最近一分钟累计约 {recovery.RecentLossMs / 1000:F0} 秒补为静音，请检查负载或降低帧率。"
            : $"正在录制；音频发生过卡顿，缺口按静音补齐，本片段累计约 {recovery.LostMs / 1000.0:F1} 秒。";
    }
}

internal static class RecordingAudioPump
{
    public static async Task RunAsync(Stream output, Action<float[]> mix, Action discard, Action recovered,
        CancellationToken token, Func<double>? elapsed = null,
        Func<double, CancellationToken, Task>? wait = null, RecordingAudioHealth? health = null)
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
            if (step.NewGap) { discard(); recovered(); health?.Recovered(step); }
            Array.Clear(mixed);
            if (!step.Silence) mix(mixed);
            Buffer.BlockCopy(mixed, 0, bytes, 0, bytes.Length);
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
            deadline.CancelAfter(TimeSpan.FromMilliseconds(RecordingAudioPacing.RecoveryGraceMs));
            health?.Writing();
            try { await output.WriteAsync(bytes.AsMemory(), deadline.Token); }
            catch (OperationCanceledException error) when (!token.IsCancellationRequested)
            { throw new IOException($"音频连续 {RecordingAudioPacing.RecoveryGraceMs / 1000} 秒无法写入编码器，已停止以保留当前片段。", error); }
            finally { health?.WriteFinished(); }
            pacing.Written();
        }
    }
}

using System.Diagnostics;

namespace NPEduTools.Recorder;

// Encoded frame progress, independent of whether the desktop's pixels change.
internal sealed class RecordingProgressWatch
{
    internal const int GraceSeconds = 90;
    private long _lastProgress;

    public void Advanced(long timestamp) => Interlocked.Exchange(ref _lastProgress, timestamp);
    public bool Stalled(long timestamp) => Stopwatch.GetElapsedTime(Interlocked.Read(ref _lastProgress), timestamp) > TimeSpan.FromSeconds(GraceSeconds);
}

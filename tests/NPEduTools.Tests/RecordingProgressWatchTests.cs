using System.Diagnostics;
using NPEduTools.Recorder;

namespace NPEduTools.Tests;

public sealed class RecordingProgressWatchTests
{
    private static long At(double seconds) => (long)(seconds * Stopwatch.Frequency);

    [Fact]
    public void EncodedProgressCanRecoverThroughNinetySecondsAndResetForAnotherPart()
    {
        var watch = new RecordingProgressWatch();
        watch.Advanced(At(10));
        Assert.False(watch.Stalled(At(25))); // Former 15-second cutoff.
        Assert.False(watch.Stalled(At(100)));
        Assert.True(watch.Stalled(At(100.001)));
        watch.Advanced(At(105)); // A newly encoded frame restores progress.
        Assert.False(watch.Stalled(At(106)));
        Assert.False(watch.Stalled(At(195)));
        Assert.True(watch.Stalled(At(195.001)));
        watch.Advanced(At(200)); // StartPart resets the same watch on resume.
        Assert.False(watch.Stalled(At(201)));
    }
}

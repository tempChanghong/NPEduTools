using System.Diagnostics;
using NPEduTools.PowerPoint.Diagnostics;

namespace NPEduTools.Tests;

public sealed class TouchInputBufferTests
{
    [Theory]
    [InlineData(5000L, 4000U, 4000L)]
    [InlineData(4294967396L, 4294967246U, 4294967246L)]
    [InlineData(8589934692L, 50U, 8589934642L)]
    public void NativeEventAgeSurvivesDispatchDelayAndTimerWrap(long now, uint original, long expected) =>
        Assert.Equal(expected, TouchInputBuffer.EventTime(now, original));
    private static ShowTarget Target => new("show", 1, 2, 3, 0, 0, 1920, 1080, 96, true, 256, 1, 0, 2, 1, 1);
    private static SnapshotFrame Frame(string status = "Showing", long? at = null) =>
        new(new(DateTimeOffset.UtcNow, status, Targets: [Target]), at ?? Stopwatch.GetTimestamp());
    private static void Observe(TouchInputBuffer buffer, nuint message, long at = 1000, long foreground = 1, int x = 500, SnapshotFrame? frame = null) =>
        buffer.Observe(message, x, 500, 0xff515780, 0, at, frame ?? Frame(), foreground);

    [Theory]
    [InlineData("NotRunning")]
    [InlineData("NoSlideShow")]
    [InlineData("Starting")]
    [InlineData("ProbeTimeout")]
    public void WithoutAShowAllOrdinaryInputBypassesEvenForegroundQueries(string status)
    {
        var buffer = new TouchInputBuffer(); var frame = Frame(status);
        foreach (nuint message in new nuint[] { 0x201, 0x202, 0x200, 0x204, 0x20a }) Assert.False(buffer.Interested(message, frame));
    }

    [Fact]
    public void IdleMoveIsIgnoredAndLosingTheShowStillCancelsAnInFlightGesture()
    {
        var buffer = new TouchInputBuffer();
        Assert.False(buffer.Interested(0x200, Frame())); Assert.True(buffer.Interested(0x201, Frame()));
        Observe(buffer, 0x201); var lost = Frame("NoSlideShow");
        Assert.True(buffer.Interested(0x200, lost)); Observe(buffer, 0x200, 1030, frame: lost);
        Assert.False(buffer.Interested(0x202, lost));
        Assert.True(buffer.TryRead(1100, (_, _, _) => true, out _));
        Assert.True(buffer.TryRead(1100, (_, _, _) => throw new Exception("Cancellation was hit tested"), out var cancellation));
        Assert.Equal("Cancel", cancellation!.Kind);
    }

    [Fact]
    public async Task SlowWindowHitTestCannotHoldUpTheInputProducer()
    {
        var buffer = new TouchInputBuffer(); Observe(buffer, 0x201);
        using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
        var consumer = Task.Run(() => buffer.TryRead(1100, (_, _, _) => { entered.Set(); release.Wait(); return true; }, out _));
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(3)));
            var producer = Task.Run(() => { for (int i = 0; i < 100; i++) Observe(buffer, 0x200, 1001 + i); Observe(buffer, 0x202, 1101); });
            await producer.WaitAsync(TimeSpan.FromSeconds(1));
            Assert.False(consumer.IsCompleted); Assert.Equal(0, buffer.Dropped);
        }
        finally { release.Set(); await consumer; }
        int count = 0; while (buffer.TryRead(1200, (_, _, _) => true, out _)) count++;
        Assert.Equal(101, count);
    }

    [Theory]
    [InlineData("NotRunning", 1)]
    [InlineData("Showing", 9)]
    public void OrdinaryApplicationInputIsNotQueuedOrHitTested(string status, long foreground)
    {
        var buffer = new TouchInputBuffer(); var frame = Frame(status);
        Observe(buffer, 0x201, foreground: foreground, frame: frame); Observe(buffer, 0x200, foreground: foreground, frame: frame);
        Observe(buffer, 0x202, foreground: foreground, frame: frame);
        Assert.False(buffer.TryRead(1100, (_, _, _) => throw new Exception("Ordinary input reached window hit testing"), out _));
    }

    [Fact]
    public void SidebarOverlayCancelsTapInsteadOfSupplementingPowerPoint()
    {
        var buffer = new TouchInputBuffer(); var gesture = new TouchAssistGesture();
        Observe(buffer, 0x201); Observe(buffer, 0x202, 1100);
        while (buffer.TryRead(1100, (_, _, _) => false, out var sample))
        { Assert.Equal("Cancel", sample!.Kind); Assert.Null(sample.Target); Assert.Equal(0, sample.X); Assert.Null(gesture.Accept(sample)); }
    }

    [Fact]
    public void LeavingTheShowClearsCoordinatesAndCancelsTheWholeGesture()
    {
        var buffer = new TouchInputBuffer(); var gesture = new TouchAssistGesture();
        Observe(buffer, 0x201); Observe(buffer, 0x200, 1030, foreground: 9); Observe(buffer, 0x202, 1100);
        Assert.True(buffer.TryRead(1100, (_, _, _) => true, out var down)); gesture.Accept(down!);
        Assert.True(buffer.TryRead(1100, (_, _, _) => throw new Exception("Cancellation was hit tested"), out var cancel));
        Assert.Equal("Cancel", cancel!.Kind); Assert.Equal(0UL, cancel.ExtraInfo); Assert.Null(gesture.Accept(cancel));
        Assert.True(buffer.TryRead(1100, (_, _, _) => true, out var up)); Assert.Null(gesture.Accept(up!));
    }

    [Theory]
    [InlineData(999)]
    [InlineData(1501)]
    public void StaleOrFutureInputCannotWaitForWindowOrProduceATap(long now)
    {
        var buffer = new TouchInputBuffer(); Observe(buffer, 0x201);
        Assert.True(buffer.TryRead(now, (_, _, _) => throw new Exception("Stale input reached window hit testing"), out var sample));
        Assert.Equal("Cancel", sample!.Kind);
    }

    [Fact]
    public void StaleProbeAndAmbiguousShowsDoNotObserveInput()
    {
        var buffer = new TouchInputBuffer(); var stale = Frame(at: Stopwatch.GetTimestamp() - Stopwatch.Frequency);
        Observe(buffer, 0x201, frame: stale);
        Observe(buffer, 0x201, frame: Frame() with { Snapshot = new(DateTimeOffset.UtcNow, "Showing", Targets: [Target, Target]) });
        Assert.False(buffer.TryRead(1100, (_, _, _) => true, out _));
    }

    [Fact]
    public void FullQueueDropsInsteadOfBlockingAndCanBeDrained()
    {
        var buffer = new TouchInputBuffer(); Observe(buffer, 0x201);
        for (int i = 0; i < 4096; i++) Observe(buffer, 0x200);
        Assert.Equal(2049, buffer.Dropped);
        int count = 0; while (buffer.TryRead(1100, (_, _, _) => true, out _)) count++;
        Assert.Equal(2048, count);
        Observe(buffer, 0x202, 1100); Assert.True(buffer.TryRead(1100, (_, _, _) => true, out var up)); Assert.Equal("Up", up!.Kind);
    }
}

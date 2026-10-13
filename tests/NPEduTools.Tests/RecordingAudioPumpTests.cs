using NPEduTools.Recorder;

namespace NPEduTools.Tests;

public sealed class RecordingAudioPumpTests
{
    [Theory]
    [InlineData(501)] [InlineData(1000)] [InlineData(2500)] [InlineData(3000)]
    public void TransientGapKeepsAudioTimelineAndThenReturnsToLiveSamples(double gap)
    {
        var pacing = new RecordingAudioPacing();
        var first = pacing.Next(gap);
        Assert.True(first.NewGap); Assert.True(first.Silence);
        int silence = 0;
        for (; silence < Math.Ceiling(gap / 20); silence++)
        { Assert.True(pacing.Next(gap).Silence); pacing.Written(); }
        var recovered = pacing.Next(gap);
        Assert.False(recovered.Silence); Assert.False(recovered.NewGap);
        Assert.InRange(recovered.WaitMs, 0, 20);
    }

    [Fact]
    public void ProlongedOrRepeatedOverloadStillStops()
    {
        Assert.Throws<IOException>(() => new RecordingAudioPacing().Next(3001));
        var pacing = new RecordingAudioPacing();
        Assert.True(pacing.Next(2500).NewGap);
        for (int i = 0; i < 125; i++) pacing.Written();
        Assert.True(pacing.Next(5000).NewGap);
        for (int i = 0; i < 125; i++) pacing.Written();
        Assert.Throws<IOException>(() => pacing.Next(5600));
    }

    [Fact]
    public void ThirtyMinuteRunWithOccasionalGapsDoesNotAccumulateClockDrift()
    {
        var pacing = new RecordingAudioPacing();
        int gaps = 0; double elapsed = 0;
        for (int i = 0; i < 90000; i++)
        {
            if (i % 3000 == 2999) elapsed += 1000;
            var step = pacing.Next(elapsed);
            if (step.NewGap) gaps++;
            elapsed += step.WaitMs;
            pacing.Written();
            // Writes can catch up immediately, while only real-time waiting advances the clock.
        }
        Assert.Equal(30, gaps);
        Assert.InRange(pacing.Next(elapsed).WaitMs, 0, 20);
    }

    private class Sink(Action<ReadOnlyMemory<byte>> write) : Stream
    {
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        { write(buffer); return ValueTask.CompletedTask; }
        public override bool CanRead => false; public override bool CanSeek => false; public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException(); public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    [Fact]
    public async Task ActualPumpFillsDelayedContinuationWithSilenceAndCountsOneRecovery()
    {
        using var stop = new CancellationTokenSource();
        double elapsed = 0; int writes = 0, discarded = 0, recoveries = 0, silence = 0, live = 0;
        using var sink = new Sink(bytes =>
        {
            Assert.Equal(7680, bytes.Length);
            if (BitConverter.ToSingle(bytes.Span) == 0) silence++; else live++;
            if (++writes == 53) stop.Cancel();
        });
        await RecordingAudioPump.RunAsync(sink, buffer => Array.Fill(buffer, .25f), () => discarded++, () => recoveries++,
            stop.Token, () => elapsed, (ms, _) => { elapsed += ms + (writes == 1 ? 1000 : 0); return Task.CompletedTask; });
        Assert.Equal(1, recoveries); Assert.Equal(1, discarded);
        Assert.Equal(50, silence); Assert.Equal(3, live);
    }

    [Fact]
    public async Task ActualPumpDoesNotSwallowDeviceFailureOrCancellation()
    {
        using var sink = new Sink(_ => { });
        await Assert.ThrowsAsync<IOException>(() => RecordingAudioPump.RunAsync(sink, _ => throw new IOException("设备断开"),
            () => { }, () => { }, CancellationToken.None));
        using var stop = new CancellationTokenSource();
        using var cancelSink = new Sink(_ => stop.Cancel());
        await RecordingAudioPump.RunAsync(cancelSink, _ => { }, () => { }, () => { }, stop.Token);
    }

    private sealed class BlockedSink : Sink
    {
        public BlockedSink() : base(_ => { }) { }
        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) =>
            await Task.Delay(Timeout.Infinite, cancellationToken);
    }

    [Fact]
    public async Task ActualPumpStopsBlockedWriterWithinItsDeadline()
    {
        using var sink = new BlockedSink();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => RecordingAudioPump.RunAsync(sink, _ => { },
            () => { }, () => { }, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10)));
    }
}

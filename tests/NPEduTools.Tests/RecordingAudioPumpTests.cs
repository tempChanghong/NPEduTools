using NPEduTools.Recorder;

namespace NPEduTools.Tests;

public sealed class RecordingAudioPumpTests
{
    [Theory]
    [InlineData(501)] [InlineData(1000)] [InlineData(2500)] [InlineData(3000)]
    [InlineData(6000)] [InlineData(10000)]
    [InlineData(30000)] [InlineData(59999)] [InlineData(60000)]
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
    public void ProlongedSingleGapStillStopsButRepeatedRecoverableGapsDoNot()
    {
        Assert.Throws<IOException>(() => new RecordingAudioPacing().Next(60001));
        var pacing = new RecordingAudioPacing();
        for (int i = 1; i <= 6; i++)
        {
            var step = pacing.Next(i * 6000);
            Assert.True(step.NewGap);
            Assert.Equal(i * 6000, step.RecentLossMs);
            for (int block = 0; block < 300; block++) pacing.Written();
        }
        Assert.False(pacing.Next(36000).Silence);
        for (int block = 1800; block < 4800; block++)
        { Assert.False(pacing.Next(block * 20).NewGap); pacing.Written(); }
        Assert.Equal(600, pacing.Next(96600).RecentLossMs);
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
        var health = new RecordingAudioHealth();
        using var sink = new Sink(bytes =>
        {
            Assert.Equal(7680, bytes.Length);
            if (BitConverter.ToSingle(bytes.Span) == 0) silence++; else live++;
            if (++writes == 53) stop.Cancel();
        });
        await RecordingAudioPump.RunAsync(sink, buffer => Array.Fill(buffer, .25f), () => discarded++, () => recoveries++,
            stop.Token, () => elapsed, (ms, _) => { elapsed += ms + (writes == 1 ? 1000 : 0); return Task.CompletedTask; }, health);
        Assert.Equal(1, recoveries); Assert.Equal(1, discarded);
        Assert.Equal(50, silence); Assert.Equal(3, live);
        Assert.Contains("静音", health.Message());
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

    private sealed class RecoveringSink(Action<ReadOnlyMemory<byte>> write) : Sink(write)
    {
        private bool _first = true;
        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_first) { _first = false; await Task.Delay(TimeSpan.FromSeconds(12), cancellationToken); }
            await base.WriteAsync(buffer, cancellationToken);
        }
    }

    [Fact]
    public async Task ActualWriterCanRecoverBeyondFormerTenSecondLimitAndContinue()
    {
        using var stop = new CancellationTokenSource();
        var health = new RecordingAudioHealth();
        int writes = 0, recoveries = 0, silence = 0, resumedLive = 0;
        using var sink = new RecoveringSink(bytes =>
        {
            writes++;
            if (BitConverter.ToSingle(bytes.Span) == 0) silence++;
            else if (writes > 1) { resumedLive++; stop.Cancel(); }
        });
        await RecordingAudioPump.RunAsync(sink, samples => Array.Fill(samples, .1f), () => { }, () => recoveries++,
            stop.Token, health: health).WaitAsync(TimeSpan.FromSeconds(25));
        Assert.Equal(1, recoveries);
        Assert.True(silence >= 500);
        Assert.Equal(1, resumedLive);
        Assert.Contains("静音", health.Message());
    }

    [Fact]
    public async Task ActualPumpStopsBlockedWriterWithinItsDeadline()
    {
        using var sink = new BlockedSink();
        var error = await Assert.ThrowsAsync<IOException>(() => RecordingAudioPump.RunAsync(sink, _ => { },
            () => { }, () => { }, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(75)));
        Assert.Contains("60 秒", error.Message);
    }

    [Fact]
    public async Task CallerCancellationInterruptsBlockedWriterWithoutWaitingForRecoveryGrace()
    {
        using var stop = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
        using var sink = new BlockedSink();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => RecordingAudioPump.RunAsync(sink, _ => { },
            () => { }, () => { }, stop.Token).WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public void WaitingIsVisibleBeforeDeadlineAndClearsWhenWriterReturns()
    {
        var health = new RecordingAudioHealth();
        const long start = 1;
        health.Writing(start);
        Assert.Null(health.Message(start + System.Diagnostics.Stopwatch.Frequency / 2));
        Assert.Contains("等待恢复", health.Message(start + System.Diagnostics.Stopwatch.Frequency));
        health.WriteFinished();
        Assert.Null(health.Message(start + 2 * System.Diagnostics.Stopwatch.Frequency));
        health.Recovered(new(0, true, true, 6000, 36000));
        Assert.Contains("频繁卡顿", health.Message());
        health.Recovered(new(0, true, true, 600, 600));
        Assert.DoesNotContain("频繁卡顿", health.Message()!);
        Assert.Contains("静音", health.Message());
    }

    [Fact]
    public void PastQualityWarningDoesNotClaimCurrentMinuteIsStillPoor()
    {
        var health = new RecordingAudioHealth();
        health.Recovered(new(0, true, true, 6000, 36000));
        Assert.Contains("最近一分钟", health.Message());
        long later = System.Diagnostics.Stopwatch.GetTimestamp() + 61 * System.Diagnostics.Stopwatch.Frequency;
        Assert.DoesNotContain("最近一分钟", health.Message(later)!);
        Assert.Contains("累计约 6.0 秒", health.Message(later));
    }

    [Fact]
    public async Task SeveralSixSecondGapsContinueThroughActualPumpAndShowQualityWarning()
    {
        using var stop = new CancellationTokenSource();
        int writes = 0, recoveries = 0, silence = 0;
        double elapsed = 0;
        var health = new RecordingAudioHealth();
        using var sink = new Sink(bytes =>
        {
            if (BitConverter.ToSingle(bytes.Span) == 0) silence++;
            if (++writes == 1807) stop.Cancel();
        });
        await RecordingAudioPump.RunAsync(sink, b => Array.Fill(b, .1f), () => { }, () => recoveries++, stop.Token,
            () => elapsed, (ms, _) => { elapsed += ms + 6000; return Task.CompletedTask; }, health);
        Assert.Equal(6, recoveries);
        Assert.Equal(1800, silence);
        Assert.Contains("频繁卡顿", health.Message());
    }
}

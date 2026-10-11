using NPEduTools.Contracts;

namespace NPEduTools.Host;

public sealed partial class NoiseService
{
    private void AcceptMeter(Session s, SoundMeterObservation observation)
    {
        lock (_sync)
        {
            if (_session != s || s.Stop.IsCancellationRequested || s.UsbReference is null) return;
            double requested = _time.GetElapsedTime(s.Started, observation.RequestedTimestamp).TotalSeconds;
            double received = _time.GetElapsedTime(s.Started, observation.ReceivedTimestamp).TotalSeconds;
            double age = _time.GetElapsedTime(observation.ReceivedTimestamp).TotalSeconds;
            if (age is < 0 or > .25 || !s.UsbReference.Receive(observation.Reading, requested, received, observation.QueuedInputDiscarded))
            {
                s.MeterReading = null; s.MeterReceived = null; s.CalibrationInterrupted = true;
                Fail(s, s.UsbReference.Error ?? "USB 读数已陈旧，本段作废。"); return;
            }
            var v = observation.Reading;
            s.MeterReading = new(v.Decibels, v.Weighting.ToString(), v.TimeWeighting.ToString(), v.MaximumHold,
                v.LowBattery, v.Range, observation.RoundTrip.TotalMilliseconds, observation.InputReportLength, observation.OutputReportLength);
            s.MeterReceived = observation.ReceivedTimestamp;
        }
    }
    private NoiseReferenceMeterState MeterSnapshot(Session s)
    {
        double? age = s.MeterReceived is { } at ? _time.GetElapsedTime(at).TotalSeconds : null;
        bool fresh = !s.Stop.IsCancellationRequested && s.MeterReading is not null && age is >= 0 and <= NoiseReferenceContract.UsbMaximumGapSeconds;
        return new(fresh ? s.MeterReading : null, age,
            fresh ? "USB 连续读取中；按同一 Host 时钟取窗，仪表内部采样时间仍待实物验证。" : "仪表读数未确认或已过期，未沿用旧值。");
    }
    private async Task PollMeterAsync(Session s, IReferenceSoundMeter meter)
    {
        try
        {
            while (!s.Stop.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(NoiseReferenceContract.UsbPollMilliseconds), _time, s.Stop.Token);
                AcceptMeter(s, await meter.ReadAsync(s.Stop.Token));
            }
        }
        catch (OperationCanceledException) when (s.Stop.IsCancellationRequested) { }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            lock (_sync)
            {
                if (s.Stop.IsCancellationRequested) return;
                s.MeterReading = null; s.MeterReceived = null; s.CalibrationInterrupted = true;
                Fail(s, "USB 连续读取中断，本段作废；请核对仪表后重新采样。" +
                    (error is SoundMeterException e ? "（" + e.Error + "）" : ""));
            }
        }
    }
    private void CheckFinalCalibrationSignature(Session s)
    {
        if (s.Reference is null || s.Metadata is null) return;
        try
        {
            if (s.Metadata.Signature == s.Signature) return;
        }
        catch (Exception error) when (error is not OutOfMemoryException) { }
        s.CalibrationInterrupted = true; _lastReferenceSignature = null; _referenceSignatureAt = null;
        s.Failure = "参考采样结束时输入设置已变化或无法核对，本段作废。";
    }
}

using NPEduTools.Contracts;
using NPEduTools.Core;

namespace NPEduTools.Host;

public interface IReferenceSoundMeter : IAsyncDisposable
{
    Task<SoundMeterObservation> ReadAsync(CancellationToken token);
}

/// <summary>One explicit read, one disposable connection. Never auto-polls or alters a calibration.</summary>
public sealed class SoundMeterService(Func<ISoundMeterHidBackend> backend, Func<string, string?> verify,
    Func<bool>? unavailable = null)
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    // Only the authenticated NoiseService workflow acquires this lease. Shared with diagnostics.
    public async Task<IReferenceSoundMeter> OpenReferenceAsync(string path, TimeProvider time, CancellationToken token)
    {
        if (!NoiseReferenceContract.MeterPathValid(path)) throw new ArgumentException("Invalid meter path.", nameof(path));
        if (unavailable?.Invoke() == true || !await _gate.WaitAsync(0, token))
            throw new SoundMeterException(SoundMeterError.BusyOrDenied, "Meter unavailable or already in use.");
        Dly2202Reader? reader = null;
        try
        {
            token.ThrowIfCancellationRequested();
            reader = new(backend(), time, TimeSpan.FromMilliseconds(NoiseReferenceContract.UsbReadTimeoutMilliseconds));
            await reader.ConnectAsync(path, token);
            return new ReferenceLease(reader, _gate);
        }
        catch
        {
            try { if (reader is not null) await reader.DisposeAsync(); }
            finally { _gate.Release(); }
            throw;
        }
    }
    private sealed class ReferenceLease(Dly2202Reader reader, SemaphoreSlim gate) : IReferenceSoundMeter
    {
        private int _disposed;
        public Task<SoundMeterObservation> ReadAsync(CancellationToken token) => reader.ReadAsync(token, discardQueuedInput: true);
        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            try { await reader.DisposeAsync(); } finally { gate.Release(); }
        }
    }
    public async Task<HostResponse> HandleAsync(HostRequest r, CancellationToken token)
    {
        HostResponse Reject(string code, string message) => new(1, r.RequestId, "Rejected", code, message);
        if (Protocol.Validate(r) is { } invalid) return Reject(invalid, "联机请求无效。");
        if (r.Capability != "noise.meter.command") return Reject("UnknownCapability", "联机请求无效。");
        if (verify(r.SoundMeter!.Secret) is { } denied) return Reject(denied, "本机管理验证未通过，未连接声级计。");
        if (unavailable?.Invoke() == true) return Reject("NoiseStopping", "后台维护退出中，未连接声级计。");
        if (!await _gate.WaitAsync(0, token)) return Reject("METER_BUSY", "另一条联机请求正在执行，请稍后重试。");
        try
        {
            token.ThrowIfCancellationRequested();
            var selectedBackend = backend(); // No backend or enumeration at startup/window load.
            if (r.SoundMeter.Action == "list")
            {
                var devices = selectedBackend.Enumerate().Where(d => d.VendorId == Dly2202Protocol.VendorId &&
                    d.ProductId == Dly2202Protocol.ProductId && SoundMeterContract.Valid(new("read", "x", d.Path)))
                    .Take(8).Select(d => new SoundMeterDevice(d.Path,
                        new string((d.Name ?? "DLY-2202").Where(c => !char.IsControl(c)).Take(200).ToArray()),
                        d.MetadataError, d.NativeErrorCode)).ToArray();
                token.ThrowIfCancellationRequested();
                return new(1, r.RequestId, "Succeeded", null, devices.Length == 0 ? "未发现兼容设备；未打开任何设备。" : "请选择需要测试的声级计。", SoundMeter: new(devices));
            }
            await using var reader = new Dly2202Reader(selectedBackend);
            await reader.ConnectAsync(r.SoundMeter.DevicePath!, token);
            var observation = await reader.ReadAsync(token);
            token.ThrowIfCancellationRequested();
            var v = observation.Reading;
            var sample = new SoundMeterSample(v.Decibels, v.Weighting.ToString(), v.TimeWeighting.ToString(),
                v.MaximumHold, v.LowBattery, v.Range, observation.RoundTrip.TotalMilliseconds, observation.InputReportLength, observation.OutputReportLength);
            return new(1, r.RequestId, "Succeeded", null,
                "已读取一次并释放连接；这是仪表读数，不是校准成功，不自动保存为参考。", SoundMeter: new(Reading: sample));
        }
        catch (SoundMeterException e) { return new(1, r.RequestId, "Rejected", "METER_" + e.Error.ToString().ToUpperInvariant(), Message(e.Error),
            SoundMeter: e.Stage is null && e.InputReportLength is null && e.NativeErrorCode is null ? null :
                new(Diagnostic: new(e.Stage, e.InputReportLength, e.OutputReportLength, e.NativeErrorCode))); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        { return Reject("METER_UNAVAILABLE", "联机读取不可用，请关闭 SoundLab2、检查 USB 连接后重试；未使用旧读数。"); }
        finally { _gate.Release(); }
    }
    private static string Message(SoundMeterError e) => e switch
    {
        SoundMeterError.DeviceNotFound => "所选声级计已不在设备列表，不会切换其他设备。",
        SoundMeterError.AmbiguousDevice or SoundMeterError.WrongDevice => "设备身份不明确或已变化，未读取其他设备。",
        SoundMeterError.UnsupportedReport => "实物报文长度与当前协议不符，未发送未验证的命令。",
        SoundMeterError.InvalidReading => "仪表报文无效或超出量程，未沿用旧读数。",
        SoundMeterError.TimedOut => "读取超时，连接已关闭；请核对仪表并显式重试。",
        SoundMeterError.BusyOrDenied => "设备忙或访问被拒绝，请先关闭 SoundLab2。",
        _ => "声级计连接中断；未沿用旧读数，请重新选择并读取。"
    };
}

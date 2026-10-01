using System.Runtime.Versioning;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NPEduTools.Contracts;
using NPEduTools.Core;

namespace NPEduTools.Host;

[SupportedOSPlatform("windows")]
public sealed class WasapiNoiseCapture : INoiseCapture
{
    private readonly MMDevice _device;
    private readonly WasapiCapture _capture;
    private readonly NoiseMeter _meter;
    private int _disposed;
    public string DeviceName { get; }
    public event Action<NoiseFrame>? Frame;
    public event Action<string>? Failed;

    public static IReadOnlyList<NoiseDevice> Enumerate()
    {
        using var enumerator = new MMDeviceEnumerator();
        var result = new List<NoiseDevice>();
        foreach (var device in enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active))
        {
            using (device)
                result.Add(new(device.ID, device.FriendlyName.Length > 256 ? device.FriendlyName[..256] : device.FriendlyName));
        }
        return result;
    }

    public WasapiNoiseCapture(string id)
    {
        using var enumerator = new MMDeviceEnumerator();
        _device = enumerator.GetDevice(id); // Pin the actual capture endpoint; never fall back to another microphone.
        WasapiCapture? capture = null;
        try
        {
            if (_device.State != DeviceState.Active || _device.DataFlow != DataFlow.Capture)
                throw new InvalidOperationException("Selected microphone is not active.");
            DeviceName = _device.FriendlyName;
            capture = new WasapiCapture(_device) { ShareMode = AudioClientShareMode.Shared };
            var format = capture.WaveFormat;
            bool floating = format.Encoding == WaveFormatEncoding.IeeeFloat ||
                format is WaveFormatExtensible ext && ext.SubFormat == new Guid("00000003-0000-0010-8000-00aa00389b71");
            bool pcm = format.Encoding == WaveFormatEncoding.Pcm ||
                format is WaveFormatExtensible pcmExt && pcmExt.SubFormat == new Guid("00000001-0000-0010-8000-00aa00389b71");
            if (!floating && !pcm) throw new InvalidOperationException("Unsupported capture format.");
            _meter = new(format.SampleRate, format.Channels, format.BitsPerSample, floating, frame => Frame?.Invoke(frame));
            _capture = capture;
            _capture.DataAvailable += (_, data) =>
            {
                if (Volatile.Read(ref _disposed) != 0) return;
                try { _meter.Push(data.Buffer.AsSpan(0, data.BytesRecorded)); }
                catch (Exception error) when (error is not OutOfMemoryException)
                { Failed?.Invoke("音频格式或采样数据异常，监测已停止。"); }
            };
            _capture.RecordingStopped += (_, _) =>
            {
                if (Volatile.Read(ref _disposed) == 0) Failed?.Invoke("麦克风采集中断，请检查设备后手动重试。");
            };
        }
        catch { capture?.Dispose(); _device.Dispose(); throw; }
    }

    public void Start() => _capture.StartRecording();
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        try { _capture.StopRecording(); }
        finally { try { _capture.Dispose(); } finally { _device.Dispose(); } }
    }
}

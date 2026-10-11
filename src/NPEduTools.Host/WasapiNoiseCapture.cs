using System.Runtime.Versioning;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NPEduTools.Contracts;
using NPEduTools.Core;

namespace NPEduTools.Host;

[SupportedOSPlatform("windows")]
public sealed class WasapiNoiseCapture : IReferenceNoiseCapture
{
    private readonly MMDevice _device;
    private readonly WasapiCapture _capture;
    private readonly NoiseMeter? _meter;
    private ReferenceNoiseMeter? _referenceMeter;
    private int _disposed;
    public string DeviceName { get; }
    private string _formatSignature = "";
    private readonly bool _referenceOnly;
    public string Signature
    {
        get
        {
            var volume = _device.AudioEndpointVolume;
            var culture = System.Globalization.CultureInfo.InvariantCulture;
            return _formatSignature + "|gain=" + volume.MasterVolumeLevelScalar.ToString("R", culture) +
                "|mute=" + volume.Mute + "|channelGain=" + string.Join(",", Enumerable.Range(0, volume.Channels.Count)
                    .Select(i => volume.Channels[i].VolumeLevelScalar.ToString("R", culture)));
        }
    }
    public string ReferenceSignature
    {
        get { string signature = Signature; return _referenceOnly ? signature : ReferenceNoiseMeter.AlgorithmVersion + signature[signature.IndexOf('|')..]; }
    }
    public void EnableReference()
    {
        if (_referenceMeter is not null) return;
        var f = _capture.WaveFormat;
        bool floating = f.Encoding == WaveFormatEncoding.IeeeFloat || f is WaveFormatExtensible ext &&
            ext.SubFormat == new Guid("00000003-0000-0010-8000-00aa00389b71");
        _referenceMeter = new(f.SampleRate, f.Channels, f.BitsPerSample, floating, frame => ReferenceFrame?.Invoke(frame));
    }
    public event Action<NoiseFrame>? Frame;
    public event Action<ReferenceNoiseFrame>? ReferenceFrame;
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

    public WasapiNoiseCapture(string id) : this(id, false) { }
    public static IReferenceNoiseCapture CreateReference(string id) => new WasapiNoiseCapture(id, true);
    private WasapiNoiseCapture(string id, bool reference)
    {
        _referenceOnly = reference;
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
            _formatSignature = $"{(reference ? ReferenceNoiseMeter.AlgorithmVersion : "pcm-energy-v1")}|{format.SampleRate}|{format.Channels}|{format.BitsPerSample}|{format.Encoding}";
            using (var formatBytes = new MemoryStream())
            {
                using (var writer = new BinaryWriter(formatBytes, System.Text.Encoding.UTF8, true)) format.Serialize(writer);
                _formatSignature += "|format=" + Convert.ToHexString(formatBytes.ToArray());
            }
            bool floating = format.Encoding == WaveFormatEncoding.IeeeFloat ||
                format is WaveFormatExtensible ext && ext.SubFormat == new Guid("00000003-0000-0010-8000-00aa00389b71");
            bool pcm = format.Encoding == WaveFormatEncoding.Pcm ||
                format is WaveFormatExtensible pcmExt && pcmExt.SubFormat == new Guid("00000001-0000-0010-8000-00aa00389b71");
            if (!floating && !pcm) throw new InvalidOperationException("Unsupported capture format.");
            if (reference) _referenceMeter = new(format.SampleRate, format.Channels, format.BitsPerSample, floating, frame => ReferenceFrame?.Invoke(frame));
            else _meter = new(format.SampleRate, format.Channels, format.BitsPerSample, floating, frame => Frame?.Invoke(frame));
            _capture = capture;
            _capture.DataAvailable += (_, data) =>
            {
                if (Volatile.Read(ref _disposed) != 0) return;
                try
                {
                    if (_referenceMeter is not null) _referenceMeter.Push(data.Buffer.AsSpan(0, data.BytesRecorded));
                    _meter?.Push(data.Buffer.AsSpan(0, data.BytesRecorded));
                }
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

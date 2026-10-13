using System.IO.Pipes;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using NPEduTools.Contracts;

namespace NPEduTools.Recorder;

internal sealed class AudioInput : IAsyncDisposable
{
    private sealed record Source(WasapiCapture Capture, MMDevice Device, BufferedWaveProvider Buffer, ISampleProvider Samples);
    private readonly List<Source> _sources = [];
    private readonly NamedPipeServerStream _pipe;
    private readonly CancellationTokenSource _lifetime = new();
    private Task? _pump;
    private long _overruns;
    private Exception? _error;
    public string PipePath { get; }
    public long Overruns => Interlocked.Read(ref _overruns);
    public Exception? Error => Volatile.Read(ref _error);

    public AudioInput(RecordingOptions options)
    {
        string name = "NPEduTools.Record.Audio." + Guid.NewGuid().ToString("N");
        PipePath = @"\\.\pipe\" + name;
        _pipe = new(name, PipeDirection.Out, 1, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly, 32768, 32768);
        try
        {
            if (options.SystemAudio) Add(options.SpeakerId, true);
            if (options.Microphone) Add(options.MicrophoneId, false);
        }
        catch
        {
            foreach (var source in _sources) { source.Capture.Dispose(); source.Device.Dispose(); }
            _pipe.Dispose(); _lifetime.Dispose(); throw;
        }
    }
    private void Add(string id, bool loopback)
    {
        using var devices = new MMDeviceEnumerator();
        var device = id == "default" ? devices.GetDefaultAudioEndpoint(loopback ? DataFlow.Render : DataFlow.Capture, Role.Console) : devices.GetDevice(id);
        WasapiCapture? capture = null;
        try
        {
            if (device.State != DeviceState.Active || device.DataFlow != (loopback ? DataFlow.Render : DataFlow.Capture))
                throw new InvalidOperationException("选中的音频设备当前不可用。");
            capture = loopback ? new WasapiLoopbackCapture(device) : new WasapiCapture(device);
            var buffer = new BufferedWaveProvider(capture.WaveFormat) { BufferDuration = TimeSpan.FromSeconds(2), ReadFully = true, DiscardOnBufferOverflow = true };
            ISampleProvider samples = new StereoSamples(buffer.ToSampleProvider());
            if (samples.WaveFormat.SampleRate != 48000) samples = new WdlResamplingSampleProvider(samples, 48000);
            capture.DataAvailable += (_, e) =>
            {
                if (buffer.BufferedBytes + e.BytesRecorded > buffer.BufferLength) Interlocked.Increment(ref _overruns);
                buffer.AddSamples(e.Buffer, 0, e.BytesRecorded);
            };
            capture.RecordingStopped += (_, e) => { if (e.Exception is not null) Volatile.Write(ref _error, e.Exception); };
            _sources.Add(new(capture, device, buffer, samples));
        }
        catch { capture?.Dispose(); device.Dispose(); throw; }
    }
    public async Task PrepareAsync()
    {
        // Device initialization can take hundreds of milliseconds. Complete it before
        // GDI starts, otherwise every segment begins with an audio/video offset.
        await Task.Run(() => { foreach (var source in _sources) source.Capture.StartRecording(); });
        _pump = Task.Run(PumpAsync);
    }
    private async Task PumpAsync()
    {
        try
        {
            var token = _lifetime.Token;
            await _pipe.WaitForConnectionAsync(token);
            foreach (var source in _sources) source.Buffer.ClearBuffer();
            float[] input = new float[1920];
            await RecordingAudioPump.RunAsync(_pipe, mixed =>
            {
                if (Error is { } error) throw new IOException("音频设备已中断。", error);
                foreach (var source in _sources)
                {
                    // Bound latency after bursts; do not keep stale classroom audio queued indefinitely.
                    if (source.Buffer.BufferedDuration > TimeSpan.FromMilliseconds(300))
                    { source.Buffer.ClearBuffer(); Interlocked.Increment(ref _overruns); }
                    int read = source.Samples.Read(input, 0, input.Length);
                    for (int i = 0; i < read; i++) mixed[i] += input[i] / _sources.Count;
                }
            }, () => { foreach (var source in _sources) source.Buffer.ClearBuffer(); },
                () => Interlocked.Increment(ref _overruns), token);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception error) when (error is not OutOfMemoryException) { Volatile.Write(ref _error, error); }
    }
    public async ValueTask DisposeAsync()
    {
        _lifetime.Cancel();
        await _pipe.DisposeAsync();
        if (_pump is not null) await _pump;
        foreach (var source in _sources) { source.Capture.Dispose(); source.Device.Dispose(); }
        _lifetime.Dispose();
    }
    private sealed class StereoSamples(ISampleProvider input) : ISampleProvider
    {
        private float[] _buffer = [];
        public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(input.WaveFormat.SampleRate, 2);
        public int Read(float[] buffer, int offset, int count)
        {
            int channels = input.WaveFormat.Channels;
            int needed = count / 2 * channels;
            if (_buffer.Length < needed) _buffer = new float[needed];
            int frames = input.Read(_buffer, 0, needed) / channels;
            for (int frame = 0; frame < frames; frame++)
            {
                if (channels <= 2)
                {
                    buffer[offset + frame * 2] = _buffer[frame * channels];
                    buffer[offset + frame * 2 + 1] = _buffer[frame * channels + channels - 1];
                }
                else
                {
                    // Preserve speech on center/surround channels; downmix all channels to mono stereo.
                    float sample = 0;
                    for (int channel = 0; channel < channels; channel++) sample += _buffer[frame * channels + channel] / channels;
                    buffer[offset + frame * 2] = buffer[offset + frame * 2 + 1] = sample;
                }
            }
            return frames * 2;
        }
    }
}

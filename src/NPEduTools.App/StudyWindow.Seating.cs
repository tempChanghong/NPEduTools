using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;
using NPEduTools.Contracts;
using NPEduTools.Core;
namespace NPEduTools.App;
public sealed partial class StudyWindow
{
    private CancellationTokenSource? _seatingRead;
    private string? _seatingKey, _seatingRetryKey;
    private long _seatingRetryAt, _seatingSequence;
    public void InvalidateSeating() { _seatingRead?.Cancel(); _seatingKey = null; _surface.ClearSeating(); }
    private void UpdateSeating(StudyHostState state)
    {
        if (state.Seating is not { State: "READY", Hash: { } hash, Version: { } version, Chunks: > 0 and <= 128 } seating) {
            _seatingRead?.Cancel(); _seatingKey = null; _surface.ClearSeating(); return;
        }
        string scope = $"{state.Display?.InstanceId}/{version}";
        _surface.SeatingScope(scope);
        string key = $"{scope}/{seating.Revision}/{hash}";
        if (_seatingKey == key && (_surface.HasSeating(hash, seating.Revision) || _seatingRead is { IsCancellationRequested: false })) return;
        if (_seatingRetryKey == key && System.Diagnostics.Stopwatch.GetElapsedTime(_seatingRetryAt).TotalSeconds < 5) return;
        _seatingRead?.Cancel(); _seatingKey = key; _seatingRead = new(TimeSpan.FromSeconds(30));
        _ = ReadSeatingAsync(key, seating, _seatingRead);
    }
    private async Task ReadSeatingAsync(string key, StudySeatingState seat, CancellationTokenSource cancellation)
    {
        try {
            using var buffer = new MemoryStream();
            for (int index = 0; index < seat.Chunks; index++) {
                var request = new HostRequest(1, Guid.NewGuid(), "study.seating", StudySeating: new(seat.Version!, seat.Revision, seat.Hash!, index));
                var reply = await HostClient.RequestAsync(_pipe, request, cancellation.Token);
                if (reply.RequestId != request.RequestId || reply.Outcome != "Succeeded" || reply.SeatingChunk is not { } chunk || chunk.Hash != seat.Hash || chunk.Revision != seat.Revision || chunk.Index != index || chunk.Count != seat.Chunks) throw new InvalidDataException();
                var bytes = Convert.FromBase64String(chunk.Data);
                if (bytes.Length is <= 0 or > 16384 || index < seat.Chunks - 1 && bytes.Length != 16384 || buffer.Length + bytes.Length > seat.ByteCount) throw new InvalidDataException();
                buffer.Write(bytes);
            }
            var image = buffer.ToArray();
            if (image.Length < 24 || image.Length != seat.ByteCount || image.Length > 2097152 || Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(image)) != seat.Hash ||
                !image.AsSpan(0, 8).SequenceEqual(new byte[] {137,80,78,71,13,10,26,10}) ||
                System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(image.AsSpan(16)) != seat.Width || System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(image.AsSpan(20)) != seat.Height ||
                seat.Width is <= 0 or > 4096 || seat.Height is <= 0 or > 4096 || (long)seat.Width * seat.Height > 6000000) throw new InvalidDataException();
            using var stream = new MemoryStream(image);
            var frame = BitmapFrame.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            if (frame.PixelWidth != seat.Width || frame.PixelHeight != seat.Height) throw new InvalidDataException();
            frame.Freeze();
            if (!_shutdown && !cancellation.IsCancellationRequested && _seatingKey == key) _surface.ApplySeating(seat.Hash!, seat.Revision, frame);
        } catch (Exception e) when (e is IOException or InvalidDataException or TimeoutException or OperationCanceledException or System.Text.Json.JsonException or UnauthorizedAccessException or FormatException or NotSupportedException or ArgumentException) {
            if (_seatingKey == key && ReferenceEquals(_seatingRead, cancellation)) { _seatingKey = null; _seatingRetryKey = key; _seatingRetryAt = System.Diagnostics.Stopwatch.GetTimestamp(); }
        } finally { if (ReferenceEquals(_seatingRead, cancellation)) _seatingRead = null; cancellation.Dispose(); }
    }
    private async Task ReportSeatingAsync()
    {
        if (_state is not { Version: { } version, Display: { } display, Seating.State: "READY" }) return;
        bool visible = !_shutdown && IsVisible && WindowState != WindowState.Minimized && _surface.SeatingVisible;
        var p = new StudySeatingPulse(display.InstanceId, _appInstance, ++_seatingSequence, version,
            _surface.SeatingRevision, _surface.SeatingHash, visible, visible ? _surface.FreshRenderAgeMs : null);
        try { using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
            await HostClient.RequestAsync(_pipe, new HostRequest(1, Guid.NewGuid(), "study.seating.presentation", SeatingPresentation: p), timeout.Token);
        } catch (Exception e) when (e is IOException or OperationCanceledException or InvalidDataException or System.Text.Json.JsonException or UnauthorizedAccessException or TimeoutException) { }
    }
}

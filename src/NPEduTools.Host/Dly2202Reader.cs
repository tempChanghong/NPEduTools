using NPEduTools.Core;

namespace NPEduTools.Host;

public sealed record SoundMeterHidDevice(string Path, string Name, ushort VendorId, ushort ProductId,
    string? MetadataError = null, int? NativeErrorCode = null);

/// <summary>Enumeration does not start acquisition. Open only the explicitly selected path.</summary>
public interface ISoundMeterHidBackend
{
    IReadOnlyList<SoundMeterHidDevice> Enumerate();
    ISoundMeterHidConnection Open(SoundMeterHidDevice device);
}

public interface ISoundMeterHidConnection : IDisposable
{
    SoundMeterHidDevice Device { get; }
    int InputReportLength { get; }
    int OutputReportLength { get; }
    ValueTask WriteAsync(ReadOnlyMemory<byte> report, CancellationToken cancellationToken);
    ValueTask<int> ReadAsync(Memory<byte> report, CancellationToken cancellationToken);
}

public enum SoundMeterError
{
    NotConnected, DeviceNotFound, AmbiguousDevice, WrongDevice, UnsupportedReport,
    InvalidReading, TimedOut, Disconnected, BusyOrDenied
}

public sealed class SoundMeterException(SoundMeterError error, string message, Exception? inner = null,
    int? inputReportLength = null, int? outputReportLength = null, string? stage = null, int? nativeErrorCode = null)
    : IOException(message, inner)
{
    public SoundMeterError Error { get; } = error;
    public int? InputReportLength { get; } = inputReportLength;
    public int? OutputReportLength { get; } = outputReportLength;
    public string? Stage { get; } = stage;
    public int? NativeErrorCode { get; } = nativeErrorCode;
}

/// <summary>Host monotonic timestamps bracket this request; they are not instrument sample times.</summary>
public sealed record SoundMeterObservation(SoundMeterHidDevice Device, SoundMeterReading Reading,
    long RequestedTimestamp, long ReceivedTimestamp, TimeSpan RoundTrip, int InputReportLength = 0, int OutputReportLength = 0);

/// <summary>
/// Explicit, single-flight reference reads. No polling at startup, background reconnect, stale-value
/// fallback, microphone access, settings writes, or report upload. A failed exchange closes the
/// connection so an old asynchronous operation is not reused as a later request. The observed
/// protocol has no established sequence number or device sample timestamp.
/// </summary>
public sealed class Dly2202Reader : IAsyncDisposable
{
    private readonly ISoundMeterHidBackend _backend;
    private readonly TimeProvider _time;
    private readonly TimeSpan _timeout;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly CancellationToken _lifetimeToken;
    private ISoundMeterHidConnection? _connection;
    private int _disposed;
    private int _connected;

    public Dly2202Reader(ISoundMeterHidBackend backend, TimeProvider? time = null,
        TimeSpan? timeout = null)
    {
        ArgumentNullException.ThrowIfNull(backend);
        _backend = backend;
        _time = time ?? TimeProvider.System;
        _timeout = timeout ?? TimeSpan.FromSeconds(2);
        if (_timeout < TimeSpan.FromMilliseconds(50) || _timeout > TimeSpan.FromSeconds(10))
            throw new ArgumentOutOfRangeException(nameof(timeout));
        _lifetimeToken = _lifetime.Token;
    }

    public bool IsConnected => Volatile.Read(ref _connected) != 0;

    public async Task ConnectAsync(string devicePath, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(devicePath);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetimeToken);
        await _gate.WaitAsync(linked.Token).ConfigureAwait(false);
        try
        {
            linked.Token.ThrowIfCancellationRequested();
            CloseConnection();
            var matches = _backend.Enumerate().Where(d =>
                string.Equals(d.Path, devicePath, StringComparison.OrdinalIgnoreCase)).ToArray();
            linked.Token.ThrowIfCancellationRequested(); // Synchronous enumeration may outlast the caller's deadline.
            if (matches.Length == 0) throw new SoundMeterException(SoundMeterError.DeviceNotFound,
                "Selected reference meter was not found. No other device was selected.");
            if (matches.Length != 1) throw new SoundMeterException(SoundMeterError.AmbiguousDevice,
                "Selected reference meter identity is ambiguous.");
            ValidateIdentity(matches[0], devicePath);
            linked.Token.ThrowIfCancellationRequested();
            var connection = _backend.Open(matches[0]);
            try
            {
                ValidateIdentity(connection.Device, devicePath);
                if (!Dly2202Protocol.SupportsReportLength(connection.InputReportLength) ||
                    !Dly2202Protocol.SupportsReportLength(connection.OutputReportLength))
                    throw new SoundMeterException(SoundMeterError.UnsupportedReport,
                        "Device HID report lengths are not supported by the observed protocol.",
                        inputReportLength: connection.InputReportLength, outputReportLength: connection.OutputReportLength, stage: "Capabilities");
                linked.Token.ThrowIfCancellationRequested();
                _connection = connection;
                Volatile.Write(ref _connected, 1);
            }
            catch { connection.Dispose(); throw; }
        }
        catch (UnauthorizedAccessException error)
        { throw new SoundMeterException(SoundMeterError.BusyOrDenied, "Close SoundLab and check device access.", error,
            stage: "Open", nativeErrorCode: (error.InnerException as System.ComponentModel.Win32Exception)?.NativeErrorCode); }
        catch (IOException error) when (error is not SoundMeterException)
        { throw new SoundMeterException(SoundMeterError.Disconnected, "Reference meter could not be opened.", error,
            stage: "Connect", nativeErrorCode: (error.InnerException as System.ComponentModel.Win32Exception)?.NativeErrorCode); }
        finally { _gate.Release(); }
    }

    public async Task<SoundMeterObservation> ReadAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetimeToken);
        await _gate.WaitAsync(linked.Token).ConfigureAwait(false);
        try
        {
            linked.Token.ThrowIfCancellationRequested();
            var connection = _connection ?? throw new SoundMeterException(SoundMeterError.NotConnected,
                "Explicitly connect the selected reference meter first.");
            long requested = _time.GetTimestamp();
            using var operationCancellation = CancellationTokenSource.CreateLinkedTokenSource(linked.Token);
            var exchange = ExchangeAsync(connection, operationCancellation.Token);
            try
            {
                var report = await exchange.WaitAsync(_timeout, _time, linked.Token).ConfigureAwait(false);
                linked.Token.ThrowIfCancellationRequested();
                if (!Dly2202Protocol.TryDecode(report, out var reading, out string reason))
                    throw new SoundMeterException(SoundMeterError.InvalidReading, "Invalid reference report: " + reason);
                long received = _time.GetTimestamp();
                return new(connection.Device, reading!, requested, received, _time.GetElapsedTime(requested, received),
                    connection.InputReportLength, connection.OutputReportLength);
            }
            catch (TimeoutException error)
            {
                throw new SoundMeterException(SoundMeterError.TimedOut,
                    "Reference meter did not respond in time. Reconnect explicitly before retrying.", error);
            }
            catch (IOException error) when (error is not SoundMeterException)
            { throw new SoundMeterException(SoundMeterError.Disconnected, "Reference meter disconnected.", error); }
            finally
            {
                // Observe failures from a driver which completes after cancellation/timeout.
                _ = exchange.ContinueWith(t => { _ = t.Exception; }, CancellationToken.None,
                    TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);
                operationCancellation.Cancel();
            }
        }
        catch { CloseConnection(); throw; }
        finally { _gate.Release(); }
    }

    private static async Task<byte[]> ExchangeAsync(ISoundMeterHidConnection connection, CancellationToken token)
    {
        await connection.WriteAsync(Dly2202Protocol.CreateReadReport(connection.OutputReportLength), token)
            .ConfigureAwait(false);
        var report = new byte[connection.InputReportLength];
        int count = await connection.ReadAsync(report, token).ConfigureAwait(false);
        if (count != report.Length) throw new SoundMeterException(SoundMeterError.InvalidReading,
            "A partial or empty HID report is not a valid reference sample.");
        return report;
    }

    private static void ValidateIdentity(SoundMeterHidDevice device, string path)
    {
        if (device.VendorId != Dly2202Protocol.VendorId || device.ProductId != Dly2202Protocol.ProductId ||
            !string.Equals(device.Path, path, StringComparison.OrdinalIgnoreCase))
            throw new SoundMeterException(SoundMeterError.WrongDevice, "Reference meter identity does not match selection.");
    }

    private void CloseConnection()
    {
        Volatile.Write(ref _connected, 0);
        var connection = _connection;
        _connection = null;
        connection?.Dispose();
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _lifetime.Cancel();
        await _gate.WaitAsync().ConfigureAwait(false);
        try { CloseConnection(); }
        finally { _gate.Release(); _lifetime.Dispose(); }
    }
}

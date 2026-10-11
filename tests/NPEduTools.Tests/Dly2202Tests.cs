using System.Buffers.Binary;
using System.Runtime.InteropServices;
using NPEduTools.Core;
using NPEduTools.Host;

namespace NPEduTools.Tests;

public sealed partial class Dly2202Tests
{
    private static SoundMeterHidDevice Device(string path = "synthetic-meter") =>
        new(path, "Synthetic DLY-2202", Dly2202Protocol.VendorId, Dly2202Protocol.ProductId);

    private static byte[] Report(double value = 60.5, byte flags = 0, int length = 9)
    {
        var report = new byte[length];
        BinaryPrimitives.WriteInt16BigEndian(report.AsSpan(1, 2), checked((short)Math.Round(value * 10)));
        report[3] = flags;
        return report;
    }

    private sealed class Clock : TimeProvider
    {
        public long Milliseconds = 1000;
        public override long TimestampFrequency => 1000;
        public override long GetTimestamp() => Milliseconds;
    }

    private sealed class Backend : ISoundMeterHidBackend
    {
        public IReadOnlyList<SoundMeterHidDevice> Devices = [Device()];
        public Func<SoundMeterHidDevice, ISoundMeterHidConnection> Create = d => new Connection(d);
        public int Enumerations, Opens;
        public IReadOnlyList<SoundMeterHidDevice> Enumerate() { Enumerations++; return Devices; }
        public ISoundMeterHidConnection Open(SoundMeterHidDevice device) { Opens++; return Create(device); }
    }

    private sealed class Connection(SoundMeterHidDevice device, int input = 9, int output = 9)
        : ISoundMeterHidConnection
    {
        public SoundMeterHidDevice Device { get; } = device;
        public int InputReportLength { get; } = input;
        public int OutputReportLength { get; } = output;
        public bool Disposed;
        public List<byte[]> Writes = [];
        public Func<Memory<byte>, CancellationToken, ValueTask<int>>? Read;
        public Func<CancellationToken, ValueTask>? Write;
        public ValueTask WriteAsync(ReadOnlyMemory<byte> report, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            Writes.Add(report.ToArray());
            return Write?.Invoke(token) ?? ValueTask.CompletedTask;
        }
        public ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (Read is not null) return Read(buffer, token);
            Report(length: InputReportLength).CopyTo(buffer);
            return ValueTask.FromResult(InputReportLength);
        }
        public void Dispose() => Disposed = true;
    }

    [Theory]
    [InlineData(9, 0)]
    [InlineData(65, 0x23)]
    public void Query_report_matches_observed_vendor_forms_and_only_reads(int length, byte padding)
    {
        var report = Dly2202Protocol.CreateReadReport(length);
        Assert.Equal(length, report.Length);
        Assert.Equal(0, report[0]); Assert.Equal(0xB3, report[1]);
        Assert.All(report.Skip(2), b => Assert.Equal(padding, b));
    }

    [Theory]
    [InlineData(0)] [InlineData(4)] [InlineData(8)] [InlineData(10)] [InlineData(64)] [InlineData(66)]
    public void Unobserved_lengths_are_not_guessed(int length)
    {
        Assert.False(Dly2202Protocol.SupportsReportLength(length));
        Assert.Throws<ArgumentOutOfRangeException>(() => Dly2202Protocol.CreateReadReport(length));
        Assert.False(Dly2202Protocol.TryDecode(new byte[length], out _, out _));
    }

    [Theory]
    [InlineData(9)] [InlineData(65)]
    public void A_slow_reading_decodes_without_changing_units(int length)
    {
        Assert.True(Dly2202Protocol.TryDecode(Report(length: length), out var reading, out _));
        Assert.Equal(60.5, reading!.Decibels); Assert.Equal("dB(A)", reading.Unit);
        Assert.Equal(SoundMeterTimeWeighting.Slow, reading.TimeWeighting);
        Assert.False(reading.MaximumHold); Assert.False(reading.LowBattery); Assert.Equal(0, reading.Range);
    }

    [Fact]
    public void C_fast_max_and_battery_status_are_preserved_not_disguised_as_A_reference()
    {
        Assert.True(Dly2202Protocol.TryDecode(Report(130, 0xF4), out var reading, out _));
        Assert.Equal("dB(C)", reading!.Unit); Assert.Equal(SoundMeterTimeWeighting.Fast, reading.TimeWeighting);
        Assert.True(reading.MaximumHold); Assert.True(reading.LowBattery); Assert.Equal(4, reading.Range);
    }

    [Theory]
    [InlineData(30, 0, true)] [InlineData(130, 0, true)]
    [InlineData(29.9, 0, false)] [InlineData(130.1, 0, false)] [InlineData(-1, 0, false)]
    [InlineData(35, 0x10, true)] [InlineData(34.9, 0x10, false)]
    [InlineData(80, 1, true)] [InlineData(80.1, 1, false)]
    [InlineData(50, 2, true)] [InlineData(49.9, 2, false)]
    [InlineData(110, 3, true)] [InlineData(110.1, 3, false)]
    [InlineData(80, 4, true)] [InlineData(79.9, 4, false)]
    [InlineData(60, 5, false)] [InlineData(60, 15, false)]
    public void Invalid_or_out_of_range_values_cannot_become_reference_samples(double level, byte flags, bool valid)
    {
        Assert.Equal(valid, Dly2202Protocol.TryDecode(Report(level, flags), out var reading, out _));
        Assert.Equal(valid, reading is not null);
    }

    [Fact]
    public void Unexpected_report_ID_is_rejected()
    {
        var report = Report(); report[0] = 1;
        Assert.False(Dly2202Protocol.TryDecode(report, out _, out _));
    }

    [Fact]
    public async Task Construction_and_unconnected_reads_do_not_enumerate_or_acquire_devices()
    {
        var backend = new Backend(); await using var reader = new Dly2202Reader(backend);
        Assert.False(reader.IsConnected);
        var error = await Assert.ThrowsAsync<SoundMeterException>(() => reader.ReadAsync());
        Assert.Equal(SoundMeterError.NotConnected, error.Error);
        Assert.Equal(0, backend.Enumerations); Assert.Equal(0, backend.Opens);
    }

    [Fact]
    public async Task Missing_selection_never_falls_back_to_another_meter()
    {
        var backend = new Backend(); await using var reader = new Dly2202Reader(backend);
        var error = await Assert.ThrowsAsync<SoundMeterException>(() => reader.ConnectAsync("missing"));
        Assert.Equal(SoundMeterError.DeviceNotFound, error.Error); Assert.Equal(0, backend.Opens);
    }

    [Fact]
    public async Task Ambiguous_selection_is_rejected()
    {
        var backend = new Backend { Devices = [Device(), Device()] };
        await using var reader = new Dly2202Reader(backend);
        var error = await Assert.ThrowsAsync<SoundMeterException>(() => reader.ConnectAsync("synthetic-meter"));
        Assert.Equal(SoundMeterError.AmbiguousDevice, error.Error); Assert.Equal(0, backend.Opens);
    }

    [Fact]
    public async Task Different_product_is_never_opened()
    {
        var backend = new Backend { Devices = [Device() with { ProductId = 0x1234 }] };
        await using var reader = new Dly2202Reader(backend);
        var error = await Assert.ThrowsAsync<SoundMeterException>(() => reader.ConnectAsync("synthetic-meter"));
        Assert.Equal(SoundMeterError.WrongDevice, error.Error); Assert.Equal(0, backend.Opens);
    }

    [Fact]
    public async Task Identity_changed_between_enumeration_and_open_is_closed()
    {
        var connection = new Connection(Device("other"));
        var backend = new Backend { Create = _ => connection }; await using var reader = new Dly2202Reader(backend);
        var error = await Assert.ThrowsAsync<SoundMeterException>(() => reader.ConnectAsync("synthetic-meter"));
        Assert.Equal(SoundMeterError.WrongDevice, error.Error); Assert.True(connection.Disposed);
    }

    [Theory]
    [InlineData(8, 9)] [InlineData(9, 64)] [InlineData(8192, 65)]
    public async Task Unsupported_capabilities_are_closed_before_sending(int input, int output)
    {
        var connection = new Connection(Device(), input, output);
        var backend = new Backend { Create = _ => connection }; await using var reader = new Dly2202Reader(backend);
        var error = await Assert.ThrowsAsync<SoundMeterException>(() => reader.ConnectAsync("synthetic-meter"));
        Assert.Equal(SoundMeterError.UnsupportedReport, error.Error); Assert.True(connection.Disposed);
        Assert.Empty(connection.Writes);
    }

    [Theory]
    [InlineData(9, 9)] [InlineData(65, 65)] [InlineData(9, 65)] [InlineData(65, 9)]
    public async Task Fresh_reads_use_capabilities_and_Host_monotonic_time(int input, int output)
    {
        var clock = new Clock(); var connection = new Connection(Device(), input, output);
        connection.Read = (buffer, _) =>
        { clock.Milliseconds += 25; Report(length: input).CopyTo(buffer); return ValueTask.FromResult(input); };
        var backend = new Backend { Create = _ => connection };
        await using var reader = new Dly2202Reader(backend, clock);
        await reader.ConnectAsync("synthetic-meter"); Assert.Empty(connection.Writes);
        var sample = await reader.ReadAsync();
        Assert.Equal(60.5, sample.Reading.Decibels); Assert.Equal("synthetic-meter", sample.Device.Path);
        Assert.Equal(1000, sample.RequestedTimestamp); Assert.Equal(1025, sample.ReceivedTimestamp);
        Assert.Equal(TimeSpan.FromMilliseconds(25), sample.RoundTrip);
        Assert.Equal(input, sample.InputReportLength); Assert.Equal(output, sample.OutputReportLength);
        Assert.Equal(output, Assert.Single(connection.Writes).Length);
        Assert.Equal(0xB3, connection.Writes[0][1]); Assert.True(reader.IsConnected);
        await reader.DisposeAsync(); Assert.True(connection.Disposed); Assert.False(reader.IsConnected);
    }

    [Theory]
    [InlineData(0)] [InlineData(3)] [InlineData(8)]
    public async Task Empty_or_partial_read_closes_session(int count)
    {
        var connection = new Connection(Device()) { Read = (_, _) => ValueTask.FromResult(count) };
        var backend = new Backend { Create = _ => connection }; await using var reader = new Dly2202Reader(backend);
        await reader.ConnectAsync("synthetic-meter");
        var error = await Assert.ThrowsAsync<SoundMeterException>(() => reader.ReadAsync());
        Assert.Equal(SoundMeterError.InvalidReading, error.Error); Assert.True(connection.Disposed);
        Assert.False(reader.IsConnected);
    }

    [Fact]
    public async Task Invalid_packet_closes_connection_instead_of_returning_previous_value()
    {
        var connection = new Connection(Device()); var backend = new Backend { Create = _ => connection };
        await using var reader = new Dly2202Reader(backend); await reader.ConnectAsync("synthetic-meter");
        Assert.Equal(60.5, (await reader.ReadAsync()).Reading.Decibels);
        connection.Read = (buffer, _) => { Report(140).CopyTo(buffer); return ValueTask.FromResult(9); };
        var error = await Assert.ThrowsAsync<SoundMeterException>(() => reader.ReadAsync());
        Assert.Equal(SoundMeterError.InvalidReading, error.Error); Assert.False(reader.IsConnected);
        Assert.Equal(SoundMeterError.NotConnected,
            (await Assert.ThrowsAsync<SoundMeterException>(() => reader.ReadAsync())).Error);
    }

    [Fact]
    public async Task Busy_or_denied_device_has_an_explicit_error()
    {
        var backend = new Backend { Create = _ => throw new UnauthorizedAccessException() };
        await using var reader = new Dly2202Reader(backend);
        var error = await Assert.ThrowsAsync<SoundMeterException>(() => reader.ConnectAsync("synthetic-meter"));
        Assert.Equal(SoundMeterError.BusyOrDenied, error.Error); Assert.False(reader.IsConnected);
    }

    [Fact]
    public async Task Cancellation_after_open_releases_the_handle_without_publishing_a_connection()
    {
        using var stop = new CancellationTokenSource();
        var connection = new Connection(Device());
        var backend = new Backend { Create = _ => { stop.Cancel(); return connection; } };
        await using var reader = new Dly2202Reader(backend);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => reader.ConnectAsync("synthetic-meter", stop.Token));
        Assert.True(connection.Disposed); Assert.False(reader.IsConnected); Assert.Empty(connection.Writes);
    }

    [Fact]
    public async Task Explicit_reconnect_releases_prior_selection_and_missing_new_device_leaves_no_connection()
    {
        var connection = new Connection(Device()); var backend = new Backend { Create = _ => connection };
        await using var reader = new Dly2202Reader(backend);
        await reader.ConnectAsync("synthetic-meter"); await reader.ReadAsync();
        var error = await Assert.ThrowsAsync<SoundMeterException>(() => reader.ConnectAsync("missing"));
        Assert.Equal(SoundMeterError.DeviceNotFound, error.Error);
        Assert.True(connection.Disposed); Assert.False(reader.IsConnected);
    }

    [Theory]
    [InlineData(true)] [InlineData(false)]
    public async Task Unplug_during_write_or_read_closes_session(bool duringWrite)
    {
        var connection = new Connection(Device());
        if (duringWrite) connection.Write = _ => throw new IOException("Synthetic unplug");
        else connection.Read = (_, _) => throw new IOException("Synthetic unplug");
        var backend = new Backend { Create = _ => connection }; await using var reader = new Dly2202Reader(backend);
        await reader.ConnectAsync("synthetic-meter");
        var error = await Assert.ThrowsAsync<SoundMeterException>(() => reader.ReadAsync());
        Assert.Equal(SoundMeterError.Disconnected, error.Error); Assert.True(connection.Disposed);
        Assert.False(reader.IsConnected);
    }

    [Fact]
    public async Task Timeout_covers_write_and_read_and_requires_explicit_reconnect()
    {
        var connection = new Connection(Device())
        { Write = async token => await Task.Delay(Timeout.Infinite, token) };
        var backend = new Backend { Create = _ => connection };
        await using var reader = new Dly2202Reader(backend, timeout: TimeSpan.FromMilliseconds(50));
        await reader.ConnectAsync("synthetic-meter");
        var error = await Assert.ThrowsAsync<SoundMeterException>(() => reader.ReadAsync());
        Assert.Equal(SoundMeterError.TimedOut, error.Error); Assert.True(connection.Disposed);
        Assert.False(reader.IsConnected); Assert.Equal(1, backend.Opens);
    }

    [Fact]
    public async Task Late_response_after_timeout_cannot_contaminate_new_connection()
    {
        var late = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        var old = new Connection(Device())
        { Read = async (buffer, _) => { (await late.Task).CopyTo(buffer); return 9; } };
        var next = new Connection(Device()); int opens = 0;
        var backend = new Backend { Create = _ => ++opens == 1 ? old : next };
        await using var reader = new Dly2202Reader(backend, timeout: TimeSpan.FromMilliseconds(50));
        await reader.ConnectAsync("synthetic-meter");
        Assert.Equal(SoundMeterError.TimedOut,
            (await Assert.ThrowsAsync<SoundMeterException>(() => reader.ReadAsync())).Error);
        Assert.True(old.Disposed);
        await reader.ConnectAsync("synthetic-meter");
        late.SetResult(Report(110));
        Assert.Equal(60.5, (await reader.ReadAsync()).Reading.Decibels);
        Assert.Single(next.Writes);
    }

    [Fact]
    public async Task Concurrent_reads_are_serialized_and_cancelled_waiter_does_not_close_active_read()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var connection = new Connection(Device()); int reads = 0;
        connection.Read = async (buffer, token) =>
        {
            if (++reads == 1) { entered.SetResult(); await ready.Task.WaitAsync(token); }
            Report().CopyTo(buffer); return 9;
        };
        var backend = new Backend { Create = _ => connection }; await using var reader = new Dly2202Reader(backend);
        await reader.ConnectAsync("synthetic-meter"); var first = reader.ReadAsync();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        using var cancelled = new CancellationTokenSource(); var waiting = reader.ReadAsync(cancelled.Token);
        cancelled.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
        Assert.False(connection.Disposed); Assert.Single(connection.Writes);
        var second = reader.ReadAsync(); ready.SetResult();
        await first; await second; Assert.Equal(2, connection.Writes.Count); Assert.True(reader.IsConnected);
    }

    [Fact]
    public async Task Cancelling_active_read_closes_connection_without_sampling_elsewhere()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var connection = new Connection(Device())
        { Read = async (_, token) => { entered.SetResult(); await Task.Delay(Timeout.Infinite, token); return 0; } };
        var backend = new Backend { Create = _ => connection }; await using var reader = new Dly2202Reader(backend);
        await reader.ConnectAsync("synthetic-meter"); using var cancelled = new CancellationTokenSource();
        var read = reader.ReadAsync(cancelled.Token); await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        cancelled.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => read);
        Assert.True(connection.Disposed); Assert.False(reader.IsConnected); Assert.Equal(1, backend.Opens);
    }

    [Fact]
    public async Task Disposal_cancels_active_and_queued_requests_and_releases_connection()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var connection = new Connection(Device())
        { Read = async (_, token) => { entered.SetResult(); await Task.Delay(Timeout.Infinite, token); return 0; } };
        var backend = new Backend { Create = _ => connection }; var reader = new Dly2202Reader(backend);
        await reader.ConnectAsync("synthetic-meter"); var read = reader.ReadAsync();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(3)); var queued = reader.ReadAsync();
        await reader.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(3));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => read);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => queued);
        Assert.True(connection.Disposed); Assert.False(reader.IsConnected);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => reader.ReadAsync());
        await reader.DisposeAsync();
    }

    [Fact]
    public void Windows_HID_struct_layout_matches_native_ABI_without_calling_any_native_function()
    {
        if (!OperatingSystem.IsWindows()) return;
        Assert.Equal(12, Marshal.SizeOf<WindowsSoundMeterHid.Native.Attributes>());
        Assert.Equal(64, Marshal.SizeOf<WindowsSoundMeterHid.Native.Capabilities>());
        Assert.Equal(IntPtr.Size == 8 ? 32 : 28, Marshal.SizeOf<WindowsSoundMeterHid.Native.InterfaceData>());
        Assert.Equal(new IntPtr(4), Marshal.OffsetOf<WindowsSoundMeterHid.Native.Capabilities>("InputLength"));
        Assert.Equal(new IntPtr(6), Marshal.OffsetOf<WindowsSoundMeterHid.Native.Capabilities>("OutputLength"));
    }

    [Fact]
    public void Windows_native_entry_points_exist_without_enumerating_or_opening_any_device()
    {
        if (!OperatingSystem.IsWindows()) return;
        var exports = new Dictionary<string, string[]>
        {
            ["hid.dll"] = ["HidD_GetHidGuid", "HidD_GetAttributes", "HidD_GetProductString",
                "HidD_GetPreparsedData", "HidD_FreePreparsedData", "HidP_GetCaps"],
            ["setupapi.dll"] = ["SetupDiGetClassDevsW", "SetupDiEnumDeviceInterfaces",
                "SetupDiGetDeviceInterfaceDetailW", "SetupDiDestroyDeviceInfoList"],
            ["kernel32.dll"] = ["CreateFileW"]
        };
        foreach (var (library, symbols) in exports)
        {
            var handle = NativeLibrary.Load(Path.Combine(Environment.SystemDirectory, library));
            try { foreach (string symbol in symbols) Assert.True(NativeLibrary.TryGetExport(handle, symbol, out _), symbol); }
            finally { NativeLibrary.Free(handle); }
        }
    }
}

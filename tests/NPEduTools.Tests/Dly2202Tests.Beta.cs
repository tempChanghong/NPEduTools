using NPEduTools.Contracts;
using NPEduTools.Core;
using NPEduTools.Host;

namespace NPEduTools.Tests;

public sealed partial class Dly2202Tests
{
    private static HostRequest Meter(string action = "read", string secret = "admin") => new(1, Guid.NewGuid(), "noise.meter.command",
        SoundMeter: new(action, secret, action == "read" ? "synthetic-meter" : null));

    [Fact]
    public async Task BetaRequiresValidLocalManagementAndDoesNothingAtConstruction()
    {
        int factories = 0; var backend = new Backend();
        var service = new SoundMeterService(() => { factories++; return backend; }, s => s == "admin" ? null : "MANAGEMENT_SECRET_INVALID");
        Assert.Equal(0, factories);
        Assert.Equal("MANAGEMENT_SECRET_INVALID", (await service.HandleAsync(Meter(secret: "wrong"), default)).ErrorCode);
        Assert.Equal(0, factories); Assert.Equal(0, backend.Enumerations);
        var list = await service.HandleAsync(Meter("list"), default);
        Assert.Equal("Succeeded", list.Outcome); Assert.Single(list.SoundMeter!.Devices!); Assert.Equal(0, backend.Opens);
        Assert.Equal("InvalidSoundMeterCommand", Protocol.Validate(Meter() with { SoundMeter = new("write", "admin") }));
        Assert.Equal("InvalidSoundMeterCommand", Protocol.Validate(Meter() with { Capability = "noise.status" }));
    }

    [Fact]
    public async Task BetaExitGateAndInvalidRequestsNeverCreateBackend()
    {
        int factories = 0;
        var service = new SoundMeterService(() => { factories++; return new Backend(); }, _ => null, () => true);
        Assert.Equal("NoiseStopping", (await service.HandleAsync(Meter(), default)).ErrorCode);
        var invalid = Meter() with { SoundMeter = new("read", "admin", "bad\npath") };
        Assert.Equal("InvalidSoundMeterCommand", (await service.HandleAsync(invalid, default)).ErrorCode);
        Assert.Equal(0, factories);
    }

    [Fact]
    public async Task BetaReadsOneExactDeviceReleasesItAndDoesNotReturnOldReadingAfterFailure()
    {
        var connections = new List<Connection>(); bool invalid = false;
        var backend = new Backend { Create = d =>
        {
            var c = new Connection(d) { Read = (buffer, _) => { Report(invalid ? 140 : 60.5, 0xC0).CopyTo(buffer); return ValueTask.FromResult(9); } };
            connections.Add(c); return c;
        } };
        var service = new SoundMeterService(() => backend, _ => null);
        var read = await service.HandleAsync(Meter(), default);
        Assert.Equal("Succeeded", read.Outcome); Assert.Equal(60.5, read.SoundMeter!.Reading!.Decibels);
        Assert.Equal("Fast", read.SoundMeter.Reading.TimeWeighting); Assert.True(read.SoundMeter.Reading.LowBattery);
        Assert.Equal("A", read.SoundMeter.Reading.Weighting); Assert.True(connections.Single().Disposed);
        invalid = true; read = await service.HandleAsync(Meter(), default);
        Assert.Equal("METER_INVALIDREADING", read.ErrorCode); Assert.Null(read.SoundMeter);
        Assert.All(connections, c => { Assert.True(c.Disposed); Assert.Equal(0xB3, Assert.Single(c.Writes)[1]); });
        Assert.DoesNotContain("admin", System.Text.Json.JsonSerializer.Serialize(read, Protocol.Json));
    }

    [Fact]
    public async Task BetaConcurrentRequestIsRejectedAndCancellationReleasesTheSelectedHandle()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var connection = new Connection(Device()) { Read = async (_, token) =>
        { entered.TrySetResult(); await Task.Delay(Timeout.Infinite, token); return 0; } };
        var backend = new Backend { Create = _ => connection };
        var service = new SoundMeterService(() => backend, _ => null);
        using var stop = new CancellationTokenSource(); var first = service.HandleAsync(Meter(), stop.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal("METER_BUSY", (await service.HandleAsync(Meter(), default)).ErrorCode);
        Assert.Equal(1, backend.Opens); stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first); Assert.True(connection.Disposed);
        backend.Create = d => new Connection(d);
        Assert.Equal("Succeeded", (await service.HandleAsync(Meter(), default)).Outcome);
    }

    [Fact]
    public async Task ThousandReconnectsWithInvalidPacketsReleaseEveryFakeHandleAndSendOnlyReads()
    {
        int opened = 0; var connections = new List<Connection>();
        var backend = new Backend { Create = d =>
        {
            int n = ++opened;
            var c = new Connection(d, n % 2 == 0 ? 65 : 9, n % 3 == 0 ? 65 : 9);
            c.Read = (buffer, _) => { Report(n % 17 == 0 ? 140 : 60.5, length: c.InputReportLength).CopyTo(buffer); return ValueTask.FromResult(c.InputReportLength); };
            connections.Add(c); return c;
        } };
        await using var reader = new Dly2202Reader(backend);
        for (int i = 1; i <= 1000; i++)
        {
            await reader.ConnectAsync("synthetic-meter");
            if (i % 17 == 0)
            { Assert.Equal(SoundMeterError.InvalidReading, (await Assert.ThrowsAsync<SoundMeterException>(() => reader.ReadAsync())).Error); Assert.False(reader.IsConnected); }
            else Assert.Equal(60.5, (await reader.ReadAsync()).Reading.Decibels);
        }
        await reader.DisposeAsync(); Assert.Equal(1000, opened);
        Assert.All(connections, c => { Assert.True(c.Disposed); Assert.Equal(0xB3, Assert.Single(c.Writes)[1]); });
    }

    [Theory]
    [InlineData(9)] [InlineData(65)]
    public void ExhaustiveSignedReadingsNeverEscapeInstrumentRange(int length)
    {
        var packet = new byte[length];
        foreach (byte flags in new byte[] { 0, 0x10, 0x40, 0xF4 })
        {
            packet[3] = flags;
            for (int raw = short.MinValue; raw <= short.MaxValue; raw++)
            {
                System.Buffers.Binary.BinaryPrimitives.WriteInt16BigEndian(packet.AsSpan(1, 2), (short)raw);
                if (!Dly2202Protocol.TryDecode(packet, out var reading, out _)) continue;
                Assert.InRange(reading!.Decibels, reading.Weighting == SoundMeterWeighting.C ? 35 : 30, 130);
                Assert.InRange(reading.Range, 0, 4); Assert.Equal(raw / 10d, reading.Decibels);
            }
        }
    }
}

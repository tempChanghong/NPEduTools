using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using NPEduTools.Contracts;

namespace NPEduTools.Tests;

public sealed class ProtocolTests
{
    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(65537)]
    public async Task RejectsInvalidLengthBeforeReadingBody(int length)
    {
        byte[] header = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(header, length);
        await using var stream = new MemoryStream(header);
        await Assert.ThrowsAsync<InvalidDataException>(() => Protocol.ReadAsync<HostRequest>(stream, default));
    }

    [Fact]
    public async Task RejectsTruncatedFrame()
    {
        await using var stream = new MemoryStream(new byte[] { 5, 0, 0, 0, 123 });
        await Assert.ThrowsAsync<EndOfStreamException>(() => Protocol.ReadAsync<HostRequest>(stream, default));
    }

    [Fact]
    public async Task RejectsUnexpectedFields()
    {
        byte[] body = Encoding.UTF8.GetBytes("{\"version\":1,\"shell\":\"not-allowed\"}");
        await using var stream = new MemoryStream();
        byte[] header = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(header, body.Length);
        stream.Write(header);
        stream.Write(body);
        stream.Position = 0;
        await Assert.ThrowsAsync<JsonException>(() => Protocol.ReadAsync<HostRequest>(stream, default));
    }

    [Theory]
    [InlineData(2, "host.ping", 3000, 0, "ProtocolVersionMismatch")]
    [InlineData(1, "shell.execute", 3000, 0, "UnknownCapability")]
    [InlineData(1, "classisland.status", 15001, 0, "InvalidTimeout")]
    [InlineData(1, "classisland.status", 1000, 1000, "InvalidObservationWindow")]
    public void ValidatesRequestBudgetAndAllowlist(int version, string capability, int timeout, int observe, string expected)
        => Assert.Equal(expected, Protocol.Validate(new(version, Guid.NewGuid(), capability, timeout, observe)));
}

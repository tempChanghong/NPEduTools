using System.Security.Cryptography;
using System.Buffers.Binary;
using System.Text;
using System.Text.Json.Nodes;
using NPEduTools.Host;

namespace NPEduTools.Tests;

public sealed class ClassroomUsbPresenceTests
{
    [Fact]
    public void Usb_storage_identity_is_shared_across_ports_but_requires_an_actual_usb_serial()
    {
        // STORAGE_DEVICE_DESCRIPTOR fields and bytes as returned to the Windows endpoint.
        var descriptor = new byte[96];
        BinaryPrimitives.WriteUInt32LittleEndian(descriptor.AsSpan(4), (uint)descriptor.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(descriptor.AsSpan(28), 7);
        void Field(int offsetIndex, int offset, string value)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(descriptor.AsSpan(offsetIndex), (uint)offset);
            Encoding.ASCII.GetBytes(value).CopyTo(descriptor, offset);
        }
        Field(12, 40, "  Acme "); Field(16, 50, "Model A"); Field(24, 60, "Serial42 ");
        const string digest = "b84f5b9b8f82be98c5fd5b2c6575ac4da60d437dc9cf846a08ec5100f7fe1030";
        Assert.Equal(digest, ClassroomUsbPresence.DecodeMediaHash(descriptor));

        descriptor[60] = (byte)'Z';
        Assert.NotEqual(digest, ClassroomUsbPresence.DecodeMediaHash(descriptor));
        descriptor[60] = 0;
        Assert.Null(ClassroomUsbPresence.DecodeMediaHash(descriptor));
        Field(24, 60, "Serial42");
        BinaryPrimitives.WriteUInt32LittleEndian(descriptor.AsSpan(28), 11);
        Assert.Null(ClassroomUsbPresence.DecodeMediaHash(descriptor));
        Assert.Null(ClassroomUsbPresence.DecodeMediaHash(descriptor.AsSpan(0, 32)));

        var oversized = new byte[400];
        descriptor.CopyTo(oversized, 0);
        BinaryPrimitives.WriteUInt32LittleEndian(oversized.AsSpan(4), (uint)oversized.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(oversized.AsSpan(28), 7);
        oversized.AsSpan(60, 257).Fill((byte)'X');
        Assert.Null(ClassroomUsbPresence.DecodeMediaHash(oversized));
    }

    [Fact]
    public void Registered_usb_accepts_its_own_marker_with_or_without_bom_and_checks_current_media()
    {
        var keyId = Guid.NewGuid().ToString("D");
        var secret = RandomNumberGenerator.GetBytes(32);
        string hash = Convert.ToHexStringLower(SHA256.HashData(secret));
        string mediaHash = new('a', 64), otherMedia = new('b', 64);
        var registered = new ClassroomUsbKey(keyId, mediaHash, hash);
        string token = Convert.ToBase64String(secret).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var marker = new JsonObject { ["schemaVersion"] = 1, ["keyId"] = keyId, ["secret"] = token };

        bool Read(string input, string media, IEnumerable<ClassroomUsbKey> keys, bool bom = false)
        {
            byte[] payload = Encoding.UTF8.GetBytes(input);
            using var file = new MemoryStream(bom ? [.. Encoding.UTF8.Preamble, .. payload] : payload);
            return ClassroomUsbPresence.Matches(file, media, keys.ToArray());
        }

        Assert.True(Read(marker.ToJsonString(), mediaHash, [registered]));
        Assert.True(Read(marker.ToJsonString(), mediaHash, [registered], bom: true));
        Assert.False(Read(marker.ToJsonString(), otherMedia, [registered]));
        Assert.False(Read(marker.ToJsonString(), mediaHash, [])); // the enrollment was revoked
        Assert.False(Read(marker.ToJsonString(), mediaHash, [registered with { KeyId = Guid.NewGuid().ToString("D") }]));
        Assert.False(Read(marker.ToJsonString(), mediaHash, [registered with { SecretHash = new string('0', 64) }]));

        Assert.False(Read(marker.ToJsonString() + new string(' ', 512), mediaHash, [registered]));
        Assert.False(Read("{", mediaHash, [registered]));
        marker["schemaVersion"] = 2;
        Assert.False(Read(marker.ToJsonString(), mediaHash, [registered]));
    }
}

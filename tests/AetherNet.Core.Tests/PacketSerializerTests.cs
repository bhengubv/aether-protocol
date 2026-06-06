// SPDX-License-Identifier: MIT

using AetherNet.Protocol;
using Xunit;

namespace AetherNet.Core.Tests;

/// <summary>
/// Round-trip tests for <see cref="PacketSerializer"/>. Mirror of the Swift
/// suite in <c>swift/Tests/PacketSerializationTests.swift</c>; cross-language
/// byte equivalence is validated separately under <c>fixtures/</c>.
/// </summary>
public class PacketSerializerTests
{
    private static byte[] EightByteNonce(byte fill = 0x00) => Enumerable.Repeat(fill, 8).ToArray();

    [Fact]
    public void SerializeDeserialize_RoundTrip()
    {
        var packet = new MeshPacket
        {
            Type = PacketType.Data,
            SourceUhid = "alice-node",
            DestinationUhid = "bob-node",
            Ttl = 7,
            Priority = 10,
            Payload = System.Text.Encoding.UTF8.GetBytes("Hello, Aether!"),
            PacketNonce = EightByteNonce(0xAB),
        };

        var bytes = PacketSerializer.Serialize(packet);
        var roundTripped = PacketSerializer.Deserialize(bytes);

        Assert.Equal(packet.Type, roundTripped.Type);
        Assert.Equal(packet.SourceUhid, roundTripped.SourceUhid);
        Assert.Equal(packet.DestinationUhid, roundTripped.DestinationUhid);
        Assert.Equal(packet.Ttl, roundTripped.Ttl);
        Assert.Equal(packet.Priority, roundTripped.Priority);
        Assert.Equal(packet.Payload, roundTripped.Payload);
        Assert.Equal(packet.PacketNonce, roundTripped.PacketNonce);
        Assert.Equal(packet.ProtocolVersion, roundTripped.ProtocolVersion);
    }

    [Fact]
    public void EmptyDestinationUhid_RoundTrips()
    {
        var packet = new MeshPacket
        {
            Type = PacketType.SosBroadcast,
            SourceUhid = "node-1",
            DestinationUhid = string.Empty,
            PacketNonce = EightByteNonce(),
        };

        var bytes = PacketSerializer.Serialize(packet);
        var got = PacketSerializer.Deserialize(bytes);

        Assert.Equal("node-1", got.SourceUhid);
        Assert.Equal(string.Empty, got.DestinationUhid);
    }

    [Fact]
    public void EmptyPayload_RoundTrips()
    {
        var packet = new MeshPacket
        {
            Type = PacketType.Heartbeat,
            SourceUhid = "node-1",
            PacketNonce = EightByteNonce(),
            Payload = Array.Empty<byte>(),
        };

        var bytes = PacketSerializer.Serialize(packet);
        var got = PacketSerializer.Deserialize(bytes);

        Assert.Empty(got.Payload);
    }

    [Fact]
    public void LargePayload_RoundTrips()
    {
        var payload = Enumerable.Repeat((byte)0xFF, 262144).ToArray(); // 256 KB
        var packet = new MeshPacket
        {
            Type = PacketType.ChunkData,
            SourceUhid = "node-1",
            DestinationUhid = "node-2",
            PacketNonce = EightByteNonce(),
            Payload = payload,
        };

        var bytes = PacketSerializer.Serialize(packet);
        var got = PacketSerializer.Deserialize(bytes);

        Assert.Equal(262144, got.Payload.Length);
        Assert.Equal(payload[0], got.Payload[0]);
        Assert.Equal(payload[^1], got.Payload[^1]);
    }

    [Fact]
    public void Uuid_RoundTrips()
    {
        var expected = Guid.Parse("550e8400-e29b-41d4-a716-446655440000");
        var packet = new MeshPacket
        {
            Id = expected,
            Type = PacketType.Data,
            SourceUhid = "node-1",
            PacketNonce = EightByteNonce(),
        };

        var bytes = PacketSerializer.Serialize(packet);
        var got = PacketSerializer.Deserialize(bytes);

        Assert.Equal(expected, got.Id);
    }

    [Fact]
    public void Uuid_WireOrderIsRfc4122BigEndian()
    {
        // Anchors the cross-language wire-order contract: 16 bytes after
        // [version(1), type(1)] must be the UUID in RFC4122 big-endian order.
        // Catches the .NET default mixed-endian Guid bug if anyone reverts it.
        var expected = Guid.Parse("550e8400-e29b-41d4-a716-446655440000");
        var packet = new MeshPacket
        {
            Id = expected,
            Type = PacketType.Data,
            SourceUhid = "n",
            PacketNonce = EightByteNonce(),
        };

        var bytes = PacketSerializer.Serialize(packet);
        var idBytes = bytes.AsSpan(2, 16).ToArray();

        Assert.Equal(new byte[]
        {
            0x55, 0x0e, 0x84, 0x00, 0xe2, 0x9b, 0x41, 0xd4,
            0xa7, 0x16, 0x44, 0x66, 0x55, 0x44, 0x00, 0x00,
        }, idBytes);
    }

    [Fact]
    public void TooShort_Throws()
    {
        var tooShort = new byte[] { 0x01, 0x02 };
        Assert.Throws<ArgumentException>(() => PacketSerializer.Deserialize(tooShort));
    }

    [Fact]
    public void TryDeserialize_ReturnsFalseOnGarbage()
    {
        var ok = PacketSerializer.TryDeserialize(new byte[] { 0xFF }, out var packet);
        Assert.False(ok);
        Assert.Null(packet);
    }

    [Fact]
    public void AllPacketTypes_RoundTrip()
    {
        var types = Enum.GetValues<PacketType>();
        foreach (var t in types)
        {
            var packet = new MeshPacket
            {
                Type = t,
                SourceUhid = $"node-{(byte)t}",
                PacketNonce = EightByteNonce(),
            };
            var bytes = PacketSerializer.Serialize(packet);
            var got = PacketSerializer.Deserialize(bytes);
            Assert.Equal(t, got.Type);
        }
    }

    [Fact]
    public void Timestamp_PreservedToTheMillisecond()
    {
        const long ts = 1710528000000L; // 2024-03-15 12:00:00 UTC
        var packet = new MeshPacket
        {
            Type = PacketType.Data,
            SourceUhid = "node-1",
            TimestampMs = ts,
            PacketNonce = EightByteNonce(),
        };

        var bytes = PacketSerializer.Serialize(packet);
        var got = PacketSerializer.Deserialize(bytes);

        Assert.Equal(ts, got.TimestampMs);
    }

    [Fact]
    public void UnicodeUhids_RoundTrip()
    {
        var packet = new MeshPacket
        {
            Type = PacketType.Data,
            SourceUhid = "노드-1",
            DestinationUhid = "узел-2",
            PacketNonce = EightByteNonce(),
        };

        var bytes = PacketSerializer.Serialize(packet);
        var got = PacketSerializer.Deserialize(bytes);

        Assert.Equal("노드-1", got.SourceUhid);
        Assert.Equal("узел-2", got.DestinationUhid);
    }

    [Fact]
    public void Signature_Preserved()
    {
        var packet = new MeshPacket
        {
            Type = PacketType.Data,
            SourceUhid = "node-1",
            PacketNonce = EightByteNonce(),
            Signature = Enumerable.Repeat((byte)0xAB, 64).ToArray(),
        };

        var bytes = PacketSerializer.Serialize(packet);
        var got = PacketSerializer.Deserialize(bytes);

        Assert.Equal(packet.Signature, got.Signature);
    }

    [Fact]
    public void Ttl_FullInt32RangePreserved()
    {
        // Anchors the int32-TTL fix (was uint8 with silent truncation in the
        // pre-2026-05-02 implementation).
        var packet = new MeshPacket
        {
            Type = PacketType.Data,
            SourceUhid = "n",
            Ttl = 256, // > UInt8 max — would have wrapped to 0 under the bug
            PacketNonce = EightByteNonce(),
        };

        var bytes = PacketSerializer.Serialize(packet);
        var got = PacketSerializer.Deserialize(bytes);

        Assert.Equal(256, got.Ttl);
    }
}

// SPDX-License-Identifier: MIT

using System;
using System.Linq;
using AetherNet.CircuitRelay;
using AetherNet.Models;
using Xunit;

namespace AetherNet.Core.Tests;

/// <summary>
/// Wire-format tests for <see cref="RelayFrameSerializer"/>. These pin the native
/// circuit-relay-v2 binary layout that all eight language SDKs must reproduce
/// byte-for-byte (the <c>fixtures/circuit-relay/</c> corpus is generated from the
/// same cases).
/// </summary>
public class CircuitRelaySerializerTests
{
    private static RelayFrame FullyPopulated() => new()
    {
        Type = RelayMessageType.ConnectResponse,
        Status = RelayStatus.Ok,
        SourceUhid = "alice-uhid",
        DestinationUhid = "bob-uhid",
        RelayUhid = "relay-uhid",
        ConnectionId = new Guid("0102030405060708090a0b0c0d0e0f10"),
        ReservationExpiresAtMs = 1_735_689_600_000, // 2025-01-01T00:00:00Z
        LimitDurationSeconds = 120,
        LimitDataBytes = 1_048_576,
        Payload = [1, 2, 3, 4, 5],
    };

    [Fact]
    public void RoundTrip_FullyPopulated_PreservesEveryField()
    {
        var f = FullyPopulated();
        var back = RelayFrameSerializer.Deserialize(RelayFrameSerializer.Serialize(f));

        Assert.Equal(f.Type, back.Type);
        Assert.Equal(f.Status, back.Status);
        Assert.Equal(f.SourceUhid, back.SourceUhid);
        Assert.Equal(f.DestinationUhid, back.DestinationUhid);
        Assert.Equal(f.RelayUhid, back.RelayUhid);
        Assert.Equal(f.ConnectionId, back.ConnectionId);
        Assert.Equal(f.ReservationExpiresAtMs, back.ReservationExpiresAtMs);
        Assert.Equal(f.LimitDurationSeconds, back.LimitDurationSeconds);
        Assert.Equal(f.LimitDataBytes, back.LimitDataBytes);
        Assert.Equal(f.Payload, back.Payload);
    }

    [Theory]
    [InlineData(RelayMessageType.Reserve)]
    [InlineData(RelayMessageType.ReserveResponse)]
    [InlineData(RelayMessageType.Connect)]
    [InlineData(RelayMessageType.Stop)]
    [InlineData(RelayMessageType.StopResponse)]
    [InlineData(RelayMessageType.ConnectResponse)]
    [InlineData(RelayMessageType.Data)]
    public void RoundTrip_EveryMessageType(RelayMessageType type)
    {
        var f = FullyPopulated();
        f.Type = type;
        var back = RelayFrameSerializer.Deserialize(RelayFrameSerializer.Serialize(f));
        Assert.Equal(type, back.Type);
    }

    [Theory]
    [InlineData(RelayStatus.Ok)]
    [InlineData(RelayStatus.ReservationRefused)]
    [InlineData(RelayStatus.NoReservation)]
    [InlineData(RelayStatus.ResourceLimitExceeded)]
    [InlineData(RelayStatus.PermissionDenied)]
    [InlineData(RelayStatus.ConnectionFailed)]
    [InlineData(RelayStatus.MalformedMessage)]
    public void RoundTrip_EveryStatus(RelayStatus status)
    {
        var f = FullyPopulated();
        f.Status = status;
        var back = RelayFrameSerializer.Deserialize(RelayFrameSerializer.Serialize(f));
        Assert.Equal(status, back.Status);
    }

    [Fact]
    public void MinimalFrame_EmptyStrings_NoPayload_Is49Bytes()
    {
        var f = new RelayFrame { Type = RelayMessageType.Reserve };
        var bytes = RelayFrameSerializer.Serialize(f);
        Assert.Equal(49, bytes.Length);

        var back = RelayFrameSerializer.Deserialize(bytes);
        Assert.Equal(RelayMessageType.Reserve, back.Type);
        Assert.Equal(string.Empty, back.SourceUhid);
        Assert.Empty(back.Payload);
    }

    [Fact]
    public void FirstByte_IsVersion_SecondIsType()
    {
        var f = new RelayFrame { Type = RelayMessageType.Data };
        var bytes = RelayFrameSerializer.Serialize(f);
        Assert.Equal(RelayFrameSerializer.Version, bytes[0]);
        Assert.Equal((byte)RelayMessageType.Data, bytes[1]);
    }

    [Fact]
    public void ConnectionId_IsWrittenBigEndian()
    {
        var id = new Guid("00112233445566778899aabbccddeeff");
        var f = new RelayFrame { Type = RelayMessageType.Connect, ConnectionId = id };
        var bytes = RelayFrameSerializer.Serialize(f);

        // Layout: version(1) type(1) status(1) + three empty strings (2 each = 6) => connId at offset 9.
        var connIdBytes = bytes.Skip(9).Take(16).ToArray();
        var expected = new byte[]
        {
            0x00,0x11,0x22,0x33,0x44,0x55,0x66,0x77,
            0x88,0x99,0xaa,0xbb,0xcc,0xdd,0xee,0xff,
        };
        Assert.Equal(expected, connIdBytes); // RFC-4122 big-endian, not .NET mixed-endian
    }

    [Fact]
    public void UnicodeUhids_RoundTrip()
    {
        var f = FullyPopulated();
        f.SourceUhid = "нода-α";
        f.DestinationUhid = "節點-β";
        var back = RelayFrameSerializer.Deserialize(RelayFrameSerializer.Serialize(f));
        Assert.Equal("нода-α", back.SourceUhid);
        Assert.Equal("節點-β", back.DestinationUhid);
    }

    [Fact]
    public void LargePayload_RoundTrips()
    {
        var payload = Enumerable.Range(0, 200_000).Select(i => (byte)(i % 251)).ToArray();
        var f = new RelayFrame { Type = RelayMessageType.Data, Payload = payload };
        var back = RelayFrameSerializer.Deserialize(RelayFrameSerializer.Serialize(f));
        Assert.Equal(payload, back.Payload);
    }

    [Fact]
    public void Deserialize_RejectsUnknownVersion()
    {
        var bytes = RelayFrameSerializer.Serialize(new RelayFrame { Type = RelayMessageType.Reserve });
        bytes[0] = 0x02;
        Assert.Throws<FormatException>(() => RelayFrameSerializer.Deserialize(bytes));
    }

    [Theory]
    [InlineData((byte)0)]   // 0 is not a valid type
    [InlineData((byte)9)]   // past RouteAnnounce(8)
    [InlineData((byte)255)]
    public void Deserialize_RejectsInvalidType(byte badType)
    {
        var bytes = RelayFrameSerializer.Serialize(new RelayFrame { Type = RelayMessageType.Reserve });
        bytes[1] = badType;
        Assert.Throws<FormatException>(() => RelayFrameSerializer.Deserialize(bytes));
    }

    [Theory]
    [InlineData((byte)7)]   // past MalformedMessage(6)
    [InlineData((byte)200)]
    public void Deserialize_RejectsInvalidStatus(byte badStatus)
    {
        var bytes = RelayFrameSerializer.Serialize(new RelayFrame { Type = RelayMessageType.Reserve });
        bytes[2] = badStatus;
        Assert.Throws<FormatException>(() => RelayFrameSerializer.Deserialize(bytes));
    }
}

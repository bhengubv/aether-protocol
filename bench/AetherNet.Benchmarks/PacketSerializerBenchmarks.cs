// SPDX-License-Identifier: MIT

using System.Security.Cryptography;
using System.Text;
using AetherNet.Protocol;
using BenchmarkDotNet.Attributes;

namespace AetherNet.Benchmarks;

/// <summary>
/// Wire-format serializer/deserializer hot paths.
///
/// Every packet on the mesh — RouteRequest, Data, Heartbeat, voice, video —
/// runs through <see cref="PacketSerializer"/> on send and on every hop's
/// receive. A regression here multiplies across every router and every link.
///
/// Pin a baseline so future Encoding/BinaryPrimitives changes are visible.
/// </summary>
[MemoryDiagnoser]
public class PacketSerializerBenchmarks
{
    private const string SourceUhid = "alice-uhid-0001";
    private const string DestUhid = "bob-uhid-0002";

    private MeshPacket _basicPacket = null!;
    private MeshPacket _largePacket = null!;
    private byte[] _basicWire = null!;
    private byte[] _largeWire = null!;

    [GlobalSetup]
    public void Setup()
    {
        _basicPacket = MakePacket(payloadSize: 50);
        _largePacket = MakePacket(payloadSize: 4096);
        _basicWire = PacketSerializer.Serialize(_basicPacket);
        _largeWire = PacketSerializer.Serialize(_largePacket);
    }

    private static MeshPacket MakePacket(int payloadSize)
    {
        return new MeshPacket
        {
            Id = Guid.NewGuid(),
            Type = PacketType.Data,
            SourceUhid = SourceUhid,
            DestinationUhid = DestUhid,
            Ttl = 7,
            Priority = 1,
            ProtocolVersion = 2,
            TimestampMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            PacketNonce = RandomNumberGenerator.GetBytes(8),
            Payload = RandomNumberGenerator.GetBytes(payloadSize),
            // 64-byte Ed25519 signature — same shape as a real signed packet.
            Signature = RandomNumberGenerator.GetBytes(64),
        };
    }

    [Benchmark]
    public byte[] Serialize_BasicData() => PacketSerializer.Serialize(_basicPacket);

    [Benchmark]
    public byte[] Serialize_LargeData() => PacketSerializer.Serialize(_largePacket);

    [Benchmark]
    public MeshPacket Deserialize_BasicData() => PacketSerializer.Deserialize(_basicWire);

    [Benchmark]
    public MeshPacket Deserialize_LargeData() => PacketSerializer.Deserialize(_largeWire);

    [Benchmark]
    public MeshPacket RoundTrip_BasicData()
    {
        var wire = PacketSerializer.Serialize(_basicPacket);
        return PacketSerializer.Deserialize(wire);
    }
}

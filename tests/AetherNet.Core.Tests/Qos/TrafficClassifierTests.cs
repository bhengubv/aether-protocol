// SPDX-License-Identifier: MIT

using AetherNet.Protocol;
using AetherNet.Qos;
using Xunit;

namespace AetherNet.Core.Tests.Qos;

/// <summary>
/// The default classifier maps a packet to a lane from app-blind signals (SOS priority + a coarse type
/// category). It is a local scheduling hint only — never placed on the wire.
/// </summary>
public class TrafficClassifierTests
{
    [Theory]
    [InlineData(PacketType.SosBroadcast, TrafficClass.Emergency)]
    [InlineData(PacketType.RouteRequest, TrafficClass.Control)]
    [InlineData(PacketType.Hello, TrafficClass.Control)]
    [InlineData(PacketType.NameQuery, TrafficClass.Control)]
    [InlineData(PacketType.Heartbeat, TrafficClass.Control)]
    [InlineData(PacketType.VoiceCall, TrafficClass.Realtime)]
    [InlineData(PacketType.VideoFrame, TrafficClass.Realtime)]
    [InlineData(PacketType.ScreenShare, TrafficClass.Realtime)]
    [InlineData(PacketType.StreamSegment, TrafficClass.Realtime)]
    [InlineData(PacketType.ChunkData, TrafficClass.Bulk)]
    [InlineData(PacketType.TorrentMetadata, TrafficClass.Bulk)]
    [InlineData(PacketType.DtnBundle, TrafficClass.Bulk)]
    [InlineData(PacketType.Data, TrafficClass.Standard)]
    [InlineData(PacketType.ChannelMessage, TrafficClass.Standard)]
    [InlineData(PacketType.TipPacket, TrafficClass.Standard)]
    public void Classify_MapsTypeToItsLane(PacketType type, TrafficClass expected)
        => Assert.Equal(expected, TrafficClassifier.Classify(0, type));

    [Fact]
    public void SosPriority_IsEmergency_RegardlessOfType()
        => Assert.Equal(TrafficClass.Emergency, TrafficClassifier.Classify(255, PacketType.ChunkData));

    [Fact]
    public void Classify_MeshPacket_UsesPriorityAndType()
    {
        var realtime = new MeshPacket { Type = PacketType.VideoFrame, Priority = 0 };
        Assert.Equal(TrafficClass.Realtime, TrafficClassifier.Classify(realtime));

        var sos = new MeshPacket { Type = PacketType.ChunkData, Priority = 255 };
        Assert.Equal(TrafficClass.Emergency, TrafficClassifier.Classify(sos));
    }
}

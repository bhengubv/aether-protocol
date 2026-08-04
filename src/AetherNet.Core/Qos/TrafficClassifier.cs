// SPDX-License-Identifier: MIT

using System;
using AetherNet.Protocol;

namespace AetherNet.Qos;

/// <summary>
/// Assigns a default <see cref="TrafficClass"/> to a packet from generic, app-blind signals — the SOS
/// priority marker plus a coarse category of the packet type (control-plane / interactive-media / bulk).
/// It runs LOCALLY to pick the sending node's own outbound lane and the class is never written to the
/// wire. A sending service that knows its own intent can always choose the lane explicitly instead.
/// </summary>
public static class TrafficClassifier
{
    /// <summary>The SOS priority sentinel (highest urgency).</summary>
    public const byte EmergencyPriority = 255;

    /// <summary>Classify a packet's default outbound lane from its priority byte and type.</summary>
    public static TrafficClass Classify(byte priority, PacketType type)
    {
        // Life-safety always wins, whatever the type.
        if (priority == EmergencyPriority || type is PacketType.SosBroadcast or PacketType.SosAck)
        {
            return TrafficClass.Emergency;
        }

        return type switch
        {
            // Control plane — routing, keepalives, ACKs, discovery, handshakes, measurement, stream setup.
            PacketType.RouteRequest or PacketType.RouteReply or PacketType.Ack or PacketType.Heartbeat
                or PacketType.DtnCustodyAck or PacketType.DtnDeliveryReceipt
                or PacketType.PresenceBeacon or PacketType.PresenceQuery
                or PacketType.PreKeyRequest or PacketType.PreKeyResponse
                or PacketType.StreamAnnounce or PacketType.StreamSubscribe or PacketType.StreamUnsubscribe
                or PacketType.NamePublish or PacketType.NameQuery
                or PacketType.ForgeAnnounce or PacketType.VaultShardRequest or PacketType.PoVTokenExchange
                or PacketType.Hello or PacketType.HelloAck or PacketType.ReputationUpdate
                or PacketType.BandwidthProbe or PacketType.BandwidthAck or PacketType.BandwidthGossip
                or PacketType.EridAnnounce or PacketType.CircuitRelayControl
                => TrafficClass.Control,

            // Latency-sensitive interactive media.
            PacketType.StreamSegment or PacketType.StreamAbandon
                or PacketType.VoicePtt or PacketType.VoiceCall or PacketType.VoiceSignaling
                or PacketType.VideoCall or PacketType.VideoSignaling or PacketType.GroupVideoSignaling
                or PacketType.WatchSync or PacketType.WatchReaction
                or PacketType.VideoFrame or PacketType.ScreenShare
                => TrafficClass.Realtime,

            // Throughput-oriented, latency-tolerant bulk.
            PacketType.ChunkRequest or PacketType.ChunkData or PacketType.ChunkBitmap
                or PacketType.WatchChunkRequest or PacketType.TorrentMetadata
                or PacketType.DtnBundle or PacketType.SpaceBreadcrumb
                => TrafficClass.Bulk,

            // Everything else — normal application traffic.
            _ => TrafficClass.Standard,
        };
    }

    /// <summary>Classify a <see cref="MeshPacket"/> by its priority + type.</summary>
    public static TrafficClass Classify(MeshPacket packet)
    {
        ArgumentNullException.ThrowIfNull(packet);
        return Classify(packet.Priority, packet.Type);
    }
}

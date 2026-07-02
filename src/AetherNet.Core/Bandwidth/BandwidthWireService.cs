// SPDX-License-Identifier: MIT

using System.Buffers.Binary;
using AetherNet.Constants;
using AetherNet.Protocol;
using AetherNet.Routing;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AetherNet.Bandwidth;

/// <summary>A latency/throughput probe request (PacketType.BandwidthProbe = 53 body).</summary>
public sealed record BandwidthProbe(uint Sequence, long SenderSendUs);

/// <summary>Event args: an inbound probe plus the peer that sent it (so the host can reply with an ack).</summary>
public sealed record BandwidthProbeReceived(BandwidthProbe Probe, string FromUhid);

/// <summary>
/// Binary wire codec for the three ABMF packets. All multi-byte integers are little-endian, matching
/// the packet-serializer convention. NO version byte — the layouts are the ones documented on the
/// <see cref="PacketType"/> members. Byte-identity gate: fixtures/bandwidth/vectors.json (hex).
///   Probe(53)  : sequence u32 | sender_send_us i64                                              (12 B)
///   Ack(54)    : sequence u32 | sender_send_us i64 | receiver_receive_us i64 | receiver_send_us i64 | probe_bytes i32 (32 B)
///   Gossip(55) : btlbw_bps i64 | rtprop_us i32 | confidence u8                                   (13 B)
/// SenderReceiveUs is NOT on the wire — the prober fills it locally on receipt. PeerUhid/TransportName/
/// MeasuredAt of a gossip come from the enclosing packet + local clock, not the wire body.
/// </summary>
public static class BandwidthWireCodec
{
    public static byte[] SerializeProbe(BandwidthProbe p)
    {
        var buf = new byte[12];
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(0), p.Sequence);
        BinaryPrimitives.WriteInt64LittleEndian(buf.AsSpan(4), p.SenderSendUs);
        return buf;
    }

    public static BandwidthProbe DeserializeProbe(ReadOnlySpan<byte> b)
    {
        if (b.Length < 12) throw new FormatException("BandwidthProbe payload too short");
        return new BandwidthProbe(
            BinaryPrimitives.ReadUInt32LittleEndian(b[..4]),
            BinaryPrimitives.ReadInt64LittleEndian(b.Slice(4, 8)));
    }

    public static byte[] SerializeAck(BandwidthProbeAck a)
    {
        var buf = new byte[32];
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(0), a.Sequence);
        BinaryPrimitives.WriteInt64LittleEndian(buf.AsSpan(4), a.SenderSendUs);
        BinaryPrimitives.WriteInt64LittleEndian(buf.AsSpan(12), a.ReceiverReceiveUs);
        BinaryPrimitives.WriteInt64LittleEndian(buf.AsSpan(20), a.ReceiverSendUs);
        BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan(28), a.ProbeBytes);
        return buf;
    }

    public static BandwidthProbeAck DeserializeAck(ReadOnlySpan<byte> b)
    {
        if (b.Length < 32) throw new FormatException("BandwidthProbeAck payload too short");
        return new BandwidthProbeAck(
            BinaryPrimitives.ReadUInt32LittleEndian(b[..4]),
            BinaryPrimitives.ReadInt64LittleEndian(b.Slice(4, 8)),
            BinaryPrimitives.ReadInt64LittleEndian(b.Slice(12, 8)),
            BinaryPrimitives.ReadInt64LittleEndian(b.Slice(20, 8)),
            0L, // SenderReceiveUs — filled by the prober on receipt, not carried on the wire
            BinaryPrimitives.ReadInt32LittleEndian(b.Slice(28, 4)));
    }

    public static byte[] SerializeGossip(BandwidthGossipPayload g)
    {
        var buf = new byte[13];
        BinaryPrimitives.WriteInt64LittleEndian(buf.AsSpan(0), g.BtlBwBps);
        BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan(8), (int)Math.Clamp(g.RtPropUs, 0, int.MaxValue));
        buf[12] = (byte)g.Confidence;
        return buf;
    }

    /// <summary>Decode a gossip body. PeerUhid/TransportName default to empty; the service fills PeerUhid from the packet.</summary>
    public static BandwidthGossipPayload DeserializeGossip(ReadOnlySpan<byte> b)
    {
        if (b.Length < 13) throw new FormatException("BandwidthGossipPayload payload too short");
        return new BandwidthGossipPayload(
            PeerUhid: string.Empty,
            TransportName: string.Empty,
            BtlBwBps: BinaryPrimitives.ReadInt64LittleEndian(b[..8]),
            RtPropUs: BinaryPrimitives.ReadInt32LittleEndian(b.Slice(8, 4)),
            Confidence: (BandwidthConfidence)b[12],
            MeasuredAt: default);
    }
}

/// <summary>
/// Binds the three ABMF PacketTypes to the mesh: send probes (directed) + their acks (directed reply),
/// and broadcast/receive warm-start gossip. Inbound packets surface via events; the host feeds them into
/// <c>IBandwidthEstimator</c> (RecordProbeResult / WarmFromGossip) and replies to probes.
/// </summary>
public interface IBandwidthWireService
{
    event EventHandler<BandwidthProbeReceived>? ProbeReceived;
    event EventHandler<BandwidthProbeAck>? AckReceived;
    event EventHandler<BandwidthGossipPayload>? GossipReceived;

    /// <summary>Send a directed <see cref="PacketType.BandwidthProbe"/> to a peer.</summary>
    Task<bool> SendProbeAsync(string peerUhid, BandwidthProbe probe, CancellationToken cancellationToken = default);

    /// <summary>Send a directed <see cref="PacketType.BandwidthAck"/> reply to the prober.</summary>
    Task<bool> SendAckAsync(string peerUhid, BandwidthProbeAck ack, CancellationToken cancellationToken = default);

    /// <summary>Broadcast a <see cref="PacketType.BandwidthGossip"/> warm-start estimate. Returns peers reached.</summary>
    Task<int> BroadcastGossipAsync(BandwidthGossipPayload gossip, CancellationToken cancellationToken = default);

    /// <summary>Dispatch an inbound bandwidth packet to the matching event. False on wrong type or malformed body.</summary>
    Task<bool> HandleAsync(MeshPacket packet, CancellationToken cancellationToken = default);
}

/// <inheritdoc />
public sealed class BandwidthWireService : IBandwidthWireService
{
    private readonly IMeshSender _sender;
    private readonly ILogger<BandwidthWireService> _logger;

    public event EventHandler<BandwidthProbeReceived>? ProbeReceived;
    public event EventHandler<BandwidthProbeAck>? AckReceived;
    public event EventHandler<BandwidthGossipPayload>? GossipReceived;

    public BandwidthWireService(IMeshSender sender, ILogger<BandwidthWireService>? logger = null)
    {
        _sender = sender ?? throw new ArgumentNullException(nameof(sender));
        _logger = logger ?? NullLogger<BandwidthWireService>.Instance;
    }

    /// <inheritdoc />
    public Task<bool> SendProbeAsync(string peerUhid, BandwidthProbe probe, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(peerUhid);
        ArgumentNullException.ThrowIfNull(probe);
        return SendDirectedAsync(peerUhid, PacketType.BandwidthProbe, BandwidthWireCodec.SerializeProbe(probe), cancellationToken);
    }

    /// <inheritdoc />
    public Task<bool> SendAckAsync(string peerUhid, BandwidthProbeAck ack, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(peerUhid);
        ArgumentNullException.ThrowIfNull(ack);
        return SendDirectedAsync(peerUhid, PacketType.BandwidthAck, BandwidthWireCodec.SerializeAck(ack), cancellationToken);
    }

    private async Task<bool> SendDirectedAsync(string peerUhid, PacketType type, byte[] payload, CancellationToken cancellationToken)
    {
        var packet = new MeshPacket
        {
            Type = type,
            SourceUhid = _sender.LocalUhid,
            DestinationUhid = peerUhid,
            Ttl = ProtocolConstants.DefaultTtl,
            Payload = payload,
        };
        return await _sender.SendAsync(packet, peerUhid, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<int> BroadcastGossipAsync(BandwidthGossipPayload gossip, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(gossip);
        var packet = new MeshPacket
        {
            Type = PacketType.BandwidthGossip,
            SourceUhid = _sender.LocalUhid,
            DestinationUhid = "*",
            Ttl = ProtocolConstants.DefaultTtl,
            Payload = BandwidthWireCodec.SerializeGossip(gossip),
        };
        return await _sender.BroadcastAsync(packet, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public Task<bool> HandleAsync(MeshPacket packet, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(packet);
        try
        {
            switch (packet.Type)
            {
                case PacketType.BandwidthProbe:
                    var probe = BandwidthWireCodec.DeserializeProbe(packet.Payload);
                    ProbeReceived?.Invoke(this, new BandwidthProbeReceived(probe, packet.SourceUhid));
                    return Task.FromResult(true);

                case PacketType.BandwidthAck:
                    var ack = BandwidthWireCodec.DeserializeAck(packet.Payload);
                    AckReceived?.Invoke(this, ack);
                    return Task.FromResult(true);

                case PacketType.BandwidthGossip:
                    var gossip = BandwidthWireCodec.DeserializeGossip(packet.Payload) with { PeerUhid = packet.SourceUhid };
                    GossipReceived?.Invoke(this, gossip);
                    return Task.FromResult(true);

                default:
                    return Task.FromResult(false);
            }
        }
        catch (FormatException ex)
        {
            _logger.LogDebug(ex, "Bandwidth {Type} from {Source}: malformed payload — dropped", packet.Type, packet.SourceUhid);
            return Task.FromResult(false);
        }
    }
}

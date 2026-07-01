// SPDX-License-Identifier: MIT

using AetherNet.Protocol;
using AetherNet.Transport.Abstractions;

namespace AetherNet.Transport.CircuitRelay;

/// <summary>
/// Production <see cref="IRelayLink"/> that carries circuit-relay-v2 frames one hop over the
/// real mesh. Each frame is wrapped in a <see cref="MeshPacket"/> of type
/// <see cref="PacketType.CircuitRelayControl"/> and handed to the host's send-to-connected-peer
/// path; inbound CircuitRelayControl packets are fed back in via <see cref="HandleIncomingPacket"/>.
///
/// <para>The two delegates are the seam to whatever real transport layer the host runs
/// (BLE / Wi-Fi Direct / WebRTC / the HTTP relay, routed by <c>TransportManager</c>): one sends a
/// packet to a <em>directly-connected</em> peer, the other reports direct reachability. This keeps
/// the relay engine transport-agnostic — it never calls a radio directly, and it never recurses
/// back through itself (the host's one-hop send must exclude the circuit-relay transport).</para>
/// </summary>
public sealed class MeshRelayLink : IRelayLink
{
    private readonly string _localUhid;
    private readonly Func<MeshPacket, CancellationToken, Task<bool>> _sendOneHop;
    private readonly Func<string, bool> _canReach;

    /// <param name="localUhid">This node's UHID (stamped as the packet source).</param>
    /// <param name="sendOneHop">Sends a MeshPacket to a directly-connected peer; returns true if handed off.</param>
    /// <param name="canReach">Reports whether this node has a direct one-hop link to a peer.</param>
    public MeshRelayLink(
        string localUhid,
        Func<MeshPacket, CancellationToken, Task<bool>> sendOneHop,
        Func<string, bool> canReach)
    {
        _localUhid = localUhid ?? throw new ArgumentNullException(nameof(localUhid));
        _sendOneHop = sendOneHop ?? throw new ArgumentNullException(nameof(sendOneHop));
        _canReach = canReach ?? throw new ArgumentNullException(nameof(canReach));
    }

    /// <inheritdoc />
    public event Action<string, byte[]>? FrameReceived;

    /// <inheritdoc />
    public bool CanReach(string nodeUhid) => _canReach(nodeUhid);

    /// <inheritdoc />
    public Task<bool> SendFrameAsync(string nodeUhid, byte[] frame, CancellationToken cancellationToken = default)
    {
        var packet = new MeshPacket
        {
            Type = PacketType.CircuitRelayControl,
            SourceUhid = _localUhid,
            DestinationUhid = nodeUhid,
            Payload = frame,
            Ttl = 1, // relay frames travel exactly one hop; end-to-end routing is the engine's job
        };
        return _sendOneHop(packet, cancellationToken);
    }

    /// <summary>
    /// Feeds an inbound <see cref="PacketType.CircuitRelayControl"/> packet from the host's receive
    /// path into the relay engine. The host must call this for every received CircuitRelayControl
    /// packet (non-relay packet types are ignored).
    /// </summary>
    public void HandleIncomingPacket(MeshPacket packet)
    {
        ArgumentNullException.ThrowIfNull(packet);
        if (packet.Type != PacketType.CircuitRelayControl) return;
        FrameReceived?.Invoke(packet.SourceUhid, packet.Payload ?? []);
    }
}

/// <summary>
/// Wires a <see cref="CircuitRelayTransportService"/> onto a <see cref="MeshRelayLink"/>. The host:
/// (1) registers the returned <see cref="ITransportService"/> with the mesh — <c>TransportManager</c>
/// includes it automatically via its <c>additionalTransports</c> parameter, at
/// <see cref="CircuitRelayTransportService.PowerCostRelative"/> 90 (just below the HTTP relay); and
/// (2) routes every received <see cref="PacketType.CircuitRelayControl"/> packet to the returned
/// link's <see cref="MeshRelayLink.HandleIncomingPacket"/>.
/// </summary>
public static class MeshCircuitRelay
{
    /// <summary>Creates the relay transport + its mesh link.</summary>
    public static (CircuitRelayTransportService Transport, MeshRelayLink Link) Create(
        string localUhid,
        Func<MeshPacket, CancellationToken, Task<bool>> sendOneHop,
        Func<string, bool> canReach,
        CircuitRelayOptions? options = null)
    {
        var link = new MeshRelayLink(localUhid, sendOneHop, canReach);
        var transport = new CircuitRelayTransportService(localUhid, link, options);
        return (transport, link);
    }
}

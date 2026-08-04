// SPDX-License-Identifier: MIT

using System;
using System.Threading;
using System.Threading.Tasks;
using AetherNet.Models;
using AetherNet.Protocol;

namespace AetherNet.CircuitRelay;

/// <summary>
/// Native relay discovery — the cold-start piece that populates circuit-relay routes <em>from the mesh</em>
/// instead of by hand. A NAT'd node that has reserved capacity on a relay it can reach broadcasts a
/// <see cref="RelayMessageType.RouteAnnounce"/> ("reach me via relay R, until expiry"); any node that hears
/// it learns dest→relay, so when it later needs that unreachable peer it already knows which relay to
/// CONNECT through. This is the "directory / reservation gossip" that
/// <c>CircuitRelayTransportService.SetRoute</c> refers to — decentralised and native: relay-capable nodes
/// are found via the advertised <c>NodeCapabilities.Relay</c> bit, and the route comes from this gossip —
/// no external bootstrap.
///
/// <para>Transport-agnostic. The host supplies a <paramref name="broadcast"/> delegate (wrap
/// <c>IMeshSender.BroadcastAsync</c> in a <see cref="PacketType.CircuitRelayControl"/> packet) and an
/// <c>onRouteLearned</c> callback (wire it to <c>CircuitRelayTransportService.SetRoute</c>). Inbound
/// CircuitRelayControl packets are fed in via <see cref="Handle"/>.</para>
/// </summary>
public sealed class RelayDiscoveryService
{
    private readonly string _localUhid;
    private readonly Func<byte[], CancellationToken, Task> _broadcast;
    private readonly Action<string, string, long> _onRouteLearned;
    private readonly Func<DateTimeOffset> _now;

    /// <param name="localUhid">This node's UHID (announcements name it as the reachable target).</param>
    /// <param name="broadcast">Broadcasts a serialized frame to the mesh (host wraps it in a CircuitRelayControl packet).</param>
    /// <param name="onRouteLearned">Invoked (targetUhid, relayUhid, expiryMs) when a fresh route is learned — wire to SetRoute.</param>
    /// <param name="now">Clock (injectable for deterministic expiry tests).</param>
    public RelayDiscoveryService(
        string localUhid,
        Func<byte[], CancellationToken, Task> broadcast,
        Action<string, string, long> onRouteLearned,
        Func<DateTimeOffset>? now = null)
    {
        _localUhid = localUhid ?? throw new ArgumentNullException(nameof(localUhid));
        _broadcast = broadcast ?? throw new ArgumentNullException(nameof(broadcast));
        _onRouteLearned = onRouteLearned ?? throw new ArgumentNullException(nameof(onRouteLearned));
        _now = now ?? (() => DateTimeOffset.UtcNow);
    }

    /// <summary>
    /// Announce to the mesh that this node is reachable via <paramref name="relayUhid"/> until
    /// <paramref name="reservationExpiresAtMs"/>. Call after a successful reservation on that relay.
    /// </summary>
    public Task AnnounceReachabilityAsync(string relayUhid, long reservationExpiresAtMs, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(relayUhid);
        var frame = new RelayFrame
        {
            Type = RelayMessageType.RouteAnnounce,
            SourceUhid = _localUhid,
            RelayUhid = relayUhid,
            ReservationExpiresAtMs = reservationExpiresAtMs,
        };
        return _broadcast(RelayFrameSerializer.Serialize(frame), cancellationToken);
    }

    /// <summary>
    /// Feed an inbound packet. If it is a fresh <see cref="RelayMessageType.RouteAnnounce"/> from another
    /// node, invoke the route-learned callback and return true. Our own announcements, stale (expired)
    /// ones, and non-relay packets are ignored.
    /// </summary>
    public bool Handle(MeshPacket packet)
    {
        ArgumentNullException.ThrowIfNull(packet);
        if (packet.Type != PacketType.CircuitRelayControl || packet.Payload is null || packet.Payload.Length == 0)
            return false;

        RelayFrame frame;
        try { frame = RelayFrameSerializer.Deserialize(packet.Payload); }
        catch { return false; }

        if (frame.Type != RelayMessageType.RouteAnnounce) return false;
        if (string.IsNullOrEmpty(frame.SourceUhid) || string.IsNullOrEmpty(frame.RelayUhid)) return false;
        if (string.Equals(frame.SourceUhid, _localUhid, StringComparison.Ordinal)) return false; // our own echo
        if (frame.ReservationExpiresAtMs > 0 && frame.ReservationExpiresAtMs <= _now().ToUnixTimeMilliseconds())
            return false; // reservation already expired

        _onRouteLearned(frame.SourceUhid, frame.RelayUhid, frame.ReservationExpiresAtMs);
        return true;
    }
}

// SPDX-License-Identifier: MIT

using AetherNet.Identity;
using AetherNet.Protocol;

namespace AetherNet.Routing;

/// <summary>
/// Puts a rotating ERID on the wire in place of the stable UHID, and takes it back off on receipt —
/// the E2 header swap.
///
/// <para>
/// A packet's <see cref="MeshPacket.SourceUhid"/> / <see cref="MeshPacket.DestinationUhid"/> are the one
/// piece of a message a passive observer can read even though the body is sealed: the UHID is stable and
/// phone-derived, so it is a lifelong tracking + targeting handle. This rewrites those two fields to the
/// sender's and recipient's <b>current-epoch ERIDs</b> on the way out, and <see cref="EridRouteResolver"/>
/// / this codec resolves them back to the stable identity on the way in — so the routing table,
/// reputation and the app all still see the long-term UHID, while the wire shows only an address that
/// rotates every epoch and cannot be linked across epochs.
/// </para>
///
/// <para>
/// <b>Gated, and fail-safe.</b> The swap only happens when the destination peer has negotiated the
/// <c>erid-routing</c> capability (a peer that has not would receive an address it cannot resolve) AND
/// its routing key is already known (so its ERID can be derived). If either is untrue —
/// <see cref="ToWire"/> returns false and the packet keeps its stable UHIDs, exactly as before. This is
/// why bootstrap traffic (the handshake, the ERID announce itself) is safe: those flow before a routing
/// key is known, so they are never swapped. Nothing on the wire changes for a peer that hasn't opted in.
/// </para>
/// </summary>
public sealed class EridHeaderCodec
{
    private readonly EridDirectory _directory;
    private readonly string _myUhid;
    private readonly int _epochSeconds;
    private readonly Func<long> _nowUnixSeconds;

    /// <param name="directory">This node's ERID directory (its own routing key + remembered peers').</param>
    /// <param name="myUhid">This node's stable UHID, restored into a received packet's destination field.</param>
    /// <param name="epochSeconds">Rotation window; must match the ERID epoch (default 15 min).</param>
    /// <param name="nowUnixSeconds">Clock, injectable for tests. Defaults to wall-clock UTC seconds.</param>
    public EridHeaderCodec(
        EridDirectory directory, string myUhid,
        int epochSeconds = EphemeralRoutingId.DefaultEpochSeconds, Func<long>? nowUnixSeconds = null)
    {
        ArgumentNullException.ThrowIfNull(directory);
        ArgumentException.ThrowIfNullOrEmpty(myUhid);
        if (epochSeconds <= 0)
            throw new ArgumentOutOfRangeException(nameof(epochSeconds), "epochSeconds must be positive.");
        _directory = directory;
        _myUhid = myUhid;
        _epochSeconds = epochSeconds;
        _nowUnixSeconds = nowUnixSeconds ?? (() => DateTimeOffset.UtcNow.ToUnixTimeSeconds());
    }

    /// <summary>
    /// Rewrite a packet's source and destination to ERIDs before it goes on the wire. Swaps only when
    /// <paramref name="peerSpeaksErid"/> is true (the destination negotiated <c>erid-routing</c>) and the
    /// destination's routing key is known. Returns true if it swapped; false leaves the packet untouched.
    /// </summary>
    public bool ToWire(MeshPacket packet, bool peerSpeaksErid)
    {
        ArgumentNullException.ThrowIfNull(packet);
        if (!peerSpeaksErid) return false;
        if (string.IsNullOrEmpty(packet.DestinationUhid)) return false;

        var now = _nowUnixSeconds();
        var peerErid = _directory.EridForPeer(packet.DestinationUhid, now);
        if (peerErid is null) return false; // routing key not known yet — keep the stable UHID (bootstrap)

        packet.DestinationUhid = peerErid;
        packet.SourceUhid = _directory.MyErid(now);
        return true;
    }

    /// <summary>
    /// Resolve a received packet's source and destination ERIDs back to stable UHIDs, in place. A field
    /// that is not a recognised ERID (a plain UHID, or an unknown address) is left unchanged, so a mixed
    /// or pre-swap wire is handled transparently.
    /// </summary>
    public void FromWire(MeshPacket packet)
    {
        ArgumentNullException.ThrowIfNull(packet);
        var now = _nowUnixSeconds();

        // Try the current epoch and one either side: a packet in flight across an epoch boundary carries
        // an ERID from the epoch it was sent in, which need not be the one it arrives in.
        var source = _directory.ResolvePeer(packet.SourceUhid, now)
            ?? _directory.ResolvePeer(packet.SourceUhid, now - _epochSeconds)
            ?? _directory.ResolvePeer(packet.SourceUhid, now + _epochSeconds);
        if (source is not null) packet.SourceUhid = source;

        // The destination is this node when the packet reached it — if it carries our own current ERID
        // (allowing one epoch of clock skew either way), restore the stable UHID so "is this for me?"
        // checks and local addressing keep working.
        if (IsMyErid(packet.DestinationUhid, now)) packet.DestinationUhid = _myUhid;
    }

    private bool IsMyErid(string? address, long now)
    {
        if (string.IsNullOrEmpty(address)) return false;
        return string.Equals(address, _directory.MyErid(now), StringComparison.Ordinal)
            || string.Equals(address, _directory.MyErid(now - _epochSeconds), StringComparison.Ordinal)
            || string.Equals(address, _directory.MyErid(now + _epochSeconds), StringComparison.Ordinal);
    }
}

// SPDX-License-Identifier: MIT

using System.Collections.Concurrent;
using AetherNet.Protocol;

namespace AetherNet.Routing;

/// <summary>
/// Carrying somebody else's traffic — the carry-for-a-third-node decision engine that turns a set of
/// point-to-point links into a mesh.
///
/// <para>
/// This is the thing that makes a mesh a mesh, and for a long time it did not exist in the library at
/// all: <see cref="RoutingService"/> forwards only its own route-control packets, and
/// <c>MessagingService</c> deliberately drops DATA not addressed to the local node ("forwarding is the
/// routing layer's job"). Every host that wanted real relaying re-implemented it; the most complete
/// version lived in the Android sample. This is that engine, promoted into the library so one tested
/// implementation serves every host. (The sample's copy is superseded by this one and removed once the
/// app is migrated onto the shared <see cref="MeshInboundDispatcher"/>.)
/// </para>
///
/// <h3>Who it carries for</h3>
/// <para>
/// Only endpoints this node recognises, in <b>both</b> directions — the sender must be somebody known and
/// so must the recipient. That is the difference between a mesh and an open relay: a node that forwards
/// for anyone is a node anyone can use to flood a network, and a node that forwards TO anyone is a way to
/// reach people who never agreed to be reachable. Recognition is the caller's job (it holds the routing
/// keys / contact set); this engine only decides, given who the ends turned out to be, whether to carry.
/// The payload is never read — it is sealed end-to-end and this node holds no key to it.
/// </para>
/// </summary>
public sealed class MeshRelay
{
    /// <summary>What to do with a packet that has just arrived.</summary>
    public enum Verdict
    {
        /// <summary>Addressed to this node. Deliver it upstairs; do not forward.</summary>
        ForMe,

        /// <summary>Its hops are spent. It goes no further.</summary>
        Expired,

        /// <summary>Already carried once. Dropping it is what stops a loop becoming a storm.</summary>
        AlreadyCarried,

        /// <summary>From or to somebody this node has not recognised. Not ours to carry.</summary>
        NotOurs,

        /// <summary>Pass it on, one hop shorter.</summary>
        Carry,
    }

    /// <summary>What was decided, and for whom.</summary>
    /// <param name="To">
    ///   The recognised recipient identity to forward to, set only on <see cref="Verdict.Carry"/>. A
    ///   person, not a wire address: the address may rotate every epoch and the person does not, so a
    ///   route held by address would go stale.
    /// </param>
    public readonly record struct Decision(Verdict Verdict, string? To = null)
    {
        public bool ShouldCarry => Verdict == Verdict.Carry;
    }

    /// <summary>How many packet ids are remembered to catch a loop.</summary>
    public const int Remembered = 512;

    /// <summary>How long a packet id is remembered for. Short deliberately — a legitimate resend an hour
    /// later is not a loop.</summary>
    public static readonly TimeSpan Memory = TimeSpan.FromMinutes(2);

    private readonly ConcurrentDictionary<Guid, DateTimeOffset> _carried = new();

    /// <summary>How many packets this node has passed on for other people.</summary>
    public long Carried { get; private set; }

    /// <summary>
    /// Decide what happens to a packet that arrived over a link.
    /// </summary>
    /// <param name="packet">What arrived.</param>
    /// <param name="addressedToMe">
    ///   Whether the destination is one of this node's own (possibly rotating) addresses. The caller
    ///   answers it because only the caller holds the key material to resolve it.
    /// </param>
    /// <param name="fromTag">Who the sender turned out to be, or null if nobody recognised.</param>
    /// <param name="toTag">Who the recipient turns out to be, or null if nobody recognised.</param>
    public Decision Look(MeshPacket packet, bool addressedToMe, string? fromTag, string? toTag,
        DateTimeOffset? now = null)
    {
        ArgumentNullException.ThrowIfNull(packet);

        // Ours first, and before the loop guard: a packet for this node is delivered every time it
        // arrives, even if a copy came round some other way a moment ago. Deduplication of delivered
        // messages is the job of the layer that understands them.
        if (addressedToMe) return new Decision(Verdict.ForMe);

        // A packet with no destination is addressed to whoever can hear it — presence, hellos, the
        // announcements a mesh runs on. Delivered upstairs, never carried: forwarding something
        // addressed to nobody in particular is how one hello becomes a broadcast storm.
        if (string.IsNullOrEmpty(packet.DestinationUhid)) return new Decision(Verdict.ForMe);

        if (!packet.CanForward) return new Decision(Verdict.Expired);

        // Both ends have to be recognised. One-sided would be enough to carry the bytes and is exactly
        // how a node becomes an open relay.
        if (fromTag is null || toTag is null) return new Decision(Verdict.NotOurs);

        // Never carry something back to the person who sent it.
        if (string.Equals(fromTag, toTag, StringComparison.Ordinal)) return new Decision(Verdict.NotOurs);

        var at = now ?? DateTimeOffset.UtcNow;
        if (!Remember(packet.Id, at)) return new Decision(Verdict.AlreadyCarried);

        Carried++;
        return new Decision(Verdict.Carry, toTag);
    }

    /// <summary>
    /// The packet as it should leave, one hop shorter. A copy rather than an edit in place — the packet
    /// that arrived belongs to whatever else is looking at it.
    /// </summary>
    public static MeshPacket OneHopShorter(MeshPacket packet)
    {
        ArgumentNullException.ThrowIfNull(packet);

        return new MeshPacket
        {
            // The id travels unchanged — it is what every other node on the path uses to recognise this
            // as a packet it has already carried. A fresh id per hop would turn one loop into a flood.
            Id = packet.Id,
            ProtocolVersion = packet.ProtocolVersion,
            Type = packet.Type,
            Priority = packet.Priority,
            TimestampMs = packet.TimestampMs,
            SourceUhid = packet.SourceUhid,
            DestinationUhid = packet.DestinationUhid,
            PacketNonce = packet.PacketNonce,
            Signature = packet.Signature,
            // Untouched. Sealed under a session between the two ends; this node has no key to it — which
            // is the whole reason carrying for somebody costs them no privacy.
            Payload = packet.Payload,
            Ttl = packet.Ttl - 1,
        };
    }

    /// <summary>Note a packet id, or say that we have already seen it.</summary>
    private bool Remember(Guid id, DateTimeOffset at)
    {
        if (_carried.TryGetValue(id, out var when) && at - when < Memory) return false;

        _carried[id] = at;

        if (_carried.Count > Remembered)
        {
            foreach (var (seen, stamped) in _carried)
                if (at - stamped >= Memory) _carried.TryRemove(seen, out _);

            // Still full of recent ids — a genuinely busy node rather than a stale table. Forgetting the
            // lot costs at most one duplicate carried per peer, far better than growing without bound.
            if (_carried.Count > Remembered) _carried.Clear();
        }

        return true;
    }

    /// <summary>Forget everything. The links are going down.</summary>
    public void Clear() => _carried.Clear();
}

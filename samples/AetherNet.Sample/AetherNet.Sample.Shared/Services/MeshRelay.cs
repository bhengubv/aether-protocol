// SPDX-License-Identifier: MIT

using AetherNet.Protocol;
using System.Collections.Concurrent;

namespace AetherNet.Sample.Shared.Services;

/// <summary>
/// Carrying somebody else's traffic.
///
/// <para>
/// This is the thing that makes a mesh a mesh, and until now it did not exist. Packets have carried a
/// TTL of seven since the first commit and not one node has ever decremented it — every phone was a
/// dead end, so "seven hops" was a wish written on an envelope nobody was going to forward.
/// </para>
///
/// <h3>What it is for</h3>
/// <para>
/// Two people who have added each other are often not in range of each other. A third phone that both
/// of them have added is, and it can pass the note. It never reads the note: what it carries is sealed
/// under a session between the two ends, and this node holds no key to it. It is a router, not a
/// participant.
/// </para>
///
/// <h3>Who it carries for</h3>
/// <para>
/// Only people this phone has added, in both directions — the sender must be somebody you know, and so
/// must the recipient. That is not politeness, it is the difference between a mesh and an open relay:
/// a node that forwards for anyone is a node anyone can use to flood a network, and a node that
/// forwards TO anyone is a way to reach people who never agreed to be reachable.
/// </para>
///
/// <para>
/// Recognition is the same one the radios use — a rotating address resolves to a person only for
/// somebody holding that person's routing key, which is exchanged inside an established session. So
/// "somebody I have added" is answered by cryptography rather than by a list anybody can join.
/// </para>
/// </summary>
public sealed class MeshRelay
{
    /// <summary>What to do with a packet that has just arrived.</summary>
    public enum Verdict
    {
        /// <summary>Addressed to this phone. Deliver it upstairs; do not forward.</summary>
        ForMe,

        /// <summary>Its hops are spent. It goes no further.</summary>
        Expired,

        /// <summary>Already carried once. Dropping it is what stops a loop becoming a storm.</summary>
        AlreadyCarried,

        /// <summary>From or to somebody this phone has not added. Not ours to carry.</summary>
        NotOurs,

        /// <summary>Pass it on, one hop shorter.</summary>
        Carry,
    }

    /// <summary>What was decided, and for whom.</summary>
    /// <param name="To">
    ///   The AetherTag to forward to, set only on <see cref="Verdict.Carry"/>. A person, not an
    ///   address: the address rotates every epoch and the person does not, so a route held by address
    ///   would go stale every fifteen minutes.
    /// </param>
    public readonly record struct Decision(Verdict Verdict, string? To = null)
    {
        public bool ShouldCarry => Verdict == Verdict.Carry;
    }

    /// <summary>How many packet ids are remembered to catch a loop.</summary>
    /// <remarks>
    /// A few seconds of traffic at the busiest this ever gets. It only has to outlive the time a
    /// packet could plausibly come back around, which on a mesh of phones in one room is milliseconds.
    /// </remarks>
    public const int Remembered = 512;

    /// <summary>How long a packet id is remembered for.</summary>
    /// <remarks>
    /// Short deliberately. Remembering forever would refuse a legitimate resend of a message somebody
    /// sent again an hour later — and an hour later there is no loop left to protect against.
    /// </remarks>
    public static readonly TimeSpan Memory = TimeSpan.FromMinutes(2);

    private readonly ConcurrentDictionary<Guid, DateTimeOffset> _carried = new();

    /// <summary>How many packets this node has passed on for other people.</summary>
    public long Carried { get; private set; }

    /// <summary>
    /// Decide what happens to a packet that arrived over a radio.
    /// </summary>
    /// <param name="packet">What arrived.</param>
    /// <param name="addressedToMe">
    ///   Whether the destination is one of this phone's own rotating addresses. The caller answers it
    ///   because only the caller holds the routing key.
    /// </param>
    /// <param name="fromTag">Who the sender turned out to be, or null if nobody we know.</param>
    /// <param name="toTag">Who the recipient turns out to be, or null if nobody we know.</param>
    public Decision Look(MeshPacket packet, bool addressedToMe, string? fromTag, string? toTag,
        DateTimeOffset? now = null)
    {
        ArgumentNullException.ThrowIfNull(packet);

        // Ours first, and before the loop guard: a packet for this phone is delivered every time it
        // arrives, even if a copy of it came round some other way a moment ago. Deduplication of
        // delivered messages is the job of the layer that understands them.
        if (addressedToMe) return new Decision(Verdict.ForMe);

        // A packet with no destination is addressed to whoever can hear it — presence, hellos, the
        // announcements a mesh runs on. It is delivered upstairs and never carried: forwarding
        // something addressed to nobody in particular is how one hello becomes a broadcast storm.
        if (string.IsNullOrEmpty(packet.DestinationUhid)) return new Decision(Verdict.ForMe);

        if (!packet.CanForward) return new Decision(Verdict.Expired);

        // Both ends have to be people this phone has added. One-sided would be enough to carry the
        // bytes and is exactly how a node becomes an open relay.
        if (fromTag is null || toTag is null) return new Decision(Verdict.NotOurs);

        // Never carry something back to the person who sent it.
        if (string.Equals(fromTag, toTag, StringComparison.Ordinal)) return new Decision(Verdict.NotOurs);

        var at = now ?? DateTimeOffset.UtcNow;
        if (!Remember(packet.Id, at)) return new Decision(Verdict.AlreadyCarried);

        Carried++;
        return new Decision(Verdict.Carry, toTag);
    }

    /// <summary>
    /// The packet as it should leave, one hop shorter.
    /// </summary>
    /// <remarks>
    /// A copy rather than an edit in place. The packet that arrived belongs to whatever else is
    /// looking at it — the layer above still wants to see what actually came in, not a version this
    /// one quietly shortened.
    /// </remarks>
    public static MeshPacket OneHopShorter(MeshPacket packet)
    {
        ArgumentNullException.ThrowIfNull(packet);

        return new MeshPacket
        {
            // The id travels unchanged. It is what every other node on the path uses to recognise
            // this as a packet it has already carried — a fresh id per hop would turn one loop into
            // an unbounded flood.
            Id = packet.Id,
            ProtocolVersion = packet.ProtocolVersion,
            Type = packet.Type,
            Priority = packet.Priority,
            TimestampMs = packet.TimestampMs,
            SourceUhid = packet.SourceUhid,
            DestinationUhid = packet.DestinationUhid,
            PacketNonce = packet.PacketNonce,
            Signature = packet.Signature,
            // Untouched. It is sealed under a session between the two ends and this node has no key
            // to it — which is the whole reason carrying for somebody costs them no privacy.
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

            // Still full of recent ids — this is a genuinely busy node rather than a stale table.
            // Forgetting the lot costs at most one duplicate carried per peer, and is a great deal
            // better than growing without bound on a phone.
            if (_carried.Count > Remembered) _carried.Clear();
        }

        return true;
    }

    /// <summary>Forget everything. The radios are going down.</summary>
    public void Clear() => _carried.Clear();
}

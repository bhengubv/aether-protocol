// SPDX-License-Identifier: MIT

using AetherNet.Identity;
using AetherNet.Protocol;
using AetherNet.Routing;
using Xunit;

namespace AetherNet.Core.Tests.Routing;

/// <summary>
/// E2 — the ERID header swap. Two nodes that have exchanged routing keys: the sender rewrites a packet's
/// source/destination to rotating ERIDs, and the receiver resolves them back to the stable UHIDs, so the
/// wire never carries the trackable long-term identity while both ends still see it.
/// </summary>
public class EridHeaderCodecTests
{
    private const long Now = 1_700_000_000;
    private const string AUhid = "alice-uhid";
    private const string BUhid = "bob-uhid";

    private static (EridHeaderCodec A, EridHeaderCodec B) Pair(long now = Now)
    {
        var rkA = EphemeralRoutingId.DeriveRoutingKey([1, 2, 3, 4]);
        var rkB = EphemeralRoutingId.DeriveRoutingKey([5, 6, 7, 8]);

        var dirA = new EridDirectory(rkA);
        var dirB = new EridDirectory(rkB);
        dirA.RememberPeer(BUhid, rkB); // Alice knows Bob's routing key
        dirB.RememberPeer(AUhid, rkA); // Bob knows Alice's

        var a = new EridHeaderCodec(dirA, AUhid, nowUnixSeconds: () => now);
        var b = new EridHeaderCodec(dirB, BUhid, nowUnixSeconds: () => now);
        return (a, b);
    }

    private static MeshPacket Packet() => new()
    {
        Type = PacketType.Data,
        SourceUhid = AUhid,
        DestinationUhid = BUhid,
    };

    [Fact]
    public void RoundTrip_SwapsToEridsOnTheWire_AndResolvesBackToUhids()
    {
        var (alice, bob) = Pair();
        var packet = Packet();

        // Alice puts it on the wire.
        Assert.True(alice.ToWire(packet, peerSpeaksErid: true));

        // The wire carries neither stable UHID any more.
        Assert.NotEqual(AUhid, packet.SourceUhid);
        Assert.NotEqual(BUhid, packet.DestinationUhid);
        Assert.Equal(EphemeralRoutingId.DefaultLength, packet.SourceUhid.Length);
        Assert.Equal(EphemeralRoutingId.DefaultLength, packet.DestinationUhid.Length);

        // Bob takes it off the wire and sees the stable identities again.
        bob.FromWire(packet);
        Assert.Equal(AUhid, packet.SourceUhid);
        Assert.Equal(BUhid, packet.DestinationUhid);
    }

    [Fact]
    public void ToWire_LeavesThePacketUntouched_WhenThePeerHasNotOptedIn()
    {
        var (alice, _) = Pair();
        var packet = Packet();

        Assert.False(alice.ToWire(packet, peerSpeaksErid: false));

        Assert.Equal(AUhid, packet.SourceUhid);       // unchanged — no peer can be handed an ERID it can't resolve
        Assert.Equal(BUhid, packet.DestinationUhid);
    }

    [Fact]
    public void ToWire_LeavesThePacketUntouched_WhenThePeersRoutingKeyIsUnknown()
    {
        // Bootstrap safety: before the routing-key exchange, the ERID cannot be derived, so no swap.
        var rkA = EphemeralRoutingId.DeriveRoutingKey([1, 2, 3, 4]);
        var alice = new EridHeaderCodec(new EridDirectory(rkA), AUhid, nowUnixSeconds: () => Now);
        var packet = Packet(); // destined for BUhid, whose key alice does NOT know

        Assert.False(alice.ToWire(packet, peerSpeaksErid: true));
        Assert.Equal(BUhid, packet.DestinationUhid);
    }

    [Fact]
    public void FromWire_LeavesPlainUhidsUnchanged()
    {
        // A pre-swap (or capability-less) packet arrives with stable UHIDs — pass through untouched.
        var (_, bob) = Pair();
        var packet = new MeshPacket { Type = PacketType.Data, SourceUhid = AUhid, DestinationUhid = BUhid };

        bob.FromWire(packet);

        Assert.Equal(AUhid, packet.SourceUhid);
        Assert.Equal(BUhid, packet.DestinationUhid);
    }

    [Fact]
    public void RoundTrip_ToleratesOneEpochOfClockSkew()
    {
        // Alice sends near the end of an epoch; Bob receives just after it turns. The dest ERID Alice
        // wrote (her clock) must still resolve to Bob's UHID on Bob's clock one epoch later.
        var rkA = EphemeralRoutingId.DeriveRoutingKey([1, 2, 3, 4]);
        var rkB = EphemeralRoutingId.DeriveRoutingKey([5, 6, 7, 8]);
        var dirA = new EridDirectory(rkA);
        var dirB = new EridDirectory(rkB);
        dirA.RememberPeer(BUhid, rkB);
        dirB.RememberPeer(AUhid, rkA);

        var aliceTime = Now;
        var bobTime = Now + EphemeralRoutingId.DefaultEpochSeconds; // one epoch later
        var alice = new EridHeaderCodec(dirA, AUhid, nowUnixSeconds: () => aliceTime);
        var bob = new EridHeaderCodec(dirB, BUhid, nowUnixSeconds: () => bobTime);

        var packet = Packet();
        Assert.True(alice.ToWire(packet, peerSpeaksErid: true));
        bob.FromWire(packet);

        Assert.Equal(AUhid, packet.SourceUhid);
        Assert.Equal(BUhid, packet.DestinationUhid);
    }
}

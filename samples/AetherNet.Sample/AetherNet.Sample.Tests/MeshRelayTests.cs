// SPDX-License-Identifier: MIT

using AetherNet.Protocol;
using AetherNet.Sample.Shared.Services;
using System.Text;
using Xunit;

namespace AetherNet.Sample.Tests;

/// <summary>
/// Carrying somebody else's traffic — the thing that makes this a mesh rather than a set of pairs.
///
/// <para>
/// Packets have carried a TTL of seven since the first commit and no node ever decremented it. Every
/// phone was a dead end, so seven hops was a wish written on an envelope nobody was going to forward.
/// These are the rules that change that, and each of them is wrong in a way that would not show up
/// until the network was large enough for it to matter.
/// </para>
/// </summary>
public class MeshRelayTests
{
    // Six friends in three pairs, and the one phone all of them added.
    private const string D1 = "AAAA1-AAAA1", D2 = "BBBB2-BBBB2";
    private const string D3 = "CCCC3-CCCC3", D4 = "DDDD4-DDDD4";
    private const string D5 = "EEEE5-EEEE5", D6 = "FFFF6-FFFF6";

    private static MeshPacket Packet(string from, string to, int ttl = 7) => new()
    {
        Type = PacketType.Data,
        SourceUhid = from,
        DestinationUhid = to,
        Payload = Encoding.UTF8.GetBytes("sealed for the far end"),
        Ttl = ttl,
    };

    // ── What device 7 does ───────────────────────────────────────────────────

    [Fact]
    public void The_phone_everyone_added_carries_between_two_who_cannot_reach_each_other()
    {
        // D1 and D2 added each other and are out of range. D7 added both. This is the whole point.
        var d7 = new MeshRelay();

        var decision = d7.Look(Packet("addr-of-1", "addr-of-2"), addressedToMe: false, fromTag: D1, toTag: D2);

        Assert.Equal(MeshRelay.Verdict.Carry, decision.Verdict);
        Assert.Equal(D2, decision.To);
    }

    [Fact]
    public void It_carries_for_every_pair_it_knows_not_just_the_first()
    {
        var d7 = new MeshRelay();

        foreach (var (from, to) in new[] { (D1, D2), (D3, D4), (D5, D6), (D2, D1), (D4, D3), (D6, D5) })
        {
            var d = d7.Look(Packet($"addr-{from}", $"addr-{to}"), false, from, to);
            Assert.Equal(MeshRelay.Verdict.Carry, d.Verdict);
            Assert.Equal(to, d.To);
        }

        Assert.Equal(6, d7.Carried);
    }

    [Fact]
    public void It_does_not_read_what_it_carries()
    {
        // The payload is sealed under a session between the two ends. A relay that altered it, or
        // needed to understand it, would be a participant rather than a router.
        var original = Packet("addr-of-1", "addr-of-2");
        var onward = MeshRelay.OneHopShorter(original);

        Assert.Same(original.Payload, onward.Payload);
        Assert.Equal(original.SourceUhid, onward.SourceUhid);
        Assert.Equal(original.DestinationUhid, onward.DestinationUhid);
        Assert.Equal(original.Type, onward.Type);
    }

    [Fact]
    public void Every_hop_costs_one()
    {
        var packet = Packet("a", "b", ttl: 7);
        Assert.Equal(6, MeshRelay.OneHopShorter(packet).Ttl);

        // And the original is untouched — the layer above still wants to see what actually arrived.
        Assert.Equal(7, packet.Ttl);
    }

    [Fact]
    public void A_packet_keeps_its_identity_across_hops()
    {
        // Every node on the path recognises a packet it has already carried by this id. A fresh id
        // per hop would turn a single loop into an unbounded flood.
        var packet = Packet("a", "b");
        Assert.Equal(packet.Id, MeshRelay.OneHopShorter(packet).Id);
    }

    [Fact]
    public void Seven_hops_run_out()
    {
        var packet = Packet("a", "b", ttl: 7);
        for (var i = 0; i < 7; i++) packet = MeshRelay.OneHopShorter(packet);

        Assert.Equal(0, packet.Ttl);
        Assert.Equal(MeshRelay.Verdict.Expired, new MeshRelay().Look(packet, false, D1, D2).Verdict);
    }

    // ── What it refuses ──────────────────────────────────────────────────────

    [Fact]
    public void It_will_not_carry_for_a_stranger()
    {
        // A node that forwards for anyone is a node anyone can use to flood a network.
        var d7 = new MeshRelay();
        Assert.Equal(MeshRelay.Verdict.NotOurs,
            d7.Look(Packet("addr-of-x", "addr-of-2"), false, fromTag: null, toTag: D2).Verdict);
        Assert.Equal(0, d7.Carried);
    }

    [Fact]
    public void It_will_not_carry_to_a_stranger()
    {
        // And a node that forwards TO anyone is a way to reach people who never agreed to be
        // reachable. Both ends have to be somebody this phone added.
        var d7 = new MeshRelay();
        Assert.Equal(MeshRelay.Verdict.NotOurs,
            d7.Look(Packet("addr-of-1", "addr-of-x"), false, fromTag: D1, toTag: null).Verdict);
        Assert.Equal(0, d7.Carried);
    }

    [Fact]
    public void It_will_not_hand_somebody_their_own_packet_back()
    {
        var d7 = new MeshRelay();
        Assert.Equal(MeshRelay.Verdict.NotOurs,
            d7.Look(Packet("addr-of-1", "addr-of-1b"), false, fromTag: D1, toTag: D1).Verdict);
    }

    [Fact]
    public void A_packet_for_this_phone_is_delivered_and_never_carried()
    {
        var d7 = new MeshRelay();
        Assert.Equal(MeshRelay.Verdict.ForMe,
            d7.Look(Packet("addr-of-1", "my-own-address"), addressedToMe: true, D1, D2).Verdict);
        Assert.Equal(0, d7.Carried);
    }

    [Fact]
    public void An_announcement_addressed_to_nobody_is_delivered_and_never_carried()
    {
        // Presence, hellos, the things a mesh runs on. Forwarding something addressed to nobody in
        // particular is how one hello becomes a broadcast storm — and dropping it instead would have
        // quietly broken every announcement this app makes.
        var hello = new MeshPacket { Type = PacketType.Heartbeat, SourceUhid = "addr-of-1", DestinationUhid = "", Ttl = 7 };

        var d7 = new MeshRelay();
        Assert.Equal(MeshRelay.Verdict.ForMe, d7.Look(hello, false, D1, null).Verdict);
        Assert.Equal(0, d7.Carried);
    }

    // ── Loops ────────────────────────────────────────────────────────────────

    [Fact]
    public void The_same_packet_is_carried_once_however_many_ways_it_arrives()
    {
        // On a mesh a packet comes back. Carrying every copy is how three phones in a room turn one
        // message into a storm that never stops.
        var d7 = new MeshRelay();
        var packet = Packet("addr-of-1", "addr-of-2");

        Assert.Equal(MeshRelay.Verdict.Carry, d7.Look(packet, false, D1, D2).Verdict);
        Assert.Equal(MeshRelay.Verdict.AlreadyCarried, d7.Look(packet, false, D1, D2).Verdict);
        Assert.Equal(MeshRelay.Verdict.AlreadyCarried, d7.Look(packet, false, D1, D2).Verdict);
        Assert.Equal(1, d7.Carried);
    }

    [Fact]
    public void A_packet_that_went_round_a_ring_stops()
    {
        // D7 carries it, it travels D2 → D4 → back to D7. The id is what makes the second arrival
        // recognisable, which is why OneHopShorter must never mint a new one.
        var d7 = new MeshRelay();
        var packet = Packet("addr-of-1", "addr-of-2");

        Assert.True(d7.Look(packet, false, D1, D2).ShouldCarry);

        var cameBack = MeshRelay.OneHopShorter(MeshRelay.OneHopShorter(packet));
        Assert.Equal(MeshRelay.Verdict.AlreadyCarried, d7.Look(cameBack, false, D1, D2).Verdict);
    }

    [Fact]
    public void A_message_sent_again_much_later_is_carried_again()
    {
        // Remembering forever would refuse a legitimate resend an hour on — and an hour on there is
        // no loop left to protect against.
        var d7 = new MeshRelay();
        var packet = Packet("addr-of-1", "addr-of-2");
        var now = DateTimeOffset.UtcNow;

        Assert.True(d7.Look(packet, false, D1, D2, now).ShouldCarry);
        Assert.False(d7.Look(packet, false, D1, D2, now + TimeSpan.FromSeconds(30)).ShouldCarry);
        Assert.True(d7.Look(packet, false, D1, D2, now + MeshRelay.Memory + TimeSpan.FromSeconds(1)).ShouldCarry);
    }

    [Fact]
    public void Remembering_loops_does_not_grow_without_end()
    {
        // This runs on a phone, for as long as the app does.
        var d7 = new MeshRelay();
        var now = DateTimeOffset.UtcNow;

        for (var i = 0; i < MeshRelay.Remembered * 4; i++)
        {
            var p = Packet("addr-of-1", "addr-of-2");
            Assert.True(d7.Look(p, false, D1, D2, now).ShouldCarry);
        }

        Assert.Equal(MeshRelay.Remembered * 4, d7.Carried);
    }

    // ── The whole shape the network is built on ──────────────────────────────

    [Fact]
    public void Three_pairs_and_one_relay_all_reach_each_other_and_nobody_else()
    {
        // D1↔D2, D3↔D4, D5↔D6 each added each other. All six added D7. D7 carries for all of them,
        // and for nobody it was not introduced to.
        var d7 = new MeshRelay();
        var known = new[] { D1, D2, D3, D4, D5, D6 };
        var carried = 0;

        foreach (var from in known)
            foreach (var to in known)
            {
                if (from == to) continue;
                var d = d7.Look(Packet($"a-{from}-{carried}", $"a-{to}"), false, from, to);
                if (d.ShouldCarry) carried++;
            }

        Assert.Equal(30, carried);            // every ordered pair of the six

        // And a seventh party nobody added gets nothing, in either direction.
        Assert.False(d7.Look(Packet("a-x", "a-1"), false, null, D1).ShouldCarry);
        Assert.False(d7.Look(Packet("a-1", "a-x"), false, D1, null).ShouldCarry);
    }
}

// SPDX-License-Identifier: MIT

using AetherNet.Protocol;
using AetherNet.Routing;
using Xunit;

namespace AetherNet.Core.Tests;

/// <summary>
/// Tests for <see cref="MeshRelay"/> — the carry-for-a-third-node decision engine promoted from the
/// Android sample into the library. The invariants that separate a mesh from an open relay: carry only
/// when BOTH ends are recognised and distinct, never a packet for me, never a spent or looping one, and
/// forward exactly one hop shorter with the sealed payload untouched.
/// </summary>
public class MeshRelayTests
{
    private const string Me = "ME-TAG";
    private const string Alice = "ALICE-TAG";
    private const string Bob = "BOB-TAG";

    private static MeshPacket Packet(string source = Alice, string dest = Bob, int ttl = 7) => new()
    {
        Type = PacketType.Data,
        SourceUhid = source,
        DestinationUhid = dest,
        Ttl = ttl,
        Payload = [1, 2, 3],
    };

    // ── ForMe / deliver, never carry ─────────────────────────────────────────

    [Fact]
    public void Addressed_to_me_is_ForMe_and_not_carried()
    {
        var relay = new MeshRelay();
        var d = relay.Look(Packet(dest: Me), addressedToMe: true, fromTag: Alice, toTag: Me);
        Assert.Equal(MeshRelay.Verdict.ForMe, d.Verdict);
        Assert.False(d.ShouldCarry);
        Assert.Equal(0, relay.Carried);
    }

    [Fact]
    public void No_destination_is_ForMe_never_carried()
    {
        // A hello / presence beacon addressed to whoever can hear it — delivered, never forwarded, or one
        // broadcast becomes a storm.
        var relay = new MeshRelay();
        var d = relay.Look(Packet(dest: ""), addressedToMe: false, fromTag: Alice, toTag: null);
        Assert.Equal(MeshRelay.Verdict.ForMe, d.Verdict);
    }

    // ── Refusals ─────────────────────────────────────────────────────────────

    [Fact]
    public void Spent_ttl_is_Expired()
    {
        var relay = new MeshRelay();
        var d = relay.Look(Packet(ttl: 0), addressedToMe: false, fromTag: Alice, toTag: Bob);
        Assert.Equal(MeshRelay.Verdict.Expired, d.Verdict);
    }

    [Fact]
    public void Unrecognised_sender_is_NotOurs()
    {
        var relay = new MeshRelay();
        var d = relay.Look(Packet(), addressedToMe: false, fromTag: null, toTag: Bob);
        Assert.Equal(MeshRelay.Verdict.NotOurs, d.Verdict);
        Assert.Equal(0, relay.Carried);
    }

    [Fact]
    public void Unrecognised_recipient_is_NotOurs()
    {
        var relay = new MeshRelay();
        var d = relay.Look(Packet(), addressedToMe: false, fromTag: Alice, toTag: null);
        Assert.Equal(MeshRelay.Verdict.NotOurs, d.Verdict);
    }

    [Fact]
    public void Carrying_back_to_the_sender_is_NotOurs()
    {
        var relay = new MeshRelay();
        var d = relay.Look(Packet(), addressedToMe: false, fromTag: Alice, toTag: Alice);
        Assert.Equal(MeshRelay.Verdict.NotOurs, d.Verdict);
    }

    // ── Carry ────────────────────────────────────────────────────────────────

    [Fact]
    public void Both_ends_recognised_and_distinct_is_Carry_to_the_recipient()
    {
        var relay = new MeshRelay();
        var d = relay.Look(Packet(), addressedToMe: false, fromTag: Alice, toTag: Bob);
        Assert.Equal(MeshRelay.Verdict.Carry, d.Verdict);
        Assert.True(d.ShouldCarry);
        Assert.Equal(Bob, d.To); // a person, not the rotating wire address
        Assert.Equal(1, relay.Carried);
    }

    [Fact]
    public void The_same_packet_is_carried_once_then_AlreadyCarried()
    {
        var relay = new MeshRelay();
        var packet = Packet();
        var at = DateTimeOffset.UtcNow;

        var first = relay.Look(packet, false, Alice, Bob, at);
        var second = relay.Look(packet, false, Alice, Bob, at.AddSeconds(1)); // same id, within Memory

        Assert.Equal(MeshRelay.Verdict.Carry, first.Verdict);
        Assert.Equal(MeshRelay.Verdict.AlreadyCarried, second.Verdict);
        Assert.Equal(1, relay.Carried); // counted once, not twice
    }

    [Fact]
    public void A_resend_after_the_memory_window_is_carried_again()
    {
        var relay = new MeshRelay();
        var packet = Packet();
        var at = DateTimeOffset.UtcNow;

        relay.Look(packet, false, Alice, Bob, at);
        var later = relay.Look(packet, false, Alice, Bob, at + MeshRelay.Memory + TimeSpan.FromSeconds(1));

        Assert.Equal(MeshRelay.Verdict.Carry, later.Verdict);
        Assert.Equal(2, relay.Carried);
    }

    // ── OneHopShorter ────────────────────────────────────────────────────────

    [Fact]
    public void OneHopShorter_decrements_ttl_and_copies_everything_else()
    {
        var packet = Packet(ttl: 5);
        var onward = MeshRelay.OneHopShorter(packet);

        Assert.Equal(4, onward.Ttl);
        Assert.Equal(packet.Id, onward.Id);                       // id unchanged — loop recognition depends on it
        Assert.Equal(packet.SourceUhid, onward.SourceUhid);
        Assert.Equal(packet.DestinationUhid, onward.DestinationUhid);
        Assert.Equal(packet.Payload, onward.Payload);             // sealed payload untouched
        Assert.Equal(5, packet.Ttl);                              // original not mutated — it is a copy
        Assert.NotSame(packet, onward);
    }
}

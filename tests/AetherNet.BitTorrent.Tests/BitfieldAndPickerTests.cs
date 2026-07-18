// SPDX-License-Identifier: MIT

using AetherNet.BitTorrent.PeerWire;
using Xunit;

namespace AetherNet.BitTorrent.Tests;

public class BitfieldTests
{
    [Fact]
    public void Piece_zero_is_the_high_bit_of_byte_zero()
    {
        var bf = new Bitfield(8);
        bf[0] = true;
        Assert.Equal(new byte[] { 0x80 }, bf.ToBytes());
        bf[7] = true;
        Assert.Equal(new byte[] { 0x81 }, bf.ToBytes());
    }

    [Fact]
    public void Get_set_popcount_and_hasall()
    {
        var bf = new Bitfield(10); // spans 2 bytes
        Assert.Equal(2, bf.ByteLength);
        Assert.Equal(0, bf.PopCount());
        bf[1] = true;
        bf[9] = true;
        Assert.True(bf[1]);
        Assert.False(bf[2]);
        Assert.Equal(2, bf.PopCount());
        Assert.False(bf.HasAll());
        for (int i = 0; i < 10; i++) bf[i] = true;
        Assert.True(bf.HasAll());
    }

    [Fact]
    public void Constructed_from_bytes_roundtrips()
    {
        var bytes = new byte[] { 0b1010_0000, 0b0100_0000 };
        var bf = new Bitfield(bytes, 10);
        Assert.True(bf[0]);
        Assert.False(bf[1]);
        Assert.True(bf[2]);
        Assert.True(bf[9]);
        Assert.Equal(bytes, bf.ToBytes());
    }

    [Fact]
    public void Wrong_byte_length_rejected() =>
        Assert.Throws<PeerWireException>(() => new Bitfield(new byte[3], 10));

    [Fact]
    public void Out_of_range_index_rejected()
    {
        var bf = new Bitfield(5);
        Assert.Throws<ArgumentOutOfRangeException>(() => bf[5] = true);
    }
}

public class RarestFirstPickerTests
{
    private static Bitfield Bits(int count, params int[] set)
    {
        var bf = new Bitfield(count);
        foreach (var i in set) bf[i] = true;
        return bf;
    }

    [Fact]
    public void Picks_the_rarest_piece_the_peer_offers()
    {
        var picker = new RarestFirstPicker(4);
        // Availability: piece 0 held by 3 peers, piece 1 by 1, piece 2 by 2, piece 3 by 0.
        picker.AddPeer(Bits(4, 0, 1, 2));
        picker.AddPeer(Bits(4, 0, 2));
        picker.AddPeer(Bits(4, 0));

        // A peer offering 0,1,2 — rarest among those is piece 1 (availability 1).
        Assert.Equal(1, picker.PickFor(Bits(4, 0, 1, 2)));
    }

    [Fact]
    public void Does_not_pick_pieces_we_have_or_that_are_in_flight()
    {
        var picker = new RarestFirstPicker(3);
        picker.AddPeer(Bits(3, 0, 1, 2));
        var peer = Bits(3, 0, 1, 2);

        picker.SetHave(0);                 // we already have piece 0
        int? first = picker.PickFor(peer); // picks 1 or 2 (both availability 1) and marks it in-flight
        Assert.NotNull(first);
        int? second = picker.PickFor(peer);
        Assert.NotNull(second);
        Assert.NotEqual(first, second);    // never the same in-flight piece twice
        Assert.Null(picker.PickFor(peer)); // nothing left (0 have, other two in flight)
    }

    [Fact]
    public void Released_piece_becomes_available_again()
    {
        var picker = new RarestFirstPicker(1);
        picker.AddPeer(Bits(1, 0));
        var peer = Bits(1, 0);

        int? picked = picker.PickFor(peer);
        Assert.Equal(0, picked);
        Assert.Null(picker.PickFor(peer)); // in flight
        picker.Release(0);
        Assert.Equal(0, picker.PickFor(peer)); // pickable again
    }

    [Fact]
    public void Returns_null_when_peer_offers_nothing_useful()
    {
        var picker = new RarestFirstPicker(2);
        picker.AddPeer(Bits(2, 0));
        Assert.Null(picker.PickFor(Bits(2))); // empty bitfield
    }

    [Fact]
    public void Completion_tracks_have()
    {
        var picker = new RarestFirstPicker(2);
        Assert.False(picker.IsComplete);
        picker.SetHave(0);
        picker.SetHave(1);
        Assert.True(picker.IsComplete);
    }
}

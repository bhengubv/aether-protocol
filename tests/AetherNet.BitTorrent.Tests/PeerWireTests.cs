// SPDX-License-Identifier: MIT

using AetherNet.BitTorrent.PeerWire;
using Xunit;

namespace AetherNet.BitTorrent.Tests;

public class HandshakeTests
{
    private static byte[] Filled(int len, byte b)
    {
        var a = new byte[len];
        Array.Fill(a, b);
        return a;
    }

    [Fact]
    public void Roundtrips_and_is_68_bytes()
    {
        var info = Filled(20, 0xAB);
        var peer = Filled(20, 0xCD);
        var hs = new Handshake(info, peer, Handshake.DefaultReserved());

        var wire = hs.ToBytes();
        Assert.Equal(68, wire.Length);
        Assert.Equal(19, wire[0]);
        Assert.Equal("BitTorrent protocol"u8.ToArray(), wire[1..20]);

        var back = Handshake.Parse(wire);
        Assert.Equal(info, back.InfoHash);
        Assert.Equal(peer, back.PeerId);
        Assert.True(back.SupportsExtensionProtocol);
        Assert.True(back.SupportsDht);
    }

    [Fact]
    public void Rejects_wrong_protocol_id()
    {
        var wire = new Handshake(Filled(20, 1), Filled(20, 2)).ToBytes();
        wire[1] = (byte)'X'; // corrupt the protocol string
        Assert.Throws<PeerWireException>(() => Handshake.Parse(wire));
    }

    [Fact]
    public void Rejects_short_buffer() =>
        Assert.Throws<PeerWireException>(() => Handshake.Parse(new byte[67]));
}

public class PeerMessageTests
{
    [Fact]
    public void KeepAlive_is_four_zero_bytes()
    {
        Assert.Equal(new byte[] { 0, 0, 0, 0 }, PeerMessage.KeepAlive.ToBytes());
        Assert.True(PeerMessage.ParseFrame(new byte[] { 0, 0, 0, 0 }).IsKeepAlive);
    }

    [Fact]
    public void Have_has_exact_wire_bytes()
    {
        // length 5 = id(1) + index(4); id 4; index 1
        Assert.Equal(new byte[] { 0, 0, 0, 5, 4, 0, 0, 0, 1 }, PeerMessage.Have(1).ToBytes());
    }

    [Fact]
    public void Request_has_exact_wire_bytes()
    {
        // length 13 = id(1) + 3*4; id 6; index 0, begin 0, length 16384 (0x4000)
        Assert.Equal(
            new byte[] { 0, 0, 0, 13, 6, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0x40, 0 },
            PeerMessage.Request(0, 0, 16384).ToBytes());
    }

    [Theory]
    [InlineData(0)] // choke
    [InlineData(1)] // unchoke
    [InlineData(2)] // interested
    [InlineData(3)] // not-interested
    public void Empty_messages_roundtrip(int id)
    {
        var msg = id switch
        {
            0 => PeerMessage.Choke(),
            1 => PeerMessage.Unchoke(),
            2 => PeerMessage.Interested(),
            _ => PeerMessage.NotInterested(),
        };
        var back = PeerMessage.ParseFrame(msg.ToBytes());
        Assert.Equal((PeerMessageType)id, back.Type);
        Assert.Empty(back.Payload);
    }

    [Fact]
    public void Piece_roundtrips_with_block()
    {
        var block = new byte[] { 10, 20, 30, 40 };
        var back = PeerMessage.ParseFrame(PeerMessage.Piece(3, 8, block).ToBytes());
        var (index, begin, gotBlock) = back.GetPiece();
        Assert.Equal(3, index);
        Assert.Equal(8, begin);
        Assert.Equal(block, gotBlock);
    }

    [Fact]
    public void Request_and_cancel_roundtrip()
    {
        var (i, b, l) = PeerMessage.ParseFrame(PeerMessage.Request(7, 16384, 16384).ToBytes()).GetBlockRef();
        Assert.Equal((7, 16384, 16384), (i, b, l));
        var (ci, cb, cl) = PeerMessage.ParseFrame(PeerMessage.Cancel(1, 2, 3).ToBytes()).GetBlockRef();
        Assert.Equal((1, 2, 3), (ci, cb, cl));
    }

    [Fact]
    public void Port_and_bitfield_and_have_roundtrip()
    {
        Assert.Equal(6881, PeerMessage.ParseFrame(PeerMessage.Port(6881).ToBytes()).GetPort());
        Assert.Equal(9, PeerMessage.ParseFrame(PeerMessage.Have(9).ToBytes()).GetHavePieceIndex());
        var bits = new byte[] { 0xF0, 0x0F };
        Assert.Equal(bits, PeerMessage.ParseFrame(PeerMessage.Bitfield(bits).ToBytes()).GetBitfield());
    }

    [Fact]
    public void Unknown_extension_id_is_carried_not_rejected()
    {
        // id 20 = extension protocol (BEP-10, added later) — must not crash the BEP-3 parser.
        var frame = PeerMessage.Unknown(20, new byte[] { 1, 2, 3 }).ToBytes();
        var back = PeerMessage.ParseFrame(frame);
        Assert.Equal((byte)20, back.Id);
        Assert.Null(back.Type);
        Assert.Equal(new byte[] { 1, 2, 3 }, back.Payload);
    }

    [Theory]
    [InlineData(new byte[] { 0, 0, 0, 2, 4, 0 })]           // 'have' with 1-byte payload (needs 4)
    [InlineData(new byte[] { 0, 0, 0, 5, 4, 0, 0 })]        // frame declares 5 bytes but only 3 follow
    [InlineData(new byte[] { 0, 0, 0, 4, 9, 0, 0, 0 })]     // 'port' with 3-byte payload (needs 2)
    public void Malformed_frames_rejected(byte[] frame) =>
        Assert.Throws<PeerWireException>(() => PeerMessage.ParseFrame(frame));
}

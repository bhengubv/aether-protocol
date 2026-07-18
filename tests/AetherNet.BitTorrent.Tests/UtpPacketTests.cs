// SPDX-License-Identifier: MIT

using AetherNet.BitTorrent.Utp;
using Xunit;

namespace AetherNet.BitTorrent.Tests;

public class UtpPacketTests
{
    [Fact]
    public void Data_packet_has_exact_wire_header()
    {
        var pkt = new UtpPacket
        {
            Type = UtpPacketType.Data,
            ConnectionId = 0x1234,
            TimestampMicros = 0x00ABCDEF,
            TimestampDiffMicros = 0,
            WindowSize = 0x00010000,
            SeqNr = 5,
            AckNr = 4,
            Payload = new byte[] { 0xDE, 0xAD },
        };
        var wire = pkt.ToBytes();

        Assert.Equal(22, wire.Length);          // 20 header + 2 payload
        Assert.Equal(0x01, wire[0]);            // type Data(0) << 4 | version 1
        Assert.Equal(0x00, wire[1]);            // no extension
        Assert.Equal(new byte[] { 0x12, 0x34 }, wire[2..4]);       // connection id
        Assert.Equal(new byte[] { 0x00, 0xAB, 0xCD, 0xEF }, wire[4..8]); // timestamp
        Assert.Equal(new byte[] { 0x00, 0x05 }, wire[16..18]);     // seq_nr
        Assert.Equal(new byte[] { 0x00, 0x04 }, wire[18..20]);     // ack_nr
        Assert.Equal(new byte[] { 0xDE, 0xAD }, wire[20..22]);     // payload
    }

    [Theory]
    [InlineData(UtpPacketType.Syn)]
    [InlineData(UtpPacketType.State)]
    [InlineData(UtpPacketType.Fin)]
    [InlineData(UtpPacketType.Reset)]
    public void All_types_roundtrip(UtpPacketType type)
    {
        var pkt = new UtpPacket { Type = type, ConnectionId = 42, SeqNr = 1, AckNr = 0, WindowSize = 1024 };
        var back = UtpPacket.Parse(pkt.ToBytes());
        Assert.Equal(type, back.Type);
        Assert.Equal(42, back.ConnectionId);
        Assert.Equal(1, back.SeqNr);
        Assert.Equal(1024u, back.WindowSize);
        Assert.Empty(back.Payload);
    }

    [Fact]
    public void Skips_extensions_to_find_payload()
    {
        // Header with extension=1 (selective ack), one extension block [next=0][len=4][4 bytes], then payload.
        var pkt = new UtpPacket { Type = UtpPacketType.Data, ConnectionId = 1, SeqNr = 1 };
        var header = pkt.ToBytes();          // 20 bytes, extension byte = 0
        var withExt = new byte[20 + 6 + 3];
        header.AsSpan(0, 20).CopyTo(withExt);
        withExt[1] = 1;                       // first extension type = selective ack
        withExt[20] = 0;                      // next extension = none
        withExt[21] = 4;                      // extension length
        // withExt[22..26] = ext data (zero)
        withExt[26] = 0xAA;                   // payload
        withExt[27] = 0xBB;
        withExt[28] = 0xCC;

        var parsed = UtpPacket.Parse(withExt);
        Assert.Equal(new byte[] { 0xAA, 0xBB, 0xCC }, parsed.Payload);
    }

    [Fact]
    public void Rejects_short_packet() => Assert.Throws<UtpException>(() => UtpPacket.Parse(new byte[10]));

    [Fact]
    public void Rejects_wrong_version()
    {
        var bytes = new UtpPacket { Type = UtpPacketType.Syn }.ToBytes();
        bytes[0] = (byte)((4 << 4) | 2); // version 2
        Assert.Throws<UtpException>(() => UtpPacket.Parse(bytes));
    }
}

// SPDX-License-Identifier: MIT

using System.Net;
using System.Security.Cryptography;
using AetherNet.BitTorrent.Bencoding;
using AetherNet.BitTorrent.Extensions;
using AetherNet.BitTorrent.PeerWire;
using Xunit;

namespace AetherNet.BitTorrent.Tests;

public class ExtensionProtocolTests
{
    [Fact]
    public void Extended_handshake_roundtrips_over_the_peer_wire()
    {
        var supported = new Dictionary<string, int> { ["ut_metadata"] = 1, ["ut_pex"] = 2 };
        var msg = ExtensionProtocol.BuildHandshake(supported, metadataSize: 4096, listenPort: 6881, client: "AetherNet 0.1");

        // It travels as an ordinary peer-wire message (id 20) and survives framing.
        var framed = PeerMessage.ParseFrame(msg.ToBytes());
        Assert.Equal((byte)20, framed.Id);

        var hs = ExtensionProtocol.ParseHandshake(framed);
        Assert.Equal(1, hs.MetadataMessageId);
        Assert.Equal(2, hs.PexMessageId);
        Assert.Equal(4096, hs.MetadataSize);
    }
}

public class UtMetadataTests
{
    [Fact]
    public void Request_and_reject_roundtrip()
    {
        var (t1, p1, _, _) = UtMetadata.Parse(UtMetadata.BuildRequest(3));
        Assert.Equal(MetadataMessageType.Request, t1);
        Assert.Equal(3, p1);

        var (t2, p2, _, _) = UtMetadata.Parse(UtMetadata.BuildReject(9));
        Assert.Equal(MetadataMessageType.Reject, t2);
        Assert.Equal(9, p2);
    }

    [Fact]
    public void Data_message_carries_trailing_piece_bytes()
    {
        var piece = new byte[] { 1, 2, 3, 4, 5 };
        var (type, index, total, data) = UtMetadata.Parse(UtMetadata.BuildData(2, 40000, piece));
        Assert.Equal(MetadataMessageType.Data, type);
        Assert.Equal(2, index);
        Assert.Equal(40000, total);
        Assert.Equal(piece, data);
    }

    [Fact]
    public void Assembles_multi_piece_metadata_and_verifies_infohash()
    {
        // A ~40 KB info dict → 3 metadata pieces.
        var info = new BencodeDictionary();
        info.Add("length", new BencodeInteger(1));
        info.Add("name", new BencodeString("big"));
        info.Add("piece length", new BencodeInteger(16384));
        info.Add("pieces", new BencodeString(new byte[40000]));
        var infoBytes = info.Encode();
        var infoHash = SHA1.HashData(infoBytes);

        var assembler = new MetadataAssembler(infoBytes.Length);
        Assert.True(assembler.PieceCount >= 3);
        for (int i = 0; i < assembler.PieceCount; i++)
        {
            int begin = i * UtMetadata.PieceSize;
            int len = assembler.LengthOfPiece(i);
            assembler.AddPiece(i, infoBytes.AsSpan(begin, len));
        }

        Assert.True(assembler.IsComplete);
        var finished = assembler.TryFinish(infoHash);
        Assert.NotNull(finished);
        Assert.Equal(infoBytes, finished);
        Assert.Null(assembler.TryFinish(new byte[20])); // wrong info-hash → rejected
    }
}

public class PeerExchangeTests
{
    [Fact]
    public void Added_peers_roundtrip()
    {
        var peers = new List<IPEndPoint>
        {
            new(IPAddress.Parse("1.2.3.4"), 6881),
            new(IPAddress.Parse("9.8.7.6"), 51413),
        };
        var parsed = PeerExchange.ParseAddedPeers(PeerExchange.BuildAdded(peers));
        Assert.Equal(2, parsed.Count);
        Assert.Equal("1.2.3.4:6881", parsed[0].ToString());
        Assert.Equal("9.8.7.6:51413", parsed[1].ToString());
    }
}

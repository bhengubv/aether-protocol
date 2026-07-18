// SPDX-License-Identifier: MIT

using System.Net;
using System.Text.Json;
using AetherNet.BitTorrent.Bencoding;
using AetherNet.BitTorrent.Dht;
using AetherNet.BitTorrent.Metainfo;
using AetherNet.BitTorrent.PeerWire;
using AetherNet.BitTorrent.Utp;
using AetherNet.BitTorrent.V2;
using Xunit;

namespace AetherNet.BitTorrent.Tests;

/// <summary>
/// The independent (MonoTorrent-validated) C# reference asserts byte-identity against the
/// SAME fixtures/bittorrent/vectors.json the Go oracle generated. C# agreeing with Go
/// double-anchors the corpus — every other language SDK then asserts against it.
/// </summary>
public class FixtureCorpusTests
{
    private static JsonElement Corpus()
    {
        var dir = AppContext.BaseDirectory;
        for (int i = 0; i < 12 && dir is not null; i++)
        {
            var candidate = Path.Combine(dir, "fixtures", "bittorrent", "vectors.json");
            if (File.Exists(candidate))
                return JsonDocument.Parse(File.ReadAllText(candidate)).RootElement.Clone();
            dir = Directory.GetParent(dir)?.FullName;
        }
        throw new FileNotFoundException("fixtures/bittorrent/vectors.json not found");
    }

    private static byte[] Fill(int n, int mult, int add)
    {
        var b = new byte[n];
        for (int i = 0; i < n; i++) b[i] = (byte)(i * mult + add);
        return b;
    }

    private static string Hex(byte[] b) => Convert.ToHexString(b).ToLowerInvariant();

    [Fact]
    public void Bencode_roundtrips_every_vector()
    {
        foreach (var el in Corpus().GetProperty("bencode_roundtrip").EnumerateArray())
        {
            var raw = Convert.FromHexString(el.GetString()!);
            Assert.Equal(el.GetString(), Hex(Bencode.Encode(Bencode.Decode(raw))));
        }
    }

    [Fact]
    public void InfoHash_matches_every_vector()
    {
        foreach (var el in Corpus().GetProperty("info_hash").EnumerateArray())
        {
            var content = Fill(el.GetProperty("size").GetInt32(), el.GetProperty("mult").GetInt32(), el.GetProperty("add").GetInt32());
            var torrent = TorrentBuilder.CreateSingleFile(el.GetProperty("name_str").GetString()!, content, el.GetProperty("piece_length").GetInt32());
            var meta = TorrentMetainfo.Parse(torrent);
            Assert.Equal(el.GetProperty("info_hash_hex").GetString(), meta.InfoHashV1Hex);
        }
    }

    [Fact]
    public void PeerMessages_match_every_vector()
    {
        foreach (var el in Corpus().GetProperty("peer_messages").EnumerateArray())
        {
            uint a = el.GetProperty("a").GetUInt32();
            var msg = el.GetProperty("kind").GetString() switch
            {
                "keepalive" => PeerMessage.KeepAlive,
                "choke" => PeerMessage.Choke(),
                "unchoke" => PeerMessage.Unchoke(),
                "interested" => PeerMessage.Interested(),
                "have" => PeerMessage.Have((int)a),
                "request" => PeerMessage.Request((int)a, (int)el.GetProperty("b").GetUInt32(), (int)el.GetProperty("c").GetUInt32()),
                "port" => PeerMessage.Port((int)a),
                var k => throw new InvalidOperationException($"unknown kind {k}"),
            };
            Assert.Equal(el.GetProperty("wire_hex").GetString(), Hex(msg.ToBytes()));
        }
    }

    [Fact]
    public void Utp_packets_match_every_vector()
    {
        foreach (var el in Corpus().GetProperty("utp_packets").EnumerateArray())
        {
            var payloadHex = el.GetProperty("payload_hex").GetString()!;
            var pkt = new UtpPacket
            {
                Type = (UtpPacketType)el.GetProperty("type").GetInt32(),
                ConnectionId = (ushort)el.GetProperty("conn_id").GetUInt32(),
                TimestampMicros = el.GetProperty("timestamp").GetUInt32(),
                TimestampDiffMicros = el.GetProperty("timestamp_diff").GetUInt32(),
                WindowSize = el.GetProperty("window").GetUInt32(),
                SeqNr = (ushort)el.GetProperty("seq").GetUInt32(),
                AckNr = (ushort)el.GetProperty("ack").GetUInt32(),
                Payload = payloadHex.Length == 0 ? Array.Empty<byte>() : Convert.FromHexString(payloadHex),
            };
            Assert.Equal(el.GetProperty("wire_hex").GetString(), Hex(pkt.ToBytes()));
        }
    }

    [Fact]
    public void Merkle_roots_match_every_vector()
    {
        foreach (var el in Corpus().GetProperty("merkle").EnumerateArray())
        {
            var content = Fill(el.GetProperty("size").GetInt32(), el.GetProperty("mult").GetInt32(), el.GetProperty("add").GetInt32());
            Assert.Equal(el.GetProperty("root_hex").GetString(), Hex(MerkleTree.ComputeRoot(content)));
        }
    }

    [Fact]
    public void Compact_info_matches_every_vector()
    {
        foreach (var el in Corpus().GetProperty("compact").EnumerateArray())
        {
            var wire = el.GetProperty("wire_hex").GetString()!;
            switch (el.GetProperty("kind").GetString())
            {
                case "node":
                    var nodes = CompactInfo.DecodeNodes(Convert.FromHexString(wire));
                    Assert.Equal(wire, Hex(CompactInfo.EncodeNodes(nodes)));
                    break;
                case "peers":
                    var built = new List<byte>();
                    foreach (var p in el.GetProperty("peers").EnumerateArray())
                        built.AddRange(CompactInfo.EncodePeer(new IPEndPoint(IPAddress.Parse(p.GetProperty("ip").GetString()!), (int)p.GetProperty("port").GetUInt32())));
                    Assert.Equal(wire, Hex(built.ToArray()));
                    break;
            }
        }
    }

    [Fact]
    public void Krpc_messages_match_every_vector()
    {
        foreach (var el in Corpus().GetProperty("krpc").EnumerateArray())
        {
            var tx = Convert.FromHexString(el.GetProperty("tx_hex").GetString()!);
            KrpcMessage m;
            switch (el.GetProperty("kind").GetString())
            {
                case "get_peers":
                    var args = new BencodeDictionary();
                    args.Add("id", new BencodeString(Convert.FromHexString(el.GetProperty("id_hex").GetString()!)));
                    args.Add("info_hash", new BencodeString(Convert.FromHexString(el.GetProperty("info_hash_hex").GetString()!)));
                    m = new KrpcMessage { TransactionId = tx, Type = KrpcType.Query, Method = "get_peers", Body = args };
                    break;
                case "error":
                    m = new KrpcMessage
                    {
                        TransactionId = tx,
                        Type = KrpcType.Error,
                        Error = (el.GetProperty("error_code").GetInt32(), el.GetProperty("error_message").GetString()!),
                    };
                    break;
                default:
                    throw new InvalidOperationException("unknown krpc kind");
            }
            Assert.Equal(el.GetProperty("wire_hex").GetString(), Hex(m.Encode()));
        }
    }
}

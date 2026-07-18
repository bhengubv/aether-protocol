// SPDX-License-Identifier: MIT

using System.Buffers.Binary;
using System.Net;
using AetherNet.BitTorrent.Bencoding;
using AetherNet.BitTorrent.Trackers;

namespace AetherNet.BitTorrent.Dht;

/// <summary>
/// Compact encodings used by the DHT (BEP-5): a node is 26 bytes (20-byte id + 4-byte IPv4 + 2-byte
/// port); a peer in a <c>values</c> list is 6 bytes (4-byte IPv4 + 2-byte port). All big-endian.
/// </summary>
public static class CompactInfo
{
    public const int NodeInfoSize = 26;
    public const int PeerSize = 6;

    public static byte[] EncodeNodes(IReadOnlyList<DhtContact> nodes)
    {
        var buf = new byte[nodes.Count * NodeInfoSize];
        int o = 0;
        foreach (var n in nodes)
        {
            n.Id.Span.CopyTo(buf.AsSpan(o));
            o += NodeId.Length;
            n.EndPoint.Address.MapToIPv4().GetAddressBytes().CopyTo(buf, o);
            o += 4;
            BinaryPrimitives.WriteUInt16BigEndian(buf.AsSpan(o), (ushort)n.EndPoint.Port);
            o += 2;
        }
        return buf;
    }

    public static IReadOnlyList<DhtContact> DecodeNodes(ReadOnlySpan<byte> data)
    {
        var nodes = new List<DhtContact>();
        for (int i = 0; i + NodeInfoSize <= data.Length; i += NodeInfoSize)
        {
            var id = new NodeId(data.Slice(i, NodeId.Length).ToArray());
            var ip = new IPAddress(data.Slice(i + NodeId.Length, 4).ToArray());
            int port = BinaryPrimitives.ReadUInt16BigEndian(data.Slice(i + NodeId.Length + 4, 2));
            nodes.Add(new DhtContact(id, new IPEndPoint(ip, port)));
        }
        return nodes;
    }

    public static byte[] EncodePeer(IPEndPoint endpoint)
    {
        var b = new byte[PeerSize];
        endpoint.Address.MapToIPv4().GetAddressBytes().CopyTo(b, 0);
        BinaryPrimitives.WriteUInt16BigEndian(b.AsSpan(4), (ushort)endpoint.Port);
        return b;
    }

    public static IReadOnlyList<PeerAddress> DecodePeerValues(BencodeList values)
    {
        var peers = new List<PeerAddress>();
        foreach (var v in values.Items)
        {
            var b = v.AsBytes();
            if (b.Length == PeerSize)
                peers.Add(new PeerAddress(new IPAddress(b[..4]), BinaryPrimitives.ReadUInt16BigEndian(b.AsSpan(4, 2))));
        }
        return peers;
    }
}

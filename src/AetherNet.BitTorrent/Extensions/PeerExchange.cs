// SPDX-License-Identifier: MIT

using System.Buffers.Binary;
using System.Net;
using AetherNet.BitTorrent.Bencoding;
using AetherNet.BitTorrent.Trackers;

namespace AetherNet.BitTorrent.Extensions;

/// <summary>
/// Peer Exchange — ut_pex (BEP-11): peers gossip newly-seen and dropped peers to each other, so a
/// swarm keeps growing without further tracker/DHT queries. The <c>added</c> list is compact 6-byte
/// IPv4 peers.
/// </summary>
public static class PeerExchange
{
    public static byte[] BuildAdded(IReadOnlyList<IPEndPoint> peers)
    {
        var added = new byte[peers.Count * 6];
        var flags = new byte[peers.Count]; // 0 = no special flags
        int o = 0;
        foreach (var p in peers)
        {
            p.Address.MapToIPv4().GetAddressBytes().CopyTo(added, o);
            BinaryPrimitives.WriteUInt16BigEndian(added.AsSpan(o + 4), (ushort)p.Port);
            o += 6;
        }
        var d = new BencodeDictionary();
        d.Add("added", new BencodeString(added));
        d.Add("added.f", new BencodeString(flags));
        return d.Encode();
    }

    public static IReadOnlyList<PeerAddress> ParseAddedPeers(ReadOnlySpan<byte> body)
    {
        var dict = Bencode.Decode(body).AsDictionary();
        var peers = new List<PeerAddress>();
        if (dict["added"] is BencodeString added)
        {
            var b = added.Value;
            for (int i = 0; i + 6 <= b.Length; i += 6)
                peers.Add(new PeerAddress(new IPAddress(b[i..(i + 4)]), BinaryPrimitives.ReadUInt16BigEndian(b.AsSpan(i + 4, 2))));
        }
        return peers;
    }
}

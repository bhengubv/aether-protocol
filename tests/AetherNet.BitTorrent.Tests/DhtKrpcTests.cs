// SPDX-License-Identifier: MIT

using System.Net;
using AetherNet.BitTorrent.Bencoding;
using AetherNet.BitTorrent.Dht;
using Xunit;

namespace AetherNet.BitTorrent.Tests;

public class DhtKrpcTests
{
    [Fact]
    public void GetPeers_query_roundtrips()
    {
        var args = new BencodeDictionary();
        args.Add("id", new BencodeString(new byte[20]));
        args.Add("info_hash", new BencodeString(Enumerable.Repeat((byte)0xAB, 20).ToArray()));
        var query = new KrpcMessage
        {
            TransactionId = new byte[] { 0xAA, 0xBB },
            Type = KrpcType.Query,
            Method = "get_peers",
            Body = args,
        };

        var back = KrpcMessage.Decode(query.Encode());
        Assert.Equal(KrpcType.Query, back.Type);
        Assert.Equal("get_peers", back.Method);
        Assert.Equal(new byte[] { 0xAA, 0xBB }, back.TransactionId);
        Assert.Equal(20, back.Body["info_hash"]!.AsBytes().Length);
    }

    [Fact]
    public void Response_roundtrips()
    {
        var r = new BencodeDictionary();
        r.Add("id", new BencodeString(new byte[20]));
        r.Add("token", new BencodeString("tok"u8.ToArray()));
        var msg = new KrpcMessage { TransactionId = new byte[] { 1 }, Type = KrpcType.Response, Body = r };

        var back = KrpcMessage.Decode(msg.Encode());
        Assert.Equal(KrpcType.Response, back.Type);
        Assert.Equal("tok", back.Body["token"]!.AsText());
    }

    [Fact]
    public void Error_roundtrips()
    {
        var msg = new KrpcMessage { TransactionId = new byte[] { 1 }, Type = KrpcType.Error, Error = (201, "Generic Error") };
        var back = KrpcMessage.Decode(msg.Encode());
        Assert.Equal(KrpcType.Error, back.Type);
        Assert.Equal(201, back.Error!.Value.Code);
        Assert.Equal("Generic Error", back.Error.Value.Message);
    }

    [Fact]
    public void Compact_node_info_roundtrips()
    {
        var contacts = new[]
        {
            new DhtContact(new NodeId(Enumerable.Repeat((byte)1, 20).ToArray()), new IPEndPoint(IPAddress.Parse("1.2.3.4"), 6881)),
            new DhtContact(new NodeId(Enumerable.Repeat((byte)2, 20).ToArray()), new IPEndPoint(IPAddress.Parse("10.0.0.9"), 51413)),
        };
        var enc = CompactInfo.EncodeNodes(contacts);
        Assert.Equal(52, enc.Length);

        var dec = CompactInfo.DecodeNodes(enc);
        Assert.Equal(2, dec.Count);
        Assert.Equal("1.2.3.4:6881", $"{dec[0].EndPoint.Address}:{dec[0].EndPoint.Port}");
        Assert.Equal("10.0.0.9:51413", $"{dec[1].EndPoint.Address}:{dec[1].EndPoint.Port}");
        Assert.Equal(contacts[0].Id, dec[0].Id);
    }

    [Fact]
    public void Compact_peer_values_roundtrip()
    {
        var ep = new IPEndPoint(IPAddress.Parse("8.8.4.4"), 1234);
        var values = new BencodeList(new BencodeValue[] { new BencodeString(CompactInfo.EncodePeer(ep)) });
        var peers = CompactInfo.DecodePeerValues(values);
        Assert.Equal("8.8.4.4:1234", Assert.Single(peers).ToString());
    }
}

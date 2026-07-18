// SPDX-License-Identifier: MIT

using System.Net;
using System.Security.Cryptography;
using AetherNet.BitTorrent.Dht;
using Xunit;

namespace AetherNet.BitTorrent.Tests;

public class NodeIdTests
{
    [Fact]
    public void Distance_is_xor_and_self_distance_is_zero()
    {
        var a = new NodeId(Enumerable.Repeat((byte)0xF0, 20).ToArray());
        var b = new NodeId(Enumerable.Repeat((byte)0x0F, 20).ToArray());
        Assert.All(a.DistanceTo(b), x => Assert.Equal(0xFF, x));
        Assert.All(a.DistanceTo(a), x => Assert.Equal(0, x));
    }

    [Fact]
    public void Equality_and_roundtrip()
    {
        var bytes = RandomNumberGenerator.GetBytes(20);
        var id = new NodeId(bytes);
        Assert.Equal(id, new NodeId(bytes));
        Assert.Equal(bytes, id.ToBytes());
    }

    [Fact]
    public void Rejects_wrong_length() => Assert.Throws<ArgumentException>(() => new NodeId(new byte[19]));
}

public class RoutingTableTests
{
    private static NodeId Id(params (int index, byte value)[] set)
    {
        var b = new byte[20];
        foreach (var (i, v) in set) b[i] = v;
        return new NodeId(b);
    }

    private static DhtContact C(NodeId id) => new(id, new IPEndPoint(IPAddress.Loopback, 6881));

    [Fact]
    public void Does_not_add_self()
    {
        var self = NodeId.Random();
        var rt = new RoutingTable(self);
        Assert.False(rt.TryAdd(C(self)));
        Assert.Equal(0, rt.Count);
    }

    [Fact]
    public void Closest_orders_by_xor_distance()
    {
        var self = Id(); // all-zero
        var rt = new RoutingTable(self);
        var near = Id((19, 1));    // distance 0x00…01  (smallest)
        var mid = Id((0, 0x01));   // distance 0x01…00
        var far = Id((0, 0x80));   // distance 0x80…00  (largest)
        rt.TryAdd(C(mid));
        rt.TryAdd(C(far));
        rt.TryAdd(C(near));

        var closest = rt.ClosestTo(Id(), count: 3);
        Assert.Equal(near, closest[0].Id);
        Assert.Equal(mid, closest[1].Id);
        Assert.Equal(far, closest[2].Id);
    }

    [Fact]
    public void Refreshing_an_existing_contact_does_not_grow_count()
    {
        var rt = new RoutingTable(Id());
        var id = Id((10, 5));
        Assert.True(rt.TryAdd(new DhtContact(id, new IPEndPoint(IPAddress.Parse("1.1.1.1"), 1))));
        Assert.True(rt.TryAdd(new DhtContact(id, new IPEndPoint(IPAddress.Parse("2.2.2.2"), 2)))); // same id, new endpoint
        Assert.Equal(1, rt.Count);
    }

    [Fact]
    public void Bucket_caps_at_k()
    {
        var rt = new RoutingTable(Id()); // all-zero self
        // All these ids share their first non-zero distance byte (index 18, value 2) → same bucket;
        // they differ only in the last byte, so they are distinct contacts.
        for (int i = 0; i < RoutingTable.K + 3; i++)
            rt.TryAdd(C(Id((18, 2), (19, (byte)i))));
        Assert.Equal(RoutingTable.K, rt.Count);
    }
}

// SPDX-License-Identifier: MIT
using AetherNet.Map.Crdt;
using AetherNet.Map.Models;
using AetherNet.Map.Wire;
using Xunit;

namespace AetherNet.Map.Tests;

public class MapFeatureCodecTests
{
    private static readonly byte[] OwnerKey = [9, 9, 9];
    private static HybridLogicalClock Hlc(long ms, ushort c, string node) => new(ms, c, node);

    private static MapFeatureCrdt Rich()
    {
        var f = new MapFeatureCrdt("feat-9", MapFeatureType.Storefront, AuthorityMode.OwnerAuthoritative,
            OwnerKey, GeoPoint.At(48.8566, 2.3522, 9), Hlc(1000, 0, "g"));
        f.SetAttribute("name", MapValue.String("Café"), Hlc(1100, 0, "a"));
        f.SetAttribute("seats", MapValue.Int(42), Hlc(1200, 0, "a"));
        f.SetAttribute("rating", MapValue.Float(4.5), Hlc(1300, 0, "a"));
        f.SetAttribute("open", MapValue.Bool(true), Hlc(1400, 0, "a"));
        f.SetAttribute("cleared", null, Hlc(1500, 0, "a"));       // present key, cleared value
        f.AddTag("wifi", Hlc(1600, 0, "a"));
        f.AddTag("cash_only", Hlc(1650, 0, "a"));
        f.RemoveTag("cash_only", Hlc(1700, 0, "b"));               // removed tag
        f.AddWitness("ramp", "keyB");
        f.AddWitness("ramp", "keyA");
        f.Upvote("a"); f.Upvote("b"); f.Downvote("c");
        f.SetLocation(GeoPoint.At(48.86, 2.35, 9), Hlc(1800, 0, "a"));
        f.Delete(Hlc(1900, 0, "a"));
        return f;
    }

    [Fact]
    public void RoundTrip_IsByteIdentical()
    {
        var f = Rich();
        byte[] b1 = MapFeatureCodec.Serialize(f);
        var f2 = MapFeatureCodec.Deserialize(b1);
        byte[] b2 = MapFeatureCodec.Serialize(f2);
        Assert.Equal(b1, b2);
    }

    [Fact]
    public void RoundTrip_PreservesObservableState()
    {
        var f2 = MapFeatureCodec.Deserialize(MapFeatureCodec.Serialize(Rich()));
        Assert.Equal("Café", f2.PresentAttributes["name"].Text);
        Assert.Equal(42, f2.PresentAttributes["seats"].AsInt());
        Assert.Equal(4.5, f2.PresentAttributes["rating"].AsFloat());
        Assert.True(f2.PresentAttributes["open"].AsBool());
        Assert.False(f2.PresentAttributes.ContainsKey("cleared")); // cleared value not surfaced
        Assert.Contains("wifi", f2.Tags);
        Assert.DoesNotContain("cash_only", f2.Tags);
        Assert.Equal(2, f2.WitnessCount("ramp"));
        Assert.Equal(1, f2.Sentiment); // +1 +1 -1
        Assert.True(f2.IsDeleted);
        Assert.Equal(new GeoPoint(48.86, 2.35, Geohash.Encode(48.86, 2.35, 9)), f2.Location);
        Assert.Equal(MapFeatureType.Storefront, f2.FeatureType);
        Assert.Equal(OwnerKey, f2.OwnerPubKey);
    }

    // Two partitioned replicas exchange serialized state and converge byte-identically.
    private static MapFeatureCrdt Genesis() => new(
        "feat-1", MapFeatureType.Storefront, AuthorityMode.OwnerAuthoritative, OwnerKey,
        GeoPoint.At(51.5, -0.1, 9), Hlc(1000, 0, "creator"));

    [Fact]
    public void GossipViaWire_TwoNodesConverge_ByteIdentical()
    {
        MapFeatureCrdt EditedA()
        {
            var f = Genesis();
            f.SetAttribute("hours", MapValue.String("9-5"), Hlc(1500, 0, "A"));
            f.SetAttribute("name", MapValue.String("Cafe A"), Hlc(2000, 0, "A"));
            f.AddTag("wifi", Hlc(1400, 0, "A"));
            f.Upvote("A");
            return f;
        }
        MapFeatureCrdt EditedB()
        {
            var f = Genesis();
            f.SetAttribute("phone", MapValue.String("555"), Hlc(1600, 0, "B"));
            f.SetAttribute("name", MapValue.String("Cafe B"), Hlc(2000, 0, "B"));
            f.AddTag("outdoor", Hlc(1400, 0, "B"));
            f.Upvote("B");
            return f;
        }

        // A receives B's wire state and merges; B receives A's.
        var a = EditedA();
        a.Merge(MapFeatureCodec.Deserialize(MapFeatureCodec.Serialize(EditedB())));
        var b = EditedB();
        b.Merge(MapFeatureCodec.Deserialize(MapFeatureCodec.Serialize(EditedA())));

        Assert.Equal(MapFeatureCodec.Serialize(a), MapFeatureCodec.Serialize(b)); // converged, content-identical
        Assert.Equal("9-5", a.PresentAttributes["hours"].Text);
        Assert.Equal("555", a.PresentAttributes["phone"].Text);
        Assert.Equal("Cafe B", a.PresentAttributes["name"].Text);
    }

    [Fact]
    public void Deserialize_RejectsBadVersion()
    {
        var bytes = MapFeatureCodec.Serialize(Rich());
        bytes[0] = 0x7F;
        Assert.Throws<FormatException>(() => MapFeatureCodec.Deserialize(bytes));
    }

    [Fact]
    public void Deserialize_RejectsTruncated()
    {
        var bytes = MapFeatureCodec.Serialize(Rich());
        Assert.Throws<FormatException>(() => MapFeatureCodec.Deserialize(bytes[..(bytes.Length / 2)]));
    }
}

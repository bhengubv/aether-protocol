// SPDX-License-Identifier: MIT
using AetherNet.Map.Crdt;
using AetherNet.Map.Models;
using Xunit;

namespace AetherNet.Map.Tests;

public class MapFeatureCrdtTests
{
    private static readonly byte[] OwnerKey = [1, 2, 3, 4];
    private static HybridLogicalClock Hlc(long ms, ushort c, string node) => new(ms, c, node);

    private static MapFeatureCrdt Genesis() => new(
        featureId: "feat-1",
        featureType: MapFeatureType.Storefront,
        authorityMode: AuthorityMode.OwnerAuthoritative,
        ownerPubKey: OwnerKey,
        location: GeoPoint.At(51.5, -0.1, 9),
        genesisClock: Hlc(1000, 0, "creator"));

    // A and B edit the same feature while partitioned.
    private static MapFeatureCrdt EditedA()
    {
        var f = Genesis();
        f.SetAttribute("hours", MapValue.String("9-5"), Hlc(1500, 0, "A")); // A-only field
        f.SetAttribute("name", MapValue.String("Cafe A"), Hlc(2000, 0, "A")); // same field, concurrent
        f.AddTag("wifi", Hlc(1400, 0, "A"));
        f.Upvote("A");
        return f;
    }

    private static MapFeatureCrdt EditedB()
    {
        var f = Genesis();
        f.SetAttribute("phone", MapValue.String("555-1234"), Hlc(1600, 0, "B")); // B-only field
        f.SetAttribute("name", MapValue.String("Cafe B"), Hlc(2000, 0, "B")); // same field, concurrent
        f.AddTag("outdoor_seating", Hlc(1400, 0, "B"));
        f.Upvote("B");
        return f;
    }

    [Fact]
    public void Merge_ConvergesRegardlessOfOrder_AndLosesNoFieldEdit()
    {
        var ab = EditedA(); ab.Merge(EditedB());
        var ba = EditedB(); ba.Merge(EditedA());

        // No lost edits: A's "hours" and B's "phone" both survive (the whole-record LWW failure mode).
        Assert.Equal("9-5", ab.PresentAttributes["hours"].Text);
        Assert.Equal("555-1234", ab.PresentAttributes["phone"].Text);
        Assert.Equal("9-5", ba.PresentAttributes["hours"].Text);
        Assert.Equal("555-1234", ba.PresentAttributes["phone"].Text);

        // Same-field concurrent edit resolves deterministically to the higher HLC (node "B" > "A").
        Assert.Equal("Cafe B", ab.PresentAttributes["name"].Text);
        Assert.Equal("Cafe B", ba.PresentAttributes["name"].Text);

        // Both orders converge on identical observable state.
        Assert.Equal(
            ab.PresentAttributes.OrderBy(kv => kv.Key).Select(kv => $"{kv.Key}={kv.Value.Text}"),
            ba.PresentAttributes.OrderBy(kv => kv.Key).Select(kv => $"{kv.Key}={kv.Value.Text}"));
        Assert.Equal(ab.Tags.OrderBy(t => t), ba.Tags.OrderBy(t => t));
        Assert.Equal(ab.Sentiment, ba.Sentiment);
        Assert.Equal(2, ab.Sentiment); // A + B each +1
        Assert.Contains("wifi", ab.Tags);
        Assert.Contains("outdoor_seating", ab.Tags);
    }

    [Fact]
    public void Merge_IsIdempotent()
    {
        var once = EditedA(); once.Merge(EditedB());
        var twice = EditedA(); twice.Merge(EditedB()); twice.Merge(EditedB());

        Assert.Equal(
            once.PresentAttributes.OrderBy(kv => kv.Key).Select(kv => $"{kv.Key}={kv.Value.Text}"),
            twice.PresentAttributes.OrderBy(kv => kv.Key).Select(kv => $"{kv.Key}={kv.Value.Text}"));
        Assert.Equal(once.Sentiment, twice.Sentiment);
    }

    [Fact]
    public void ObservedConsensus_WitnessesAccumulate_DistinctCount()
    {
        var f = Genesis();
        f.AddWitness("ramp", "keyA");
        f.AddWitness("ramp", "keyB");
        f.AddWitness("ramp", "keyA"); // idempotent — same witness
        Assert.Equal(2, f.WitnessCount("ramp"));
        Assert.Equal(0, f.WitnessCount("unseen"));
    }

    [Fact]
    public void Tombstone_DeleteConverges_AndIsLww()
    {
        var a = Genesis();
        a.Delete(Hlc(3000, 0, "A"));
        var b = Genesis();
        b.Undelete(Hlc(3100, 0, "B")); // later undelete wins
        a.Merge(b);
        Assert.False(a.IsDeleted);
    }

    [Fact]
    public void Merge_DifferentFeatureIdentity_Throws()
    {
        var a = Genesis();
        var other = new MapFeatureCrdt("feat-1", MapFeatureType.SidewalkFeature, AuthorityMode.ObservedConsensus,
            null, GeoPoint.At(0, 0, 9), Hlc(1000, 0, "x"));
        Assert.Throws<ArgumentException>(() => a.Merge(other));
    }
}

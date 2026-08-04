// SPDX-License-Identifier: MIT
using AetherNet.Map;
using AetherNet.Map.Crdt;
using AetherNet.Map.Models;
using Xunit;

namespace AetherNet.Map.Tests;

public class MapStoreTests
{
    private static readonly byte[] OwnerKey = [7];
    private static HybridLogicalClock Hlc(long ms, ushort c, string node) => new(ms, c, node);

    private static MapFeatureCrdt Feature(string id, double lat, double lon, MapFeatureType type, HybridLogicalClock clock, string name)
    {
        var f = new MapFeatureCrdt(id, type, AuthorityMode.ObservedConsensus, null, GeoPoint.At(lat, lon, 9), clock);
        f.SetAttribute("name", MapValue.String(name), clock);
        return f;
    }

    [Fact]
    public async Task Apply_MergesSameFeature_AndIsIdempotent()
    {
        var store = new InMemoryMapStore();
        var loc = GeoPoint.At(51.5, -0.1, 9);
        var a = new MapFeatureCrdt("f1", MapFeatureType.Storefront, AuthorityMode.OwnerAuthoritative, OwnerKey, loc, Hlc(1000, 0, "c"));
        a.SetAttribute("hours", MapValue.String("9-5"), Hlc(1500, 0, "A"));
        var b = new MapFeatureCrdt("f1", MapFeatureType.Storefront, AuthorityMode.OwnerAuthoritative, OwnerKey, loc, Hlc(1000, 0, "c"));
        b.SetAttribute("phone", MapValue.String("555"), Hlc(1600, 0, "B"));

        await store.ApplyAsync(a);
        await store.ApplyAsync(b);
        await store.ApplyAsync(b); // idempotent re-delivery

        var merged = await store.GetAsync("f1");
        Assert.NotNull(merged);
        Assert.Equal("9-5", merged!.PresentAttributes["hours"].Text);
        Assert.Equal("555", merged.PresentAttributes["phone"].Text);
        Assert.Equal(1, await store.CountAsync());
    }

    [Fact]
    public async Task QueryProximity_ReturnsCellAndNeighbours_ExcludesFar()
    {
        var store = new InMemoryMapStore();
        await store.ApplyAsync(Feature("london", 51.5069, -0.1276, MapFeatureType.Landmark, Hlc(1000, 0, "x"), "London"));
        await store.ApplyAsync(Feature("paris", 48.8566, 2.3522, MapFeatureType.Landmark, Hlc(1000, 0, "x"), "Paris"));

        var center = Geohash.Encode(51.5069, -0.1276, 6);
        var near = await store.QueryProximityAsync(center);

        Assert.Single(near);
        Assert.Equal("london", near[0].FeatureId);
    }

    [Fact]
    public async Task QueryProximity_FiltersByType()
    {
        var store = new InMemoryMapStore();
        await store.ApplyAsync(Feature("shop", 51.5069, -0.1276, MapFeatureType.Storefront, Hlc(1000, 0, "x"), "Shop"));
        await store.ApplyAsync(Feature("ramp", 51.5069, -0.1276, MapFeatureType.SidewalkFeature, Hlc(1000, 0, "x"), "Ramp"));

        var center = Geohash.Encode(51.5069, -0.1276, 6);
        var shops = await store.QueryProximityAsync(center, type: MapFeatureType.Storefront);

        Assert.Single(shops);
        Assert.Equal("shop", shops[0].FeatureId);
    }

    [Fact]
    public async Task ChangedSince_FiltersByCursor()
    {
        var store = new InMemoryMapStore();
        await store.ApplyAsync(Feature("old", 51.5, -0.1, MapFeatureType.Landmark, Hlc(1000, 0, "x"), "Old"));
        await store.ApplyAsync(Feature("new", 51.5, -0.1, MapFeatureType.Landmark, Hlc(2000, 0, "x"), "New"));

        var changed = await store.ChangedSinceAsync(Hlc(1500, 0, ""));
        Assert.Single(changed);
        Assert.Equal("new", changed[0].FeatureId);

        var max = await store.MaxClockAsync();
        Assert.Equal(2000, max.PhysicalMs);
    }

    [Fact]
    public async Task QueryProximity_ExcludesDeleted_UnlessRequested()
    {
        var store = new InMemoryMapStore();
        var f = Feature("gone", 51.5069, -0.1276, MapFeatureType.Landmark, Hlc(1000, 0, "x"), "Gone");
        f.Delete(Hlc(2000, 0, "y"));
        await store.ApplyAsync(f);

        var center = Geohash.Encode(51.5069, -0.1276, 6);
        Assert.Empty(await store.QueryProximityAsync(center));
        Assert.Single(await store.QueryProximityAsync(center, includeDeleted: true));
    }
}

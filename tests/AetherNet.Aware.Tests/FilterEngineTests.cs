// SPDX-License-Identifier: MIT
// Ported from Fieldwatch (cf6562d) app/src/test/java/app/fieldwatch/domain/FilterEngineTest.kt, plus
// SignatureExchangeTest.stockPresetIdsAreBuiltInAndUnique. Not ported: FilterPreset.isBuiltIn(), which tells
// Fieldwatch's saved-config migration its own retired chips apart — there is no such config here.
// Copyright (c) 2026 Off Grid Pete LLC. See src/AetherNet.Aware/NOTICE.md.

using Xunit;

namespace AetherNet.Aware.Tests;

public class FilterEngineTests
{
    private static readonly Sighting Labeled = Ble("BLE:AA:BB:CC:DD:EE:FF");
    private static readonly Sighting Other = Ble("BLE:11:22:33:44:55:66");
    private static readonly Sighting Signed = Labeled with { FleetIds = ["fleet-airtag"] };

    [Fact]
    public void StockPresetsMatchShortSetWithWatchedOnly()
    {
        var presets = FilterEngine.DefaultPresets();
        Assert.Equal(["all", "wifi", "ble", "strong", "with-you", "watched"], presets.Select(p => p.Id));
        var watched = presets.Single(p => p.Id == "watched");
        Assert.Equal("Watched only", watched.Name);
        Assert.True(watched.Filter.WatchedOnly);
        Assert.True(watched.Filter.ShowWifi);
        Assert.True(watched.Filter.ShowBle);
        Assert.Equal(-100, watched.Filter.RssiMin);
    }

    [Fact]
    public void StockPresetIdsAreUnique()
    {
        var presets = FilterEngine.DefaultPresets();
        Assert.Equal(presets.Select(p => p.Id), presets.Select(p => p.Id).Distinct());
        Assert.Equal(6, presets.Count);
        Assert.Equal("watched", presets.First(p => p.Name == "Watched only").Id);
        Assert.DoesNotContain(presets, p => p.Id == "cameras");
        Assert.DoesNotContain(presets, p => p.Id == "trackers");
    }

    [Fact]
    public void CustomNamesOnlyKeepsLabeledKeys()
    {
        var filter = new FilterState { CustomNamesOnly = true };
        var keys = new HashSet<string> { Labeled.Key };
        Assert.True(FilterEngine.Pass(Labeled, filter, namedRadioKeys: keys));
        Assert.False(FilterEngine.Pass(Other, filter, namedRadioKeys: keys));
        Assert.False(FilterEngine.Pass(Labeled, filter, namedRadioKeys: new HashSet<string>()));
    }

    [Fact]
    public void CustomNamesOnlyOffDoesNotHide()
    {
        Assert.True(FilterEngine.Pass(Other, new FilterState(), namedRadioKeys: new HashSet<string> { Labeled.Key }));
    }

    [Fact]
    public void HideFastPairAccountKeyDropsPlazaChipsKeepsPairingAndDual()
    {
        var hide = new FilterState { HideFastPairAccountKey = true };
        var account = Labeled with { FleetIds = ["fleet-fast-pair"], FastPairPairing = false };
        var pairing = Labeled with { FleetIds = ["fleet-fast-pair"], FastPairPairing = true };
        var dual = Labeled with { FleetIds = ["fleet-fast-pair", "fleet-google"], FastPairPairing = false };
        Assert.False(FilterEngine.Pass(account, hide));
        Assert.True(FilterEngine.Pass(pairing, hide));
        Assert.True(FilterEngine.Pass(dual, hide));
        Assert.True(FilterEngine.Pass(account, new FilterState()));
    }

    [Fact]
    public void WatchedOnlyKeepsBookmarkedSignature()
    {
        var filter = new FilterState { WatchedOnly = true };
        Assert.True(FilterEngine.Pass(Signed, filter, watchedFleetIds: new HashSet<string> { "fleet-airtag" }));
        Assert.False(FilterEngine.Pass(Signed, filter, watchedFleetIds: new HashSet<string>()));
        Assert.False(FilterEngine.Pass(Other, filter, watchedFleetIds: new HashSet<string> { "fleet-airtag" }));
    }

    [Fact]
    public void WatchedOnlyKeepsAlertNamedRadioNotLabelOnly()
    {
        var filter = new FilterState { WatchedOnly = true };
        Assert.True(FilterEngine.Pass(Labeled, filter, alertDeviceKeys: new HashSet<string> { Labeled.Key }));
        Assert.False(FilterEngine.Pass(Labeled, filter, namedRadioKeys: new HashSet<string> { Labeled.Key }));
    }

    [Fact]
    public void WatchedOnlyAndHideSurveillanceStillHidesCameras()
    {
        var filter = new FilterState
        {
            WatchedOnly = true,
            UseClassFilter = true,
            ExcludeClasses = true,
            Classes = new HashSet<SignatureClass> { SignatureClass.Surveillance },
        };
        var flock = Signed with { FleetIds = ["fleet-flock"] };
        var axon = Other with { FleetIds = ["fleet-axon"] };
        var byClass = new Dictionary<string, SignatureClass>
        {
            ["fleet-flock"] = SignatureClass.Surveillance,
            ["fleet-axon"] = SignatureClass.LawEnforcement,
        };
        var watched = new HashSet<string> { "fleet-flock", "fleet-axon" };
        Assert.False(FilterEngine.Pass(flock, filter, classByFleetId: byClass, watchedFleetIds: watched));
        Assert.True(FilterEngine.Pass(axon, filter, classByFleetId: byClass, watchedFleetIds: watched));
    }

    [Fact]
    public void WatchedOnlyStaysAndInOrLogic()
    {
        var filter = new FilterState { WatchedOnly = true, Logic = FilterLogic.Or, NameQuery = "anything" };
        var namedUnwatched = Labeled with { Name = "anything" };
        Assert.False(FilterEngine.Pass(namedUnwatched, filter, watchedFleetIds: new HashSet<string> { "fleet-airtag" }));
        Assert.True(FilterEngine.Pass(Signed with { Name = "anything" }, filter, watchedFleetIds: new HashSet<string> { "fleet-airtag" }));
    }

    [Fact]
    public void SignaturesOnlyAndCustomNamesStack()
    {
        var both = new FilterState { NamedOnly = true, CustomNamesOnly = true };
        var keys = new HashSet<string> { Labeled.Key, Other.Key };
        Assert.True(FilterEngine.Pass(Signed, both, namedRadioKeys: keys));
        Assert.False(FilterEngine.Pass(Labeled, both, namedRadioKeys: keys));
        Assert.False(FilterEngine.Pass(Signed with { Key = Other.Key }, both, namedRadioKeys: new HashSet<string> { Labeled.Key }));
    }

    private static Sighting Ble(string key) => new()
    {
        Key = key,
        Kind = RadioKind.Ble,
        Mac = key[(key.IndexOf(':') + 1)..],
        Rssi = -50,
        RssiMin = -50,
        RssiMax = -50,
        Randomized = true,
        FirstSeen = 1,
        LastSeen = 1,
        HitCount = 1,
    };
}

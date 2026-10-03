// SPDX-License-Identifier: MIT
// Smaller ported pieces. RssiNotAvailable is the Rssi half of Fieldwatch's DeviceExplainTest.rssi127IsNotAvailable
// (cf6562d); the rest pin ported code Fieldwatch has no direct test for: TrackerMatch's kind and label, signature
// groups that need several radios at once (none ship in the stock pack), and the folded "Body-worn" class.

using Xunit;

namespace AetherNet.Aware.Tests;

public class SmallPiecesTests
{
    private static readonly IReadOnlyDictionary<string, string> Names =
        StockSignatures.Fleets.ToDictionary(f => f.Id, f => f.Name);

    [Fact]
    public void RssiNotAvailable()
    {
        Assert.False(Rssi.Measured(127));
        Assert.True(Rssi.Measured(-127));
        Assert.Equal("-103 to -86 dBm", Rssi.SessionRange(-103, 127, [new(1, -86), new(2, 127)]));
        Assert.Equal("Not available", Rssi.SessionRange(127, 127));
        Assert.Equal(-99, Rssi.LastMeasured(127, [new(1, -99), new(2, 127)]));
    }

    [Fact]
    public void TrackerKindFromSignatureNamesAndPayload()
    {
        var airTag = Ble("AA:BB:CC:DD:EE:01") with { FleetIds = ["fleet-airtag"] };
        Assert.Equal(TrackerKind.Finder, TrackerMatch.KindOf(airTag, Names));
        Assert.True(TrackerMatch.IsTracker(airTag, Names));

        var findMy = Ble("AA:BB:CC:DD:EE:02") with
        {
            ManufacturerId = 0x004C,
            ManufacturerDataHex = "12" + string.Concat(Enumerable.Repeat("00", 24)),
        };
        Assert.Equal(TrackerKind.Finder, TrackerMatch.KindOf(findMy, Names));
        Assert.Equal("Apple Find My / Offline Finding", TrackerMatch.Label(findMy, Names));

        var beacon = Ble("AA:BB:CC:DD:EE:03") with { FleetIds = ["fleet-ibeacon"], RssiMax = -80, RssiMin = -90 };
        Assert.Equal(TrackerKind.Beacon, TrackerMatch.KindOf(beacon, Names));

        var quiet = Ble("AA:BB:CC:DD:EE:04");
        Assert.Null(TrackerMatch.KindOf(quiet, Names));
        Assert.Equal("tracker-like", TrackerMatch.Label(quiet, Names));
    }

    [Fact]
    public void ALoudApplePhoneCountsAsCarried()
    {
        var phone = Ble("AA:BB:CC:DD:EE:05") with
        {
            ManufacturerId = 0x004C,
            ManufacturerDataHex = "10AABBCCDDEE",
            RssiMin = -60,
            RssiMax = -45,
        };
        Assert.True(TrackerMatch.IsCarriedApple(phone, Names));
        Assert.Equal(TrackerKind.Finder, TrackerMatch.KindOf(phone, Names));
        Assert.False(TrackerMatch.IsCarriedApple(phone with { RssiMin = -80 }, Names));
    }

    [Fact]
    public void AGroupNeedsItsPeersInTheWindow()
    {
        var bikes = new Fleet
        {
            Id = "custom-bikes",
            Name = "Bike lights",
            MinPeers = 3,
            PeerWindowSec = 60,
            Rules = [new MatchRule { Kind = RuleKind.NameContains, Text = "BikeLight" }],
        };
        const long now = 1_790_000_000_000;
        var a = Ble("AA:BB:CC:DD:EE:11") with { Name = "BikeLight A", LastSeen = now };
        var b = Ble("AA:BB:CC:DD:EE:12") with { Name = "BikeLight B", LastSeen = now - 10_000 };
        var c = Ble("AA:BB:CC:DD:EE:13") with { Name = "BikeLight C", LastSeen = now - 120_000 };
        var engine = new SignatureEngine();

        var two = engine.Match([a, b, c], [bikes], now);
        Assert.Empty(two[a.Key]);
        Assert.Empty(two[b.Key]);

        var three = engine.Match([a, b, c with { LastSeen = now }], [bikes], now);
        Assert.Contains("custom-bikes", three[a.Key]);
        Assert.Contains("custom-bikes", three[b.Key]);
        Assert.Contains("custom-bikes", three[c.Key]);
    }

    [Fact]
    public void BodywornFoldsIntoWearables()
    {
        Assert.Equal(SignatureClass.Wearable, SignatureClass.Bodyworn.Folded());
        Assert.Equal(SignatureClass.Drone, SignatureClass.Drone.Folded());
        Assert.Equal("Finder tags", SignatureClass.Finder.Label());
        Assert.Equal("Public safety", SignatureClass.LawEnforcement.Label());
    }

    private static Sighting Ble(string mac) => new()
    {
        Key = "BLE:" + mac,
        Kind = RadioKind.Ble,
        Mac = mac,
        Rssi = -70,
        RssiMin = -90,
        RssiMax = -70,
        Randomized = true,
        FirstSeen = 1,
        LastSeen = 1,
        HitCount = 1,
    };
}

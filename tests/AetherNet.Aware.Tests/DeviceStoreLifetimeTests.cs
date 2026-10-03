// SPDX-License-Identifier: MIT
// How long the store keeps what was heard. Fieldwatch (cf6562d) has no tests for its refresh/evict path; these pin
// the ported behaviour: gone after the linger, unlabelled rows forgotten after 3 minutes, labelled after 15, and a
// tag left behind leaving "moving with you". Fieldwatch keys a radio by its address, so a device that rotates its
// address shows as a new row each time; the old row is forgotten on the same clock as any other.

using Xunit;

namespace AetherNet.Aware.Tests;

public class DeviceStoreLifetimeTests
{
    private static readonly IReadOnlyList<Fleet> Fleets = StockSignatures.Fleets;
    private readonly ManualClock _clock = new();

    [Fact]
    public void ARadioNotHeardPastTheLingerIsGoneAndComesBackWithANewSpan()
    {
        var store = new DeviceStore(timeProvider: _clock);
        store.IngestBatch([Heard("AA:BB:CC:DD:EE:01")], Fleets);
        store.Refresh(Fleets, staleSec: 30);
        Assert.False(store.Devices.Single().Gone);

        _clock.Advance(TimeSpan.FromSeconds(31));
        store.Refresh(Fleets, staleSec: 30);
        var gone = store.Devices.Single();
        Assert.True(gone.Gone);
        Assert.NotNull(Assert.Single(gone.Presence).End);

        store.IngestBatch([Heard("AA:BB:CC:DD:EE:01")], Fleets);
        store.Refresh(Fleets, staleSec: 30);
        var back = store.Devices.Single();
        Assert.False(back.Gone);
        Assert.Equal(2, back.Presence.Count);
        Assert.Null(back.Presence[^1].End);
    }

    [Fact]
    public void UnlabelledRowsAreForgottenAfterThreeMinutesLabelledAfterFifteen()
    {
        var store = new DeviceStore(timeProvider: _clock);
        var plain = Heard("AA:BB:CC:DD:EE:01");
        var airTag = Heard("AA:BB:CC:DD:EE:02", name: "AirTag");
        store.IngestBatch([plain, airTag], Fleets);
        Assert.Empty(store.Find("BLE:AA:BB:CC:DD:EE:01")!.FleetIds);
        Assert.Contains("fleet-airtag", store.Find("BLE:AA:BB:CC:DD:EE:02")!.FleetIds);

        _clock.Advance(TimeSpan.FromMinutes(3) + TimeSpan.FromSeconds(1));
        store.Refresh(Fleets, staleSec: 30);
        Assert.Null(store.Find("BLE:AA:BB:CC:DD:EE:01"));
        Assert.NotNull(store.Find("BLE:AA:BB:CC:DD:EE:02"));

        _clock.Advance(TimeSpan.FromMinutes(12));
        store.Refresh(Fleets, staleSec: 30);
        Assert.Null(store.Find("BLE:AA:BB:CC:DD:EE:02"));
        Assert.Empty(store.Devices);
    }

    [Fact]
    public void ARotatedAddressLeavesItsOldRowToBeForgotten()
    {
        var store = new DeviceStore(timeProvider: _clock);
        store.IngestBatch([Heard("5A:11:22:33:44:01")], Fleets);
        _clock.Advance(TimeSpan.FromMinutes(1));
        store.IngestBatch([Heard("6B:11:22:33:44:02")], Fleets);
        store.Refresh(Fleets, staleSec: 30);
        Assert.Equal(2, store.Devices.Count);
        Assert.Equal(2, store.Stats.DevicesSeen);

        _clock.Advance(TimeSpan.FromMinutes(2) + TimeSpan.FromSeconds(1));
        store.IngestBatch([Heard("6B:11:22:33:44:02")], Fleets);
        store.Refresh(Fleets, staleSec: 30);
        Assert.Equal("BLE:6B:11:22:33:44:02", Assert.Single(store.Devices).Key);
    }

    [Fact]
    public void ACrowdOfUnlabelledRadiosIsCappedOldestFirst()
    {
        var store = new DeviceStore(timeProvider: _clock);
        for (var i = 0; i < 450; i++)
        {
            store.IngestBatch([Heard($"5A:00:00:00:{i / 256:X2}:{i % 256:X2}")], Fleets);
            _clock.Advance(TimeSpan.FromMilliseconds(100));
        }
        _clock.Advance(TimeSpan.FromSeconds(31));
        store.Refresh(Fleets, staleSec: 30);
        Assert.Equal(400, store.Devices.Count);
        Assert.Null(store.Find("BLE:5A:00:00:00:00:00"));
        Assert.NotNull(store.Find("BLE:5A:00:00:00:01:C1"));
    }

    [Fact]
    public void ATagCarriedThenLeftStopsBeingWithYou()
    {
        var store = new DeviceStore(timeProvider: _clock);
        var path = new OperatorPath(_clock);
        const double lat0 = -26.2041;
        const double lon0 = 28.0473;
        const double step = 20.0 / 111_195.0; // 20 m: over the 15 m the walk needs to add a point
        for (var i = 0; i < 12; i++)
        {
            var lat = lat0 + i * step;
            path.Record(lat, lon0, _clock.NowMs);
            store.IngestBatch([Heard("AA:BB:CC:DD:EE:07", name: "AirTag", lat: lat, lon: lon0, rssi: -55)], Fleets);
            _clock.Advance(TimeSpan.FromSeconds(30));
        }
        var carried = store.Find("BLE:AA:BB:CC:DD:EE:07")!;
        Assert.True(CoTravel.WithYou(carried, CoTravel.Ctx.Of(path.Copy()), _clock.NowMs));

        // Left behind: the walk goes on and the tag is not heard again.
        for (var i = 12; i < 20; i++)
        {
            path.Record(lat0 + i * step, lon0, _clock.NowMs);
            _clock.Advance(TimeSpan.FromSeconds(30));
        }
        var left = store.Find("BLE:AA:BB:CC:DD:EE:07")!;
        Assert.False(CoTravel.WithYou(left, CoTravel.Ctx.Of(path.Copy()), _clock.NowMs));
    }

    private Observation Heard(string mac, string name = "", double? lat = null, double? lon = null, int rssi = -60) => new()
    {
        Kind = RadioKind.Ble,
        Mac = mac,
        Name = name,
        Rssi = rssi,
        At = _clock.NowMs,
        Latitude = lat,
        Longitude = lon,
    };
}

// SPDX-License-Identifier: MIT
// Ported from Fieldwatch app/src/test/java/app/fieldwatch/data/DeviceStoreTest.kt (cf6562d). The clock is a
// ManualClock instead of System.currentTimeMillis(); otherwise the inputs and answers are upstream's.
// Copyright (c) 2026 Off Grid Pete LLC. See src/AetherNet.Aware/NOTICE.md.

using Xunit;

namespace AetherNet.Aware.Tests;

public class DeviceStoreTests
{
    private static readonly IReadOnlyList<Fleet> Fleets = StockSignatures.Fleets;
    private const string Location = "0D0012200000000084D717007FE4D3000098083408000000000000";
    private const string BasicId = "0D000212" + "5445535453455249414C31323334353637383930" + "000000";

    private readonly ManualClock _clock = new();

    [Fact]
    public void CiscoOuiLabelsOnIngestAndSurvivesRssiOnly()
    {
        var store = new DeviceStore(timeProvider: _clock);
        const string mac = "00:00:0C:11:22:33";
        store.IngestBatch([Wifi(mac, name: "Campus", rssi: -60)], Fleets);
        Assert.Contains("fleet-cisco", store.Find("WIFI:" + mac)!.FleetIds);

        store.IngestBatch([Wifi(mac, name: "Campus", rssi: -42)], Fleets);
        var again = store.Find("WIFI:" + mac)!;
        Assert.Contains("fleet-cisco", again.FleetIds);
        Assert.Equal(-42, again.Rssi);
    }

    [Fact]
    public void UnifiVirtualBssidLabelsWhenVendorIeArrives()
    {
        var store = new DeviceStore(timeProvider: _clock);
        const string mac = "82:F9:2C:00:00:01";
        store.IngestBatch([Wifi(mac, name: "Deep Learning")], Fleets);
        Assert.DoesNotContain("fleet-unifi-ap", store.Find("WIFI:" + mac)!.FleetIds);

        store.IngestBatch([Wifi(mac, name: "Deep Learning", rssi: -55)], Fleets);
        Assert.DoesNotContain("fleet-unifi-ap", store.Find("WIFI:" + mac)!.FleetIds);

        store.IngestBatch([Wifi(mac, name: "Deep Learning", ies: ["00:50:F2", "00:0F:AC", "AC:8B:A9"])], Fleets);
        Assert.Contains("fleet-unifi-ap", store.Find("WIFI:" + mac)!.FleetIds);
    }

    [Fact]
    public void NameAppearingLaterCanLabel()
    {
        var store = new DeviceStore(timeProvider: _clock);
        const string mac = "DE:AD:00:11:22:33";
        store.IngestBatch([Wifi(mac, name: "")], Fleets);
        Assert.DoesNotContain("fleet-netgear", store.Find("WIFI:" + mac)!.FleetIds);

        store.IngestBatch([Wifi(mac, name: "NETGEAR-12AB")], Fleets);
        Assert.Contains("fleet-netgear", store.Find("WIFI:" + mac)!.FleetIds);
    }

    [Fact]
    public void BleCiscoOuiDoesNotTakeWifiSignature()
    {
        var store = new DeviceStore(timeProvider: _clock);
        const string mac = "00:00:0C:11:22:33";
        store.IngestBatch([Ble(mac, name: "Campus")], Fleets);
        Assert.DoesNotContain("fleet-cisco", store.Find("BLE:" + mac)!.FleetIds);
    }

    [Fact]
    public void FastPairPairingSticksAfterAccountKeyPayload()
    {
        var store = new DeviceStore(timeProvider: _clock);
        const string mac = "AA:BB:CC:DD:EE:01";
        store.IngestBatch([Ble(mac, "", new RadioFacts { ServiceData = [new("FE2C", "2A4139")] })], Fleets);
        Assert.True(store.Find("BLE:" + mac)!.FastPairPairing);
        store.IngestBatch(
            [Ble(mac, "", new RadioFacts { ServiceData = [new("FE2C", "00112233445566778899AABBCCDDEEFF")] })],
            Fleets);
        var again = store.Find("BLE:" + mac)!;
        Assert.True(again.FastPairPairing);
        Assert.Contains("fleet-fast-pair", again.FleetIds);
    }

    [Fact]
    public void RemoteIdLocationSticksAfterBasicIdPacket()
    {
        var store = new DeviceStore(timeProvider: _clock);
        const string mac = "AA:BB:CC:DD:EE:02";
        store.IngestBatch([Ble(mac, "", new RadioFacts { ServiceData = [new("FFFA", Location)] })], Fleets);
        var first = store.Find("BLE:" + mac)!;
        Assert.Contains("fleet-remote-id", first.FleetIds);
        Assert.Equal(40.0, first.PayloadLat!.Value, 1e-6);
        Assert.Equal(-74.0, first.PayloadLon!.Value, 1e-6);
        Assert.Equal(100.0, first.PayloadAlt!.Value, 1e-6);

        store.IngestBatch([Ble(mac, "", new RadioFacts { ServiceData = [new("FFFA", BasicId)] })], Fleets);
        var again = store.Find("BLE:" + mac)!;
        Assert.Contains("fleet-remote-id", again.FleetIds);
        Assert.Equal(40.0, again.PayloadLat!.Value, 1e-6);
        Assert.Equal(-74.0, again.PayloadLon!.Value, 1e-6);
        Assert.Equal(100.0, again.PayloadAlt!.Value, 1e-6);
        Assert.Equal("TESTSERIAL1234567890", again.PayloadUasId);
    }

    [Fact]
    public void RemoteIdUasIdSticksAfterLocationPacket()
    {
        var store = new DeviceStore(timeProvider: _clock);
        const string mac = "AA:BB:CC:DD:EE:03";
        store.IngestBatch([Ble(mac, "", new RadioFacts { ServiceData = [new("FFFA", BasicId)] })], Fleets);
        Assert.Equal("TESTSERIAL1234567890", store.Find("BLE:" + mac)!.PayloadUasId);
        store.IngestBatch([Ble(mac, "", new RadioFacts { ServiceData = [new("FFFA", Location)] })], Fleets);
        var again = store.Find("BLE:" + mac)!;
        Assert.Equal("TESTSERIAL1234567890", again.PayloadUasId);
        Assert.Equal(40.0, again.PayloadLat!.Value, 1e-6);
        Assert.Equal(-74.0, again.PayloadLon!.Value, 1e-6);
    }

    [Fact]
    public void WifiRemoteIdVendorIeSetsPayloadPin()
    {
        var store = new DeviceStore(timeProvider: _clock);
        const string mac = "AA:BB:CC:DD:EE:04";
        var observation = Wifi(mac, name: "RID-WIFI", ies: ["FA:0B:BC"]) with
        {
            Facts = new RadioFacts { VendorIes = [new("FA:0B:BC", 0x0D, LocationWifiPayload())] },
        };
        store.IngestBatch([observation], Fleets);
        var device = store.Find("WIFI:" + mac)!;
        Assert.Contains("fleet-remote-id", device.FleetIds);
        Assert.Equal(40.0, device.PayloadLat!.Value, 1e-6);
        Assert.Equal(-74.0, device.PayloadLon!.Value, 1e-6);
        Assert.Equal(90.0, device.PayloadHeading!.Value, 1e-6);
        Assert.Equal(10.0, device.PayloadSpeed!.Value, 1e-6);
    }

    [Fact]
    public void RemoteIdWestHeadingAndHorizontalSpeed()
    {
        var store = new DeviceStore(timeProvider: _clock);
        const string mac = "AA:BB:CC:DD:EE:05";
        var location = "0D00" + Convert.ToHexString(OpenDroneIdTests.LocationMsg(dir: 90, ew: true, speed: 40));
        store.IngestBatch([Ble(mac, "", new RadioFacts { ServiceData = [new("FFFA", location)] })], Fleets);
        var device = store.Find("BLE:" + mac)!;
        Assert.Equal(270.0, device.PayloadHeading!.Value, 1e-6);
        Assert.Equal(10.0, device.PayloadSpeed!.Value, 1e-6);
    }

    [Fact]
    public void RadioHoldDoesNotResurrectAlreadyGone()
    {
        const long now = 1_000_000L;
        const long linger = 15_000L;
        const long lastSeen = now - 120_000L;
        Assert.False(DeviceStore.StillHeard(RadioKind.Wifi, lastSeen, alreadyGone: true, now, linger,
            wifiScanFresh: false, wifiHold: true, bleHold: false));
        Assert.True(DeviceStore.StillHeard(RadioKind.Wifi, lastSeen, alreadyGone: false, now, linger,
            wifiScanFresh: false, wifiHold: false, bleHold: false));
        Assert.False(DeviceStore.StillHeard(RadioKind.Ble, lastSeen, alreadyGone: true, now, linger,
            wifiScanFresh: true, wifiHold: false, bleHold: true));
        Assert.True(DeviceStore.StillHeard(RadioKind.Ble, now - 1_000L, alreadyGone: false, now, linger,
            wifiScanFresh: true, wifiHold: false, bleHold: false));
    }

    [Fact]
    public void Rssi127DoesNotBecomeMaxOrHistory()
    {
        var store = new DeviceStore(timeProvider: _clock);
        const string mac = "E0:9D:13:6E:71:03";
        store.IngestBatch([Ble(mac, "SmartTag", rssi: -92)], Fleets);
        store.IngestBatch([Ble(mac, "SmartTag", rssi: 127)], Fleets);
        var device = store.Find("BLE:" + mac)!;
        Assert.Equal(-92, device.Rssi);
        Assert.Equal(-92, device.RssiMin);
        Assert.Equal(-92, device.RssiMax);
        Assert.DoesNotContain(device.RssiHistory, s => s.Rssi == 127);
        store.IngestBatch([Ble(mac, "SmartTag", rssi: -86)], Fleets);
        var louder = store.Find("BLE:" + mac)!;
        Assert.Equal(-86, louder.Rssi);
        Assert.Equal(-92, louder.RssiMin);
        Assert.Equal(-86, louder.RssiMax);
    }

    [Fact]
    public void RefreshEmitsWithoutDroppingTags()
    {
        var store = new DeviceStore(timeProvider: _clock);
        const string mac = "00:00:0C:11:22:33";
        store.IngestBatch([Wifi(mac, name: "Campus")], Fleets);
        store.Refresh(Fleets, staleSec: 30);
        Assert.Contains("fleet-cisco", store.Devices.First(d => d.Mac == mac).FleetIds);
    }

    private Observation Wifi(string mac, string name = "", int rssi = -50, IReadOnlyList<string>? ies = null) => new()
    {
        Kind = RadioKind.Wifi,
        Mac = mac,
        Name = name,
        Rssi = rssi,
        Channel = 1,
        FrequencyMhz = 2412,
        At = _clock.NowMs,
        VendorIeOuis = ies ?? [],
    };

    private Observation Ble(string mac, string name, RadioFacts? facts = null, int rssi = -50) => new()
    {
        Kind = RadioKind.Ble,
        Mac = mac,
        Name = name,
        Rssi = rssi,
        ServiceUuids = facts is null ? [] : facts.ServiceData.Select(s => s.Uuid).ToList(),
        At = _clock.NowMs,
        Facts = facts ?? RadioFacts.Empty,
    };

    private static string LocationWifiPayload()
    {
        var msg = OpenDroneIdTests.LocationMsg(dir: 90, ew: false, speed: 40);
        return "00" + Convert.ToHexString(msg);
    }
}

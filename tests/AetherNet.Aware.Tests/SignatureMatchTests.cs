// SPDX-License-Identifier: MIT
// Ported from Fieldwatch app/src/test/java/app/fieldwatch/domain/SignatureExchangeTest.kt (cf6562d): the tests that
// match radios against the stock pack. The import/merge/overlay/export tests wait for signature packs over the mesh,
// and "create a signature from this radio" (suggestFleet) and DeviceExplain are not ported yet — see FIELDWATCH-PARITY.md.
// Copyright (c) 2026 Off Grid Pete LLC. See src/AetherNet.Aware/NOTICE.md.

using Xunit;

namespace AetherNet.Aware.Tests;

public class SignatureMatchTests
{
    private static readonly IReadOnlyList<Fleet> Stock = StockSignatures.Fleets;
    private static readonly string Zeros24 = string.Concat(Enumerable.Repeat("00", 24));

    [Fact]
    public void SonyPlusIBeaconLayoutIsSonyNotIBeacon()
    {
        const string ibeacon = "0215E2C56DB5DFFB48D2B060D0F5A71096E000010002C5";
        var tv = new Sighting
        {
            Key = "BLE:AA:BB:CC:DD:EE:02",
            Kind = RadioKind.Ble,
            Mac = "AA:BB:CC:DD:EE:02",
            Name = "Sony TV",
            Rssi = -45,
            RssiMin = -45,
            RssiMax = -45,
            ManufacturerId = 0x012D,
            ManufacturerDataHex = ibeacon,
            FirstSeen = 1,
            LastSeen = 1,
            HitCount = 1,
            Facts = new RadioFacts { MfgRecords = [new(0x012D, "0300"), new(0x004C, ibeacon)] },
        };
        var hits = new SignatureEngine().Match([tv], Stock)[tv.Key];
        Assert.Contains("fleet-sony", hits);
        Assert.DoesNotContain("fleet-ibeacon", hits);
    }

    [Fact]
    public void TeslaPhoneKeyIBeaconIsTeslaNotIBeacon()
    {
        var data = StockSignatures.TeslaIBeaconMfgPrefix + "00015D";
        var car = new Sighting
        {
            Key = "BLE:AA:BB:CC:DD:EE:01",
            Kind = RadioKind.Ble,
            Mac = "AA:BB:CC:DD:EE:01",
            Name = "S1a87a5a75f3df858C",
            Rssi = -40,
            RssiMin = -40,
            RssiMax = -40,
            ManufacturerId = 0x004C,
            ManufacturerDataHex = data,
            FirstSeen = 1,
            LastSeen = 1,
            HitCount = 1,
            Facts = new RadioFacts { MfgRecords = [new(0x004C, data)] },
        };
        var hits = new SignatureEngine().Match([car], Stock)[car.Key];
        Assert.Contains("fleet-tesla", hits);
        Assert.DoesNotContain("fleet-ibeacon", hits);
        Assert.DoesNotContain("fleet-target-atrius", hits);
    }

    [Fact]
    public void TargetAtriusIBeaconIsTargetAndIBeacon()
    {
        var data = StockSignatures.TargetAtriusIBeaconMfgPrefix + "6C42CC85C3";
        var tag = Ble("", 0x004C, data) with { ServiceUuids = ["0000B1BB-0000-1000-8000-00805F9B34FB"] };
        var hits = new SignatureEngine().Match([tag], Stock)[tag.Key];
        Assert.Contains("fleet-target-atrius", hits);
        Assert.Contains("fleet-ibeacon", hits);
        var fleet = Stock.First(f => f.Id == "fleet-target-atrius");
        Assert.Equal(SignatureClass.Beacon, fleet.Kind);
        Assert.Equal("Retail beacons", fleet.Kind.Label());
    }

    [Fact]
    public void TargetAtriusB1bbOnlyStillMatches()
    {
        var tag = Ble("") with { ServiceUuids = ["B1BB"] };
        Assert.Contains("fleet-target-atrius", new SignatureEngine().Match([tag], Stock)[tag.Key]);
    }

    [Fact]
    public void GenericIBeaconIsNotTargetAtrius()
    {
        var other = Ble("", 0x004C, "0215E2C56DB5DFFB48D2B060D0F5A71096E000010002C5");
        var hits = new SignatureEngine().Match([other], Stock)[other.Key];
        Assert.Contains("fleet-ibeacon", hits);
        Assert.DoesNotContain("fleet-target-atrius", hits);
    }

    [Fact]
    public void IphoneOfflineFindingIsAppleDeviceNotAirTag()
    {
        var phone = Ble("", 0x004C, "10AABBCCDDEE") with
        {
            Facts = new RadioFacts { MfgRecords = [new(0x004C, "10AABBCCDDEE"), new(0x004C, "12" + Zeros24)] },
        };
        var hits = new SignatureEngine().Match([phone], Stock)[phone.Key];
        Assert.Contains("fleet-apple-device", hits);
        Assert.DoesNotContain("fleet-airtag", hits);
    }

    [Fact]
    public void NamedIphoneWithOnlyFindMyIsAppleDeviceNotAirTag()
    {
        var phone = Ble("iPhone", 0x004C, "12" + Zeros24);
        var hits = new SignatureEngine().Match([phone], Stock)[phone.Key];
        Assert.Contains("fleet-apple-device", hits);
        Assert.DoesNotContain("fleet-airtag", hits);
    }

    [Fact]
    public void AirTagOfflineFindingStillAirTag()
    {
        var tag = Ble("", 0x004C, "12" + Zeros24);
        var hits = new SignatureEngine().Match([tag], Stock)[tag.Key];
        Assert.Contains("fleet-airtag", hits);
        Assert.DoesNotContain("fleet-apple-device", hits);
    }

    [Fact]
    public void AirTagNameKeepsAirTagEvenWithContinuity()
    {
        var tag = Ble("AirTag", 0x004C, "10AABBCCDDEE") with
        {
            Facts = new RadioFacts { MfgRecords = [new(0x004C, "10AABBCCDDEE"), new(0x004C, "12" + Zeros24)] },
        };
        var hits = new SignatureEngine().Match([tag], Stock)[tag.Key];
        Assert.Contains("fleet-airtag", hits);
        Assert.Contains("fleet-apple-device", hits);
    }

    [Fact]
    public void FindMyUuidKeepsAirTagEvenWithContinuity()
    {
        var accessory = Ble("", 0x004C, "10AABBCCDDEE") with
        {
            ServiceUuids = ["0000FD44-0000-1000-8000-00805F9B34FB"],
            Facts = new RadioFacts { MfgRecords = [new(0x004C, "10AABBCCDDEE"), new(0x004C, "12" + Zeros24)] },
        };
        var hits = new SignatureEngine().Match([accessory], Stock)[accessory.Key];
        Assert.Contains("fleet-airtag", hits);
        Assert.Contains("fleet-apple-device", hits);
    }

    [Fact]
    public void ContinuityPlusFindMyIsNotFindMyPayloadAlone()
    {
        var phone = Ble("", 0x004C, "10AABBCCDDEE") with
        {
            Facts = new RadioFacts { MfgRecords = [new(0x004C, "10AABBCCDDEE"), new(0x004C, "12" + Zeros24)] },
        };
        Assert.True(TrackerMatch.IsAppleContinuity(phone));
        Assert.False(TrackerMatch.IsFindMyPayload(phone));
        var tag = Ble("", 0x004C, "12" + Zeros24);
        Assert.False(TrackerMatch.IsAppleContinuity(tag));
        Assert.True(TrackerMatch.IsFindMyPayload(tag));
    }

    [Fact]
    public void TeslaMatchesVinPhoneKeyAndCybertruck()
    {
        var tesla = Stock.First(f => f.Id == "fleet-tesla");
        Assert.Contains(tesla.Rules, r => r.Kind == RuleKind.NameContains && r.Text.Equals("Cybertruck", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(tesla.Rules, r => r.Kind == RuleKind.NameGlob && r.Text == "S????????????????C");
        Assert.True(TextMatch.Glob("S1a87a5a75f3df858C", "S????????????????C"));
        Assert.False(TextMatch.Glob("Tesla", "S????????????????C"));
    }

    [Fact]
    public void ConsumerCamerasAreCameraClass()
    {
        var ids = Stock.ToDictionary(f => f.Id);
        foreach (var id in new[]
                 {
                     "fleet-gopro", "fleet-osmo", "fleet-insta360", "fleet-wyze", "fleet-ring", "fleet-arlo", "fleet-eufy",
                     "fleet-liteon-camera-radio", "fleet-nest", "fleet-tapo", "fleet-reolink",
                 })
        {
            Assert.Equal(SignatureClass.Camera, ids[id].Kind);
        }
        Assert.Equal(SignatureClass.Glasses, ids["fleet-meta-glasses"].Kind);
        Assert.Equal(SignatureClass.Isp, ids["fleet-unifi-ap"].Kind);
        Assert.Equal(SignatureClass.Isp, ids["fleet-unifi"].Kind);
        Assert.Equal(SignatureClass.Surveillance, ids["fleet-unifi-protect"].Kind);
        foreach (var id in new[]
                 {
                     "fleet-meraki", "fleet-cisco", "fleet-aruba", "fleet-ruckus", "fleet-fortinet", "fleet-mist",
                     "fleet-sophos", "fleet-extreme", "fleet-edgecore", "fleet-watchguard-ap", "fleet-mojo",
                 })
        {
            Assert.Equal(SignatureClass.Isp, ids[id].Kind);
        }
        Assert.Equal(SignatureClass.Surveillance, ids["fleet-flock-cameras"].Kind);
        Assert.Equal(SignatureClass.Surveillance, ids["fleet-fs-ext-battery"].Kind);
        string[] publicCameras =
        [
            "fleet-flock-cameras", "fleet-fs-ext-battery", "fleet-penguin", "fleet-pigvision", "fleet-genetec", "fleet-rekor",
            "fleet-vigilant", "fleet-verkada", "fleet-avigilon", "fleet-axis", "fleet-hikvision", "fleet-dahua",
            "fleet-hanwha-wisenet", "fleet-uniview", "fleet-rhombus",
        ];
        foreach (var id in publicCameras)
        {
            Assert.False(string.IsNullOrWhiteSpace(ids[id].AttentionNote), id);
        }
        Assert.True(string.IsNullOrWhiteSpace(ids["fleet-liteon-camera-radio"].AttentionNote));
        Assert.True(string.IsNullOrWhiteSpace(ids["fleet-unifi-protect"].AttentionNote));
        Assert.True(string.IsNullOrWhiteSpace(ids["fleet-salto"].AttentionNote));
        Assert.Equal("Access control", SignatureClass.Lock.Label());
        foreach (var id in new[] { "fleet-seos", "fleet-salto", "fleet-dormakaba", "fleet-paxton", "fleet-august" })
        {
            Assert.Equal(SignatureClass.Lock, ids[id].Kind);
        }
        Assert.Equal(SignatureClass.Surveillance, ids["fleet-bluetoad"].Kind);
        Assert.True(string.IsNullOrWhiteSpace(ids["fleet-bluetoad"].AttentionNote));
        Assert.Equal(SignatureClass.Surveillance, ids["fleet-bliptrack"].Kind);
        Assert.True(string.IsNullOrWhiteSpace(ids["fleet-bliptrack"].AttentionNote));
        Assert.Equal(SignatureClass.Mesh, ids["fleet-meshcore"].Kind);
        Assert.Equal(SignatureClass.Mesh, ids["fleet-gotenna"].Kind);
        Assert.Equal(SignatureClass.Mesh, ids["fleet-sensecap"].Kind);
        Assert.Equal(SignatureClass.Mesh, ids["fleet-rak-wisgate"].Kind);
        Assert.True(string.IsNullOrWhiteSpace(ids["fleet-meshcore"].AttentionNote));
        Assert.Equal(SignatureClass.Hacking, ids["fleet-ghostesp"].Kind);
        Assert.Equal(SignatureClass.Hacking, ids["fleet-bruce"].Kind);
        Assert.False(string.IsNullOrWhiteSpace(ids["fleet-ghostesp"].AttentionNote));
        Assert.False(string.IsNullOrWhiteSpace(ids["fleet-bruce"].AttentionNote));
        Assert.Contains(ids.Values, f => f.BuiltIn && f.Kind == SignatureClass.Surveillance && !string.IsNullOrWhiteSpace(f.AttentionNote));
        Assert.Contains(ids.Values, f => f.BuiltIn && f.Kind == SignatureClass.Drone);
        Assert.Equal(SignatureClass.LawEnforcement, ids["fleet-axon"].Kind);
        Assert.Equal(SignatureClass.LawEnforcement, ids["fleet-watchguard"].Kind);
        Assert.Equal(SignatureClass.LawEnforcement, ids["fleet-cradlepoint"].Kind);
        Assert.Equal(SignatureClass.LawEnforcement, ids["fleet-airlink"].Kind);
        Assert.Equal(SignatureClass.LawEnforcement, ids["fleet-compex"].Kind);
        Assert.Equal(SignatureClass.LawEnforcement, ids["fleet-novatel"].Kind);
        Assert.Equal(SignatureClass.LawEnforcement, ids["fleet-utility-inc"].Kind);
        Assert.Equal(SignatureClass.Vehicle, ids["fleet-tesla"].Kind);
        Assert.Equal(SignatureClass.Isp, ids["fleet-ruijie"].Kind);
        Assert.Equal(SignatureClass.Isp, ids["fleet-dwnet"].Kind);
        Assert.Equal(SignatureClass.Isp, ids["fleet-wavlink"].Kind);
        Assert.Equal(SignatureClass.Vehicle, ids["fleet-peoplenet"].Kind);
        Assert.Equal(SignatureClass.Vehicle, ids["fleet-uconnect"].Kind);
        Assert.Equal(SignatureClass.Vehicle, ids["fleet-carplay"].Kind);
        Assert.Equal(SignatureClass.Home, ids["fleet-roku"].Kind);
        Assert.Equal(SignatureClass.Isp, ids["fleet-franklin"].Kind);
        Assert.Equal(SignatureClass.Isp, ids["fleet-huawei"].Kind);
        Assert.Equal(SignatureClass.Isp, ids["fleet-plume"].Kind);
        Assert.Equal(SignatureClass.Phone, ids["fleet-phone-hotspot"].Kind);
        Assert.Equal(SignatureClass.Home, ids["fleet-samsung-appliance"].Kind);
        Assert.Equal(SignatureClass.Home, ids["fleet-ecowater"].Kind);
        Assert.DoesNotContain(Stock, f => f.Kind == SignatureClass.Bodyworn);
        Assert.Equal(SignatureClass.Thermostat, ids["fleet-nest-thermostat"].Kind);
        Assert.Equal(SignatureClass.Drone, ids["fleet-dji"].Kind);
        Assert.Equal(SignatureClass.Drone, ids["fleet-hoverair"].Kind);
        Assert.Equal(SignatureClass.Health, ids["fleet-honeywell-xenon-hc"].Kind);
        Assert.Equal(SignatureClass.Health, ids["fleet-omron"].Kind);
        Assert.Equal(SignatureClass.Health, ids["fleet-withings"].Kind);
        Assert.Equal(SignatureClass.Health, ids["fleet-dexcom"].Kind);
    }

    [Fact]
    public void BlueToadSpectraHitsNameAndIterisOuiButNotGenericWords()
    {
        var named = Of(RadioKind.Wifi, "AA:BB:CC:00:00:10", "BlueTOAD-12AB");
        var velocity = Of(RadioKind.Wifi, "AA:BB:CC:00:00:11", "Vantage Velocity 4");
        var oui = Of(RadioKind.Wifi, "00:14:7B:11:22:33", "Cabinet");
        var generic = Of(RadioKind.Wifi, "AA:BB:CC:00:00:12", "Spectra Audio");
        var vantage = Of(RadioKind.Wifi, "AA:BB:CC:00:00:13", "Vantage Point");
        var hits = new SignatureEngine().Match([named, velocity, oui, generic, vantage], Stock);
        Assert.Contains("fleet-bluetoad", hits[named.Key]);
        Assert.Contains("fleet-bluetoad", hits[velocity.Key]);
        Assert.Contains("fleet-bluetoad", hits[oui.Key]);
        Assert.Empty(hits[generic.Key]);
        Assert.Empty(hits[vantage.Key]);
    }

    [Fact]
    public void CatalogV70FamiliesHitUniqueIdsNotGenericWords()
    {
        var blip = Of(RadioKind.Wifi, "00:0E:A5:11:22:33", "Cabinet");
        var wisenet = Of(RadioKind.Wifi, "AA:BB:CC:00:00:20", "XNV-6080_0076_WISENET");
        var uniview = Of(RadioKind.Wifi, "48:EA:63:11:22:33", "Yard");
        var rhombus = Of(RadioKind.Wifi, "CC:47:BD:11:22:33", "Lobby");
        var mesh = Ble("MeshCore_A1B2", mac: "AA:BB:CC:DD:EE:10");
        var nordic = Ble("", mac: "AA:BB:CC:DD:EE:11") with { ServiceUuids = ["6E400001-B5A3-F393-E0A9-E50E24DCCA9E"] };
        var gotenna = Ble("", mac: "AA:BB:CC:DD:EE:12") with { ServiceUuids = ["1276aaee-df5e-11e6-bf01-fe55135034f3"] };
        var sense = Of(RadioKind.Wifi, "AA:BB:CC:00:00:21", "SenseCAP_A1B2C3");
        var rak = Of(RadioKind.Wifi, "AA:BB:CC:00:00:22", "RAK7268_A1B2");
        var ghost = Of(RadioKind.Wifi, "AA:BB:CC:00:00:23", "GhostNet");
        var bruce = Of(RadioKind.Wifi, "AA:BB:CC:00:00:24", "BruceNet");
        var genericGhost = Of(RadioKind.Wifi, "AA:BB:CC:00:00:25", "Ghost");
        var genericBruce = Of(RadioKind.Wifi, "AA:BB:CC:00:00:26", "Bruce");
        var hits = new SignatureEngine().Match(
            [blip, wisenet, uniview, rhombus, mesh, nordic, gotenna, sense, rak, ghost, bruce, genericGhost, genericBruce],
            Stock);
        Assert.Contains("fleet-bliptrack", hits[blip.Key]);
        Assert.Contains("fleet-hanwha-wisenet", hits[wisenet.Key]);
        Assert.Contains("fleet-uniview", hits[uniview.Key]);
        Assert.Contains("fleet-rhombus", hits[rhombus.Key]);
        Assert.Contains("fleet-meshcore", hits[mesh.Key]);
        Assert.DoesNotContain("fleet-meshcore", hits[nordic.Key]);
        Assert.Contains("fleet-gotenna", hits[gotenna.Key]);
        Assert.Contains("fleet-sensecap", hits[sense.Key]);
        Assert.Contains("fleet-rak-wisgate", hits[rak.Key]);
        Assert.Contains("fleet-ghostesp", hits[ghost.Key]);
        Assert.Contains("fleet-bruce", hits[bruce.Key]);
        Assert.DoesNotContain("fleet-ghostesp", hits[genericGhost.Key]);
        Assert.DoesNotContain("fleet-bruce", hits[genericBruce.Key]);
    }

    [Fact]
    public void HoneywellXenonHealthcareNameHits()
    {
        var scan = Ble("Xenon_CCB-U00-HC_SN_25139B493", mac: "C4:EF:DA:40:63:56");
        Assert.Contains("fleet-honeywell-xenon-hc", new SignatureEngine().Match([scan], Stock)[scan.Key]);
        var warehouse = Ble("Xenon_CCB-U00-G_SN_12345");
        Assert.DoesNotContain("fleet-honeywell-xenon-hc", new SignatureEngine().Match([warehouse], Stock)[warehouse.Key]);
    }

    [Fact]
    public void OmronHealthcareCompanyHits()
    {
        var cuff = Ble("BLESmart_0000025828FFB232E019", 0x020E);
        Assert.Contains("fleet-omron", new SignatureEngine().Match([cuff], Stock)[cuff.Key]);
    }

    [Fact]
    public void OsmoActionIsCameraNotDji()
    {
        var cam = Ble("OsmoAction5Pro", 0x08AA, "150000AABBCCDDEE03");
        var hits = new SignatureEngine().Match([cam], Stock)[cam.Key];
        Assert.Contains("fleet-osmo", hits);
        Assert.DoesNotContain("fleet-dji", hits);
    }

    [Fact]
    public void OsmoUnnamedModelIdIsCameraNotDji()
    {
        var cam = Ble("", 0x08AA, "1200");
        var hits = new SignatureEngine().Match([cam], Stock)[cam.Key];
        Assert.Contains("fleet-osmo", hits);
        Assert.DoesNotContain("fleet-dji", hits);
    }

    [Fact]
    public void DjiAircraftStaysDjiNotOsmo()
    {
        var drone = Ble("DJI Mini 4", 0x08AA, "7000");
        var hits = new SignatureEngine().Match([drone], Stock)[drone.Key];
        Assert.Contains("fleet-dji", hits);
        Assert.DoesNotContain("fleet-osmo", hits);
    }

    [Fact]
    public void OsmoMobileGimbalIsNotOsmoCamera()
    {
        var gimbal = Ble("Osmo Mobile 6");
        Assert.DoesNotContain("fleet-osmo", new SignatureEngine().Match([gimbal], Stock)[gimbal.Key]);
        Assert.False(TextMatch.Glob("Osmo Mobile 6", "OsmoAction*"));
        Assert.False(TextMatch.Glob("Osmo Mobile 6", "Osmo Pocket*"));
    }

    [Fact]
    public void Insta360NameAndCompanyIdAreCamera()
    {
        var named = Ble("X3 11WC1A");
        var company = Ble("", 0x10D7, "0102");
        var ap = Of(RadioKind.Wifi, "AA:BB:CC:11:22:33", "GO 3 AABB");
        var engine = new SignatureEngine();
        Assert.Contains("fleet-insta360", engine.Match([named], Stock)[named.Key]);
        Assert.Contains("fleet-insta360", engine.Match([company], Stock)[company.Key]);
        Assert.Contains("fleet-insta360", engine.Match([ap], Stock)[ap.Key]);
        Assert.False(TextMatch.Glob("X300", "X3 *"));
        Assert.False(TextMatch.Glob("Ace Hardware", "Ace Pro*"));
    }

    [Fact]
    public void OemVehicleCompanyIdsAreStock()
    {
        var ids = Stock.ToDictionary(f => f.Id);
        Assert.Equal(0x0723, ids["fleet-ford"].Rules.First(r => r.Kind == RuleKind.ManufacturerId).CompanyId);
        Assert.Equal(0x0915, ids["fleet-honda"].Rules.First(r => r.Kind == RuleKind.ManufacturerId).CompanyId);
        Assert.Equal(0x05EB, ids["fleet-bmw"].Rules.First(r => r.Kind == RuleKind.ManufacturerId).CompanyId);
    }

    [Fact]
    public void FastPairIsStockOnFe2c()
    {
        var row = Stock.First(f => f.Id == "fleet-fast-pair");
        Assert.Equal("Fast Pair", row.Name);
        Assert.True(row.Enabled);
        Assert.Contains(row.Rules, r => r.Kind == RuleKind.ServiceUuid && r.Text.Equals("FE2C", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void MatcherStillHitsAfterImportAndRespectsRadio()
    {
        // Upstream adds the custom row with SignatureExchange.merge (import, not ported yet); a new id with new rules
        // is simply appended there, so it is appended here.
        var custom = new Fleet
        {
            Id = "custom-pete-wifi",
            Name = "Pete Test AP",
            MatchAny = true,
            Rules = [new MatchRule { Kind = RuleKind.NameGlob, Text = "PeteTest*", Radio = RadioKind.Wifi }],
        };
        IReadOnlyList<Fleet> fleets = [.. Stock, custom];
        var engine = new SignatureEngine();
        var ciscoAp = Of(RadioKind.Wifi, "00:00:0C:11:22:33", "Campus");
        var unifiVirtual = Of(RadioKind.Wifi, "82:F9:2C:00:00:01", "Deep Learning", ["00:50:F2", "00:0F:AC", "AC:8B:A9"]);
        var peteAp = Of(RadioKind.Wifi, "AA:BB:CC:11:22:33", "PeteTest-lab");
        var peteBle = Of(RadioKind.Ble, "AA:BB:CC:11:22:33", "PeteTest-lab");
        var airTag = Of(RadioKind.Ble, "F4:5C:89:00:00:01", "AirTag");
        var ciscoOnBle = Of(RadioKind.Ble, "00:00:0C:11:22:33", "Campus");
        var hits = engine.Match([ciscoAp, unifiVirtual, peteAp, peteBle, airTag, ciscoOnBle], fleets);
        Assert.Contains("fleet-cisco", hits[ciscoAp.Key]);
        Assert.Contains("fleet-unifi-ap", hits[unifiVirtual.Key]);
        Assert.Contains("custom-pete-wifi", hits[peteAp.Key]);
        Assert.DoesNotContain("custom-pete-wifi", hits[peteBle.Key]);
        Assert.Contains("fleet-airtag", hits[airTag.Key]);
        Assert.DoesNotContain("fleet-cisco", hits[ciscoOnBle.Key]);

        Assert.Contains("fleet-cisco", engine.Match([ciscoAp], fleets)[ciscoAp.Key]);
    }

    [Fact]
    public void FieldUnmatchedWifiGetsUniqueApAndVehicleRows()
    {
        var ruijie = Of(RadioKind.Wifi, "F0:74:8D:11:22:33", "MAB Family");
        var reyee = Of(RadioKind.Wifi, "AA:BB:CC:11:22:33", "@Reyee-s3F2D");
        var dwnet = Of(RadioKind.Wifi, "2C:67:BE:11:22:33", "MorrellHouse");
        var wavlink = Of(RadioKind.Wifi, "80:3F:5D:11:22:33", "WAVLINK-N");
        var glinet = Of(RadioKind.Wifi, "94:83:C4:11:22:33", "Academy - 6805");
        var keepTruckin = Of(RadioKind.Wifi, "00:25:CA:11:22:33", "KeepTruckin Hotspot - HB492085");
        var pnet = Of(RadioKind.Wifi, "98:5D:46:11:22:33", "PNet20267513");
        var toyota = Of(RadioKind.Wifi, "E0:2D:F0:11:22:33", "TOYOTA Sienna_3dabe4e96405");
        var carPlay = Of(RadioKind.Wifi, "FC:98:16:11:22:33", "BMW 51919 CarPlay");
        var uconnect = Of(RadioKind.Wifi, "AA:BB:CC:00:00:01", "Uconnect-4326653b");
        var hpPrint = Of(RadioKind.Wifi, "F0:92:1C:11:22:33", "HP-Print-34-ENVY 4500 series");
        var house = Of(RadioKind.Wifi, "9C:4F:5F:11:22:33", "Jameson");
        var hits = new SignatureEngine().Match(
            [ruijie, reyee, dwnet, wavlink, glinet, keepTruckin, pnet, toyota, carPlay, uconnect, hpPrint, house],
            Stock);
        Assert.Contains("fleet-ruijie", hits[ruijie.Key]);
        Assert.Contains("fleet-ruijie", hits[reyee.Key]);
        Assert.Contains("fleet-dwnet", hits[dwnet.Key]);
        Assert.Contains("fleet-wavlink", hits[wavlink.Key]);
        Assert.Contains("fleet-glinet", hits[glinet.Key]);
        Assert.Contains("fleet-motive", hits[keepTruckin.Key]);
        Assert.Contains("fleet-peoplenet", hits[pnet.Key]);
        Assert.Contains("fleet-toyota", hits[toyota.Key]);
        Assert.Contains("fleet-carplay", hits[carPlay.Key]);
        Assert.Contains("fleet-uconnect", hits[uconnect.Key]);
        Assert.Contains("fleet-hp", hits[hpPrint.Key]);
        Assert.Empty(hits[house.Key]);
    }

    [Fact]
    public void WifiOui24UniversalClearsLocalBitOnly()
    {
        Assert.Equal("00095B", MacUtil.WifiOui24Universal("02:09:5B:11:22:33"));
        Assert.Equal("1C3BF3", MacUtil.WifiOui24Universal("1E:3B:F3:AA:BB:CC"));
        Assert.Null(MacUtil.WifiOui24Universal("00:09:5B:11:22:33"));
        Assert.Null(MacUtil.WifiOui24Universal("03:09:5B:11:22:33"));
        Assert.True(MacUtil.IsRandomized("02:09:5B:11:22:33"));
    }

    [Fact]
    public void VirtualBssidHitsCatalogOuiNotMacPinOrBle()
    {
        var netgearVirtual = Of(RadioKind.Wifi, "02:09:5B:11:22:33", "Jameson");
        var tplinkVirtual = Of(RadioKind.Wifi, "1E:3B:F3:AA:BB:CC", "SmithWifi");
        var netgearBle = Of(RadioKind.Ble, "02:09:5B:11:22:33", "Jameson");
        var burned = Of(RadioKind.Wifi, "00:09:5B:11:22:33", "Jameson");
        var pinFleet = new Fleet
        {
            Id = "custom-pin",
            Name = "Pinned NETGEAR radio",
            MatchAny = true,
            Rules = [new MatchRule { Kind = RuleKind.MacPrefix, Text = "00:09:5B:11:22:33", Radio = RadioKind.Wifi }],
        };
        var hits = new SignatureEngine().Match([netgearVirtual, tplinkVirtual, netgearBle, burned], [.. Stock, pinFleet]);
        Assert.Contains("fleet-netgear", hits[netgearVirtual.Key]);
        Assert.Contains("fleet-tplink", hits[tplinkVirtual.Key]);
        Assert.DoesNotContain("fleet-netgear", hits[netgearBle.Key]);
        Assert.Contains("fleet-netgear", hits[burned.Key]);
        Assert.DoesNotContain("custom-pin", hits[netgearVirtual.Key]);
        Assert.Contains("custom-pin", hits[burned.Key]);
        Assert.True(netgearVirtual.Randomized);
    }

    [Fact]
    public void PhoneHotspotHuaweiAndPlumeCatchFactoryWifi()
    {
        var androidAp = Of(RadioKind.Wifi, "C2:11:22:33:44:55", "AndroidAP_1234");
        var galaxy = Of(RadioKind.Wifi, "C2:11:22:33:44:56", "Galaxy A54 5G");
        var galaxyDash = Of(RadioKind.Wifi, "C2:11:22:33:44:57", "Galaxy-A54-XXXX");
        var pixel = Of(RadioKind.Wifi, "C2:11:22:33:44:58", "Pixel 8");
        var iphone = Of(RadioKind.Wifi, "C2:11:22:33:44:59", "Pete's iPhone");
        var customHotspot = Of(RadioKind.Wifi, "C2:11:22:33:44:5A", "PeteHotspot");
        var galaxyBle = Of(RadioKind.Ble, "C2:11:22:33:44:5B", "Galaxy A54 5G");
        var huaweiName = Of(RadioKind.Wifi, "C2:AA:BB:CC:DD:01", "HUAWEI-B535");
        var huaweiOui = Of(RadioKind.Wifi, "00:18:82:11:22:33", "Jameson");
        var plumeOui = Of(RadioKind.Wifi, "60:B4:F7:11:22:33", "SmithWifi");
        var superPod = Of(RadioKind.Wifi, "C2:AA:BB:CC:DD:02", "SuperPod-setup");
        var hits = new SignatureEngine().Match(
            [androidAp, galaxy, galaxyDash, pixel, iphone, customHotspot, galaxyBle, huaweiName, huaweiOui, plumeOui, superPod],
            Stock);
        Assert.Contains("fleet-phone-hotspot", hits[androidAp.Key]);
        Assert.Contains("fleet-phone-hotspot", hits[galaxy.Key]);
        Assert.Contains("fleet-phone-hotspot", hits[galaxyDash.Key]);
        Assert.Contains("fleet-phone-hotspot", hits[pixel.Key]);
        Assert.Contains("fleet-apple-device", hits[iphone.Key]);
        Assert.DoesNotContain("fleet-phone-hotspot", hits[iphone.Key]);
        Assert.Empty(hits[customHotspot.Key]);
        Assert.DoesNotContain("fleet-phone-hotspot", hits[galaxyBle.Key]);
        Assert.Contains("fleet-huawei", hits[huaweiName.Key]);
        Assert.Contains("fleet-huawei", hits[huaweiOui.Key]);
        Assert.Contains("fleet-plume", hits[plumeOui.Key]);
        Assert.Contains("fleet-plume", hits[superPod.Key]);
    }

    [Fact]
    public void RingSsidOnUgsiOuiIsRingNotFlock()
    {
        var ring = Of(RadioKind.Wifi, "E0:4F:43:DC:6C:94", "Ring-dc6c94");
        var module = Of(RadioKind.Wifi, "70:C9:4E:11:22:33", "Home");
        var pole = Of(RadioKind.Wifi, "B4:1E:52:00:00:01", "Flock-ABCDEF");
        var hits = new SignatureEngine().Match([ring, module, pole], Stock);
        Assert.Contains("fleet-ring", hits[ring.Key]);
        Assert.DoesNotContain("fleet-flock-cameras", hits[ring.Key]);
        Assert.DoesNotContain("fleet-liteon-camera-radio", hits[ring.Key]);
        Assert.Contains("fleet-liteon-camera-radio", hits[module.Key]);
        Assert.DoesNotContain("fleet-flock-cameras", hits[module.Key]);
        Assert.Contains("fleet-flock-cameras", hits[pole.Key]);
        var ringDevice = ring with { FleetIds = hits[ring.Key] };
        var moduleDevice = module with { FleetIds = hits[module.Key] };
        var poleDevice = pole with { FleetIds = hits[pole.Key] };
        Assert.DoesNotContain(ringDevice.AttentionNotes(Stock), a => a.Name.Contains("Flock"));
        Assert.Empty(moduleDevice.AttentionNotes(Stock));
        Assert.Contains(poleDevice.AttentionNotes(Stock), a => a.Name == "Flock Safety Cameras");
    }

    [Fact]
    public void XuntongMfgHitsPenguinNotRaven()
    {
        var pack = Ble("", 0x09C8, mac: "AA:BB:CC:DD:EE:08");
        var ravenNamed = Ble("RAVEN-1", mac: "AA:BB:CC:DD:EE:09");
        var hits = new SignatureEngine().Match([pack, ravenNamed], Stock);
        Assert.Contains("fleet-penguin", hits[pack.Key]);
        Assert.DoesNotContain("fleet-raven", hits[pack.Key]);
        Assert.Contains("fleet-raven", hits[ravenNamed.Key]);
        Assert.DoesNotContain("fleet-penguin", hits[ravenNamed.Key]);
    }

    [Fact]
    public void AxonBwcdeviceMatchesServiceDataNotName()
    {
        var tagged = Ble("", mac: "AA:BB:CC:11:22:33") with
        {
            Facts = new RadioFacts { ServiceData = [new("FE6B", "41584A414E5553425743444556494345")] },
        };
        var reversed = Ble("", mac: "AA:BB:CC:11:22:34") with
        {
            Facts = new RadioFacts { ServiceData = [new("FC81", "454349564544435742")] },
        };
        var namedOnly = Ble("BWCDEVICE", mac: "AA:BB:CC:11:22:35");
        var hits = new SignatureEngine().Match([tagged, reversed, namedOnly], Stock);
        Assert.Contains("fleet-axon", hits[tagged.Key]);
        Assert.Contains("fleet-axon", hits[reversed.Key]);
        Assert.DoesNotContain("fleet-axon", hits[namedOnly.Key]);
    }

    [Fact]
    public void DultFcb2ServiceDataHitsNotUuidList()
    {
        var tagged = Ble("", mac: "AA:BB:CC:11:22:40") with { Facts = new RadioFacts { ServiceData = [new("FCB2", "0100")] } };
        var uuidOnly = BleUuid("AA:BB:CC:11:22:41", "", "FCB2");
        var hits = new SignatureEngine().Match([tagged, uuidOnly], Stock);
        Assert.Contains("fleet-dult", hits[tagged.Key]);
        Assert.DoesNotContain("fleet-dult", hits[uuidOnly.Key]);
        Assert.True(string.IsNullOrWhiteSpace(Stock.Single(f => f.Id == "fleet-dult").AttentionNote));
    }

    [Fact]
    public void RavenWifiDirectSsidHitsRavenOnly()
    {
        var raven = Of(RadioKind.Wifi, "00:0A:F5:86:56:DD", "DIRECT-rR-Raven-607");
        var genericDirect = Of(RadioKind.Wifi, "02:11:22:33:44:55", "DIRECT-xy-LivingRoom");
        var hits = new SignatureEngine().Match([raven, genericDirect], Stock);
        Assert.Contains("fleet-raven", hits[raven.Key]);
        Assert.DoesNotContain("fleet-unknown", hits[raven.Key]);
        Assert.DoesNotContain("fleet-unknown", hits[genericDirect.Key]);
        Assert.Empty(hits[genericDirect.Key]);
    }

    [Fact]
    public void DigitalAllyOuiAndFirstVuNameHit()
    {
        var oui = Of(RadioKind.Ble, "00:23:BD:11:22:33");
        var named = Of(RadioKind.Wifi, "02:11:22:33:44:55", "FirstVu-PRO");
        var hits = new SignatureEngine().Match([oui, named], Stock);
        Assert.Contains("fleet-digital-ally", hits[oui.Key]);
        Assert.Contains("fleet-digital-ally", hits[named.Key]);
    }

    [Fact]
    public void PendantUuidsAndNamesHit()
    {
        var limitless = BleUuid("AA:BB:CC:DD:EE:01", "Limitless", "632DE001-604C-446B-A80F-7963E950F3FB");
        var bee = BleUuid("AA:BB:CC:DD:EE:02", "Bee Pioneer", "03D5D5C4-A86C-11EE-9D89-8F2089A49E7E");
        var friend = BleUuid("AA:BB:CC:DD:EE:03", "", "1A3FD0E7-B1F3-AC9E-2E49-B647B2C4F8DA");
        var omi = BleUuid("AA:BB:CC:DD:EE:04", "Omi", "");
        var naomi = BleUuid("AA:BB:CC:DD:EE:05", "Naomi", "");
        var arduino = BleUuid("AA:BB:CC:DD:EE:06", "ESP32", "19B10000-E8F2-537E-4F6C-D104768A1214");
        var hits = new SignatureEngine().Match([limitless, bee, friend, omi, naomi, arduino], Stock);
        Assert.Contains("fleet-limitless", hits[limitless.Key]);
        Assert.Contains("fleet-bee", hits[bee.Key]);
        Assert.Contains("fleet-friend-pendant", hits[friend.Key]);
        Assert.Contains("fleet-omi", hits[omi.Key]);
        Assert.DoesNotContain("fleet-omi", hits[naomi.Key]);
        Assert.DoesNotContain("fleet-omi", hits[arduino.Key]);
    }

    [Fact]
    public void CarlinkAdapterHitsGlobAndPanasonicOuiNotAlpsAlone()
    {
        var named = Of(RadioKind.Wifi, "CC:57:63:11:22:33", "CARLINK-7D0AF4");
        var randNamed = Of(RadioKind.Wifi, "C2:11:22:33:44:55", "CARLINK-B6789A");
        var panasonicRenamed = Of(RadioKind.Wifi, "CC:57:63:AA:BB:CC", "MyCar");
        var zhuolian = Of(RadioKind.Wifi, "68:8F:C9:11:22:33", "CARLINK-ABCDEF");
        var toyotaAlps = Of(RadioKind.Wifi, "E0:2D:F0:11:22:33", "TOYOTA Sienna_3dabe4e96405");
        var alpsHouse = Of(RadioKind.Wifi, "E0:2D:F0:44:55:66", "Jameson");
        var hits = new SignatureEngine().Match([named, randNamed, panasonicRenamed, zhuolian, toyotaAlps, alpsHouse], Stock);
        Assert.Contains("fleet-carlink", hits[named.Key]);
        Assert.Contains("fleet-carlink", hits[randNamed.Key]);
        Assert.Contains("fleet-carlink", hits[panasonicRenamed.Key]);
        Assert.Contains("fleet-carlink", hits[zhuolian.Key]);
        Assert.DoesNotContain("fleet-carlink", hits[toyotaAlps.Key]);
        Assert.Contains("fleet-toyota", hits[toyotaAlps.Key]);
        Assert.DoesNotContain("fleet-carlink", hits[alpsHouse.Key]);
    }

    [Fact]
    public void RokuHiddenWifiDirectHitsVendorIeNotWps()
    {
        var hidden = Of(RadioKind.Wifi, "C2:D2:F3:E1:1D:02", "", ["00:50:F2", "50:6F:9A", "C8:3A:6B"]);
        var wpsOnly = Of(RadioKind.Wifi, "C2:D2:F3:E1:1D:03", "", ["00:50:F2", "50:6F:9A"]);
        var factory = Of(RadioKind.Wifi, "AA:BB:CC:00:00:02", "DIRECT-roku-abcd");
        var burned = Of(RadioKind.Wifi, "C8:3A:6B:11:22:33");
        var onBle = Of(RadioKind.Ble, "C8:3A:6B:11:22:33");
        var hits = new SignatureEngine().Match([hidden, wpsOnly, factory, burned, onBle], Stock);
        Assert.Contains("fleet-roku", hits[hidden.Key]);
        Assert.DoesNotContain("fleet-roku", hits[wpsOnly.Key]);
        Assert.Contains("fleet-roku", hits[factory.Key]);
        Assert.Contains("fleet-roku", hits[burned.Key]);
        Assert.DoesNotContain("fleet-roku", hits[onBle.Key]);
    }

    [Fact]
    public void FranklinRg3100HitsOuiAndFactorySsidNotQualcommIe()
    {
        var burned = Of(RadioKind.Wifi, "50:FB:FF:02:CA:C8", "RG3100-8434 guest", ["00:50:F2", "8C:FD:F0"]);
        var renamed = Of(RadioKind.Wifi, "50:FB:FF:11:22:33", "HouseNet");
        var factoryOnly = Of(RadioKind.Wifi, "AA:BB:CC:00:00:03", "RG3100-8434 guest");
        var qualcommOnly = Of(RadioKind.Wifi, "AA:BB:CC:00:00:04", "HouseNet", ["8C:FD:F0"]);
        var electric = Of(RadioKind.Wifi, "00:12:27:11:22:33", "Pump");
        var hits = new SignatureEngine().Match([burned, renamed, factoryOnly, qualcommOnly, electric], Stock);
        Assert.Contains("fleet-franklin", hits[burned.Key]);
        Assert.Contains("fleet-franklin", hits[renamed.Key]);
        Assert.Contains("fleet-franklin", hits[factoryOnly.Key]);
        Assert.DoesNotContain("fleet-franklin", hits[qualcommOnly.Key]);
        Assert.DoesNotContain("fleet-franklin", hits[electric.Key]);
    }

    [Fact]
    public void SamsungApplianceAndEcoWaterFactorySsids()
    {
        var fridge = Of(RadioKind.Wifi, "1C:E8:9E:01:8F:92", "[fridge]_E30AJT5133207Z");
        var oven = Of(RadioKind.Wifi, "34:FC:99:11:22:33", "[oven] Samsung");
        var h2o = Of(RadioKind.Wifi, "04:7B:CB:D1:14:00", "H2O-047bcbd11400");
        var house = Of(RadioKind.Wifi, "CE:BE:8F:24:DC:E3", "IonCannon");
        var guest = Of(RadioKind.Wifi, "A2:53:22:C6:84:42", "LOB_Guest");
        var h2oBar = Of(RadioKind.Wifi, "AA:BB:CC:00:00:05", "H2O-Lounge");
        var hits = new SignatureEngine().Match([fridge, oven, h2o, house, guest, h2oBar], Stock);
        Assert.Contains("fleet-samsung-appliance", hits[fridge.Key]);
        Assert.Contains("fleet-samsung-appliance", hits[oven.Key]);
        Assert.Contains("fleet-ecowater", hits[h2o.Key]);
        Assert.Empty(hits[house.Key]);
        Assert.Empty(hits[guest.Key]);
        Assert.Empty(hits[h2oBar.Key]);
    }

    [Fact]
    public void MerakiApDoesNotDualChipCiscoVendorIe()
    {
        var meraki = Of(RadioKind.Wifi, "00:18:0A:11:22:33", "Campus", ["00:50:F2", "00:0F:AC", "00:00:0C"]);
        var cisco = Of(RadioKind.Wifi, "00:00:0C:11:22:33", "Campus", ["00:00:0C"]);
        var hits = new SignatureEngine().Match([meraki, cisco], Stock);
        Assert.Contains("fleet-meraki", hits[meraki.Key]);
        Assert.DoesNotContain("fleet-cisco", hits[meraki.Key]);
        Assert.Contains("fleet-cisco", hits[cisco.Key]);
        Assert.DoesNotContain("fleet-meraki", hits[cisco.Key]);
    }

    [Fact]
    public void Custom128BitUuidDoesNotAliasTo0000()
    {
        const string epson = "802A0000-4EF4-4E59-B573-2BED4A4AC159";
        const string sensorPush = "EF090000-11D6-42BA-93B8-9DD7EC090AA9";
        Assert.DoesNotContain(Uuids.Aliases(epson), a => Uuids.Aliases(sensorPush).Contains(a));
        Assert.DoesNotContain("0000", Uuids.Aliases(epson));
        Assert.DoesNotContain("0000", Uuids.Aliases(sensorPush));
        Assert.Contains("FD44", Uuids.Aliases("FD44"));
        Assert.Contains("FD44", Uuids.Aliases("0000FD44-0000-1000-8000-00805F9B34FB"));
    }

    [Fact]
    public void EpsonEt4800BleIsNotSensorPush()
    {
        var radio = Of(RadioKind.Ble, "5A:05:D9:2A:CE:B0", "ET-4800 Series") with
        {
            ServiceUuids = ["802A0000-4EF4-4E59-B573-2BED4A4AC159"],
        };
        Assert.DoesNotContain("fleet-sensorpush", new SignatureEngine().Match([radio], Stock)[radio.Key]);
    }

    private static Sighting Ble(string name, int? manufacturerId = null, string manufacturerDataHex = "", string mac = "AA:BB:CC:DD:EE:01") =>
        Of(RadioKind.Ble, mac, name) with
        {
            ManufacturerId = manufacturerId,
            ManufacturerDataHex = manufacturerDataHex,
            Facts = manufacturerId is { } id
                ? new RadioFacts { MfgRecords = [new MfgRecord(id, manufacturerDataHex)] }
                : new RadioFacts(),
        };

    private static Sighting BleUuid(string mac, string name, string uuid) => new()
    {
        Key = "BLE:" + mac,
        Kind = RadioKind.Ble,
        Mac = mac,
        Name = name,
        Rssi = -40,
        RssiMin = -40,
        RssiMax = -40,
        Randomized = true,
        ServiceUuids = string.IsNullOrWhiteSpace(uuid) ? [] : [uuid],
        FirstSeen = 1,
        LastSeen = 1,
        HitCount = 1,
    };

    private static Sighting Of(RadioKind kind, string mac, string name = "", IReadOnlyList<string>? ies = null) => new()
    {
        Key = (kind == RadioKind.Wifi ? "WIFI:" : "BLE:") + mac,
        Kind = kind,
        Mac = mac,
        Name = name,
        Rssi = -40,
        RssiMin = -40,
        RssiMax = -40,
        Channel = 1,
        FrequencyMhz = 2412,
        Randomized = kind == RadioKind.Ble || (Convert.ToInt32(mac[..2], 16) & 0x02) != 0,
        FirstSeen = 1,
        LastSeen = 1,
        HitCount = 1,
        VendorIeOuis = ies ?? [],
    };
}

// SPDX-License-Identifier: MIT
// Ported from Fieldwatch app/src/test/java/app/fieldwatch/domain/DefaultCatalogTest.kt (cf6562d).
// Fieldwatch builds its stock rows in DefaultCatalog.kt and writes them to dist/fieldwatch-signatures-v2.json;
// StockCatalogFileTest proves the two are the same. Here the pack is the stock catalog, so these run against it.
// Not ported: the default-watchlist assertions (no watchlist here) and the DeviceDetailText dump (UI text).
// Copyright (c) 2026 Off Grid Pete LLC. See src/AetherNet.Aware/NOTICE.md.

using System.Text.RegularExpressions;
using Xunit;

namespace AetherNet.Aware.Tests;

public class StockCatalogTests
{
    private static readonly IReadOnlyList<Fleet> Stock = StockSignatures.Fleets;

    [Fact]
    public void StockCatalogDoesNotShipUnknownSignature()
    {
        Assert.DoesNotContain(Stock, f => f.Id == "fleet-unknown");
        Assert.DoesNotContain(Stock, f => f.Name.Equals("Unknown Signature", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void StockNotesDoNotSayShipsOn()
    {
        foreach (var fleet in Stock)
        {
            Assert.False(
                fleet.Notes.Contains("Ships on", StringComparison.OrdinalIgnoreCase),
                $"{fleet.Name} notes still mention Ships on: {fleet.Notes}");
        }
    }

    [Fact]
    public void IbeaconNoteKeepsMallAdvice()
    {
        var note = Stock.Single(f => f.Id == "fleet-ibeacon").Notes;
        Assert.Contains("Mute in a dense mall.", note);
        Assert.DoesNotContain("Ships on", note);
        Assert.Contains("proximity beacon", note, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void StockNotesExplainTheFamilyNotTheMatcher()
    {
        foreach (var fleet in Stock)
        {
            Assert.False(string.IsNullOrWhiteSpace(fleet.Notes), $"{fleet.Name} has empty notes");
            Assert.False(
                Regex.IsMatch(fleet.Notes, "0x[0-9A-Fa-f]{2,}"),
                $"{fleet.Name} notes still look like matcher copy: {fleet.Notes}");
        }
        var oura = Stock.Single(f => f.Id == "fleet-oura").Notes;
        Assert.Contains("ring", oura, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void FlockAndFsExtDropEspressifAndSilabsOuis()
    {
        var flock = Stock.Single(f => f.Id == "fleet-flock-cameras");
        var fs = Stock.Single(f => f.Id == "fleet-fs-ext-battery");
        var flockOuis = Ouis(flock);
        var fsOuis = Ouis(fs);
        Assert.DoesNotContain("A4:CF:12", flockOuis);
        Assert.DoesNotContain("3C:71:BF", flockOuis);
        Assert.DoesNotContain("E0:4F:43", flockOuis);
        Assert.DoesNotContain("70:C9:4E", flockOuis);
        Assert.Contains("B4:1E:52", flockOuis);
        var lite = Stock.Single(f => f.Id == "fleet-liteon-camera-radio");
        var liteOuis = Ouis(lite);
        Assert.Contains("70:C9:4E", liteOuis);
        Assert.DoesNotContain("E0:4F:43", liteOuis);
        Assert.DoesNotContain("B4:1E:52", liteOuis);
        Assert.True(string.IsNullOrWhiteSpace(lite.AttentionNote));
        Assert.Equal(SignatureClass.Camera, lite.Kind);
        Assert.DoesNotContain("90:35:EA", fsOuis);
        Assert.DoesNotContain("58:8E:81", fsOuis);
        Assert.DoesNotContain("EC:1B:BD", fsOuis);
    }

    [Fact]
    public void CatalogV77AddsGlassesAndAxonUuids()
    {
        var axon = Stock.Single(f => f.Id == "fleet-axon");
        var meta = Stock.Single(f => f.Id == "fleet-meta-glasses");
        var snap = Stock.Single(f => f.Id == "fleet-snap-spectacles");
        var vuzix = Stock.Single(f => f.Id == "fleet-vuzix");
        var rid = Stock.Single(f => f.Id == "fleet-remote-id");
        Assert.Contains(axon.Rules, r => r.Kind == RuleKind.ServiceUuid && r.Text.Equals("FC81", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(axon.Rules, r => r.Kind == RuleKind.ManufacturerId && r.CompanyId == 0x034D);
        Assert.Contains(meta.Rules, r => r.Kind == RuleKind.ServiceUuid && r.Text.Equals("FEB7", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(snap.Rules, r => r.Kind == RuleKind.ServiceUuid && r.Text.Equals("FE45", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(vuzix.Rules, r => r.Kind == RuleKind.ManufacturerId && r.CompanyId == 0x060C);
        Assert.Contains(rid.Rules, r => r.Kind == RuleKind.VendorIeOui && r.Text.Equals("FA:0B:BC", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void FindHubMatchesSeparatedFrameNotEddystoneUid()
    {
        var engine = new SignatureEngine();
        var eid = string.Concat(Enumerable.Repeat("11", 20));
        var hub = Ble(serviceUuids: ["FEAA"]) with
        {
            Facts = new RadioFacts { ServiceData = [new("FEAA", "41" + eid + "00")] },
        };
        var uid = Ble(serviceUuids: ["FEAA"]) with
        {
            Facts = new RadioFacts
            {
                ServiceData = [new("FEAA", "00C5" + string.Concat(Enumerable.Repeat("11", 10)) + string.Concat(Enumerable.Repeat("22", 6)))],
            },
        };
        Assert.Contains("fleet-find-hub", engine.Match([hub], Stock)[hub.Key]);
        Assert.DoesNotContain("fleet-find-hub", engine.Match([uid], Stock)[uid.Key]);
    }

    [Fact]
    public void DultMatchesFcb2ServiceDataNotUuidList()
    {
        var engine = new SignatureEngine();
        var tagged = Ble(serviceUuids: ["FCB2"]) with { Facts = new RadioFacts { ServiceData = [new("FCB2", "0100")] } };
        var uuidOnly = Ble(serviceUuids: ["FCB2"]);
        var hub = Ble(serviceUuids: ["FEAA"]) with
        {
            Facts = new RadioFacts { ServiceData = [new("FEAA", "41" + string.Concat(Enumerable.Repeat("11", 20)))] },
        };
        Assert.Contains("fleet-dult", engine.Match([tagged], Stock)[tagged.Key]);
        Assert.DoesNotContain("fleet-dult", engine.Match([uuidOnly], Stock)[uuidOnly.Key]);
        Assert.DoesNotContain("fleet-dult", engine.Match([hub], Stock)[hub.Key]);
        Assert.Contains("fleet-find-hub", engine.Match([hub], Stock)[hub.Key]);
        var fleet = Stock.Single(f => f.Id == "fleet-dult");
        Assert.Equal(SignatureClass.Finder, fleet.Kind);
        Assert.True(string.IsNullOrWhiteSpace(fleet.AttentionNote));
    }

    [Fact]
    public void AftermarketTpmsMatchesPrefixAndNameNotBareNokia()
    {
        var engine = new SignatureEngine();
        var cap = Ble(name: "TPMS1_A1B2", manufacturerId: 0x0001, manufacturerDataHex: "80EACA108A78E36D0000E60A00005B00", serviceUuids: ["FBB0"]);
        var hits = engine.Match([cap], Stock)[cap.Key];
        Assert.Contains("fleet-tpms-ble", hits);
        Assert.DoesNotContain("fleet-tesla-tstpms", hits);

        var nokia = Ble(manufacturerId: 0x0001, manufacturerDataHex: "010103215D64");
        Assert.DoesNotContain("fleet-tpms-ble", engine.Match([nokia], Stock)[nokia.Key]);

        var teslaTire = Ble(name: "tsTPMS");
        var teslaHits = engine.Match([teslaTire], Stock)[teslaTire.Key];
        Assert.Contains("fleet-tesla-tstpms", teslaHits);
        Assert.DoesNotContain("fleet-tpms-ble", teslaHits);
    }

    [Fact]
    public void SytpmsMatchesBrNameAndPressureUuidNotBrother()
    {
        var engine = new SignatureEngine();
        var sensor = Ble(name: "BR", serviceUuids: ["27A5"]);
        Assert.Contains("fleet-sytpms", engine.Match([sensor], Stock)[sensor.Key]);

        var printer = Ble(name: "Brother Printer");
        Assert.DoesNotContain("fleet-sytpms", engine.Match([printer], Stock)[printer.Key]);
    }

    [Fact]
    public void FoboMatchesServiceUuid()
    {
        var sensor = Ble(serviceUuids: ["00EE"]);
        Assert.Contains("fleet-fobo", new SignatureEngine().Match([sensor], Stock)[sensor.Key]);
    }

    [Fact]
    public void AftermarketTpmsDoesNotUseBareNokiaCompanyId()
    {
        var fleet = Stock.Single(f => f.Id == "fleet-tpms-ble");
        Assert.DoesNotContain(fleet.Rules, r => r.Kind == RuleKind.ManufacturerId && r.CompanyId == 0x0001);
        Assert.Contains(fleet.Rules, r => r.Kind == RuleKind.ManufacturerData && r.CompanyId == 0x0001);
        Assert.NotNull(fleet.Decode);
    }

    [Fact]
    public void SignatureNotesAreSeparateFromExtraAttention()
    {
        var oura = Stock.Single(f => f.Id == "fleet-oura");
        var axon = Stock.Single(f => f.Id == "fleet-axon");
        var device = Ble() with { FleetIds = [oura.Id, axon.Id] };
        var notes = device.SignatureNotes([oura, axon]);
        var attention = device.AttentionNotes([oura, axon]);
        Assert.False(string.IsNullOrWhiteSpace(oura.Notes));
        Assert.Contains(notes, n => n.Name == oura.Name && n.Note == oura.Notes);
        Assert.Contains(notes, n => n.Name == axon.Name && n.Note == axon.Notes);
        Assert.Equal([axon.Name], attention.Select(a => a.Name));
        Assert.DoesNotContain(attention, a => a.Name == oura.Name);
    }

    [Fact]
    public void Catalog89RayNeoRequiresNameAndTclEvenOrsCompanyIdLiteOnStaysQuiet()
    {
        var engine = new SignatureEngine();
        var ray = Stock.Single(f => f.Id == "fleet-rayneo");
        var even = Stock.Single(f => f.Id == "fleet-even-g1");
        var lite = Stock.Single(f => f.Id == "fleet-liteon-camera-radio");
        var flock = Stock.Single(f => f.Id == "fleet-flock-cameras");

        Assert.False(ray.MatchAny);
        Assert.Equal(["fleet-rayneo"], Stock.Where(f => !f.MatchAny).Select(f => f.Id));
        Assert.Equal(SignatureClass.Glasses, ray.Kind);
        Assert.False(string.IsNullOrWhiteSpace(ray.AttentionNote));
        Assert.Equal(0x0BC6, ray.Rules.Single(r => r.Kind == RuleKind.ManufacturerId).CompanyId);
        Assert.Equal("RayNeo*", ray.Rules.Single(r => r.Kind == RuleKind.NameGlob).Text);
        Assert.Equal(2, ray.Rules.Count);
        Assert.True(even.MatchAny);
        Assert.Contains(even.Rules, r => r.Kind == RuleKind.ManufacturerId && r.CompanyId == 0x10F9);
        Assert.Contains(even.Rules, r => r.Kind == RuleKind.NameContains && r.Text == "Even G1");
        Assert.DoesNotContain("Name-only", even.Notes, StringComparison.OrdinalIgnoreCase);
        var liteOuis = Ouis(lite);
        Assert.Contains("E0:0A:F6", liteOuis);
        Assert.Contains("14:B5:CD", liteOuis);
        Assert.Contains("08:3A:88", liteOuis);
        Assert.True(string.IsNullOrWhiteSpace(lite.AttentionNote));
        Assert.Equal(SignatureClass.Camera, lite.Kind);
        Assert.DoesNotContain(Stock, f => f.Rules.Any(r =>
            r.Kind == RuleKind.ManufacturerId && r.CompanyId is 0x07D7 or 0x0BA7 or 0xFD5F));
        Assert.DoesNotContain(Stock, f => f.Rules.Any(r =>
            r.Text.Contains("FlockCam", StringComparison.OrdinalIgnoreCase) ||
            r.Text.Contains("RWLS", StringComparison.OrdinalIgnoreCase) ||
            r.Text.Contains("META_RB", StringComparison.OrdinalIgnoreCase) ||
            r.Text.Equals("Pico", StringComparison.OrdinalIgnoreCase)));
        Assert.Contains(flock.Rules, r => r.Kind == RuleKind.NameContains && r.Text == "Flock");

        var both = Tagged("01", "RayNeo Air 2", 0x0BC6);
        var bare = Tagged("02", "RayNeo", 0x0BC6);
        var lower = Tagged("03", "rayneo", 0x0BC6);
        var nameOnly = Tagged("04", "RayNeo Air 2", null);
        var tclOnly = Tagged("05", "TCL 50", 0x0BC6);
        var midName = Tagged("06", "My RayNeo", 0x0BC6);
        var qinheng = Tagged("07", "RayNeo", 0x07D7);
        var hearx = Tagged("08", "RayNeo", 0x0BA7);
        var evenId = Tagged("09", "", 0x10F9);
        var evenName = Tagged("0A", "Even G1", null);
        var uart = Ble(serviceUuids: ["6E400001-B5A3-F393-E0A9-E50E24DCCA9E"]) with { Key = "BLE:0B", Mac = "AA:BB:CC:DD:EE:0B" };
        var liteA = Wifi("E0:0A:F6:11:22:33");
        var liteB = Wifi("14:B5:CD:11:22:33");
        var flockCam = Wifi("02:11:22:33:44:55", "FlockCam-9");
        var rwls = Wifi("02:11:22:33:44:56", "RWLS-1");
        var hits = engine.Match(
            [both, bare, lower, nameOnly, tclOnly, midName, qinheng, hearx, evenId, evenName, uart, liteA, liteB, flockCam, rwls],
            Stock);
        Assert.Contains("fleet-rayneo", hits[both.Key]);
        Assert.Contains("fleet-rayneo", hits[bare.Key]);
        Assert.Contains("fleet-rayneo", hits[lower.Key]);
        Assert.DoesNotContain("fleet-rayneo", hits[nameOnly.Key]);
        Assert.DoesNotContain("fleet-rayneo", hits[tclOnly.Key]);
        Assert.DoesNotContain("fleet-rayneo", hits[midName.Key]);
        Assert.DoesNotContain("fleet-rayneo", hits[qinheng.Key]);
        Assert.DoesNotContain("fleet-rayneo", hits[hearx.Key]);
        Assert.Contains("fleet-even-g1", hits[evenId.Key]);
        Assert.DoesNotContain("fleet-rayneo", hits[evenId.Key]);
        Assert.Contains("fleet-even-g1", hits[evenName.Key]);
        Assert.DoesNotContain("fleet-even-g1", hits[uart.Key]);
        Assert.Contains("fleet-liteon-camera-radio", hits[liteA.Key]);
        Assert.DoesNotContain("fleet-flock-cameras", hits[liteA.Key]);
        Assert.Contains("fleet-liteon-camera-radio", hits[liteB.Key]);
        Assert.DoesNotContain("fleet-flock-cameras", hits[liteB.Key]);
        Assert.Contains("fleet-flock-cameras", hits[flockCam.Key]);
        Assert.DoesNotContain("fleet-flock-cameras", hits[rwls.Key]);
        Assert.Empty((liteA with { FleetIds = hits[liteA.Key] }).AttentionNotes(Stock));
    }

    [Fact]
    public void Catalog90FrenchPlateAndNamedDronesStayNarrow()
    {
        var engine = new SignatureEngine();
        string[] added = ["fleet-tello", "fleet-potensic", "fleet-holystone", "fleet-hubsan", "fleet-yuneec", "fleet-swellpro", "fleet-crazyflie"];
        foreach (var id in added)
        {
            var fleet = Stock.Single(f => f.Id == id);
            Assert.Equal(SignatureClass.Drone, fleet.Kind);
            Assert.True(fleet.MatchAny);
            Assert.True(string.IsNullOrWhiteSpace(fleet.AttentionNote));
        }
        var remote = Stock.Single(f => f.Id == "fleet-remote-id");
        Assert.Contains(remote.Rules, r => r.Kind == RuleKind.VendorIeOui && r.Text.Equals("6A:5C:35", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(remote.Rules, r => r.Kind == RuleKind.VendorIeOui && r.Text.Equals("FA:0B:BC", StringComparison.OrdinalIgnoreCase));
        var crazy = Stock.Single(f => f.Id == "fleet-crazyflie");
        Assert.DoesNotContain(crazy.Rules, r => r.Kind == RuleKind.ManufacturerId);
        Assert.Contains(Stock.Single(f => f.Id == "fleet-parrot").Rules, r => r.Kind == RuleKind.NameGlob && r.Text == "Skycontroller*");

        var french = Wifi("02:00:00:00:00:01", "RID", "6A:5C:35");
        var astm = Wifi("02:00:00:00:00:02", "RID", "FA:0B:BC");
        var nan = Wifi("02:00:00:00:00:03", "RID", "50:6F:9A");
        var parrotOui = Wifi("90:3A:E6:11:22:33", "Home");
        var tello = Wifi("02:00:00:00:00:11", "TELLO-ABCDEF");
        var talent = Wifi("02:00:00:00:00:12", "RMTT-9AFF2A");
        var telloMid = Wifi("02:00:00:00:00:13", "my TELLO");
        var dji = Wifi("02:00:00:00:00:14", "DJI-Mini");
        var potensic = Wifi("02:00:00:00:00:21", "Potensic-ATOM-1");
        var atom = Wifi("02:00:00:00:00:22", "ATOM-1");
        var holy = Wifi("02:00:00:00:00:31", "HolyStoneFPV-1");
        var holySpaced = Wifi("02:00:00:00:00:32", "Holy Stone HS720");
        var fpv = Wifi("02:00:00:00:00:33", "FPV_WIFI");
        var hubsan = Wifi("02:00:00:00:00:41", "HUBSAN-Zino");
        var exo = Wifi("02:00:00:00:00:42", "EXO-1234");
        var yuneec = Wifi("02:00:00:00:00:51", "Yuneec-H520");
        var typhoon = Wifi("02:00:00:00:00:52", "Typhoon");
        var swell = Wifi("02:00:00:00:00:61", "SwellPro-Splash");
        var crazyName = Wifi("02:00:00:00:00:71", "Crazyflie");
        var bitcraze = Tagged("81", "", 0x01C5);
        var sky = Wifi("02:00:00:00:00:91", "Skycontroller 3");
        var autelSsid = Wifi("02:00:00:00:00:A1", "default-ssid");
        var elrs = Wifi("02:00:00:00:00:A2", "ExpressLRS");
        var hits = engine.Match(
            [french, astm, nan, parrotOui, tello, talent, telloMid, dji, potensic, atom, holy, holySpaced, fpv, hubsan, exo,
             yuneec, typhoon, swell, crazyName, bitcraze, sky, autelSsid, elrs],
            Stock);
        Assert.Contains("fleet-remote-id", hits[french.Key]);
        Assert.Contains("fleet-remote-id", hits[astm.Key]);
        Assert.DoesNotContain("fleet-remote-id", hits[nan.Key]);
        Assert.DoesNotContain("fleet-parrot", hits[parrotOui.Key]);
        Assert.Contains("fleet-tello", hits[tello.Key]);
        Assert.DoesNotContain("fleet-dji", hits[tello.Key]);
        Assert.Contains("fleet-tello", hits[talent.Key]);
        Assert.DoesNotContain("fleet-tello", hits[telloMid.Key]);
        Assert.Contains("fleet-dji", hits[dji.Key]);
        Assert.DoesNotContain("fleet-tello", hits[dji.Key]);
        Assert.Contains("fleet-potensic", hits[potensic.Key]);
        Assert.DoesNotContain("fleet-potensic", hits[atom.Key]);
        Assert.Contains("fleet-holystone", hits[holy.Key]);
        Assert.Contains("fleet-holystone", hits[holySpaced.Key]);
        Assert.DoesNotContain("fleet-holystone", hits[fpv.Key]);
        Assert.Contains("fleet-hubsan", hits[hubsan.Key]);
        Assert.DoesNotContain("fleet-hubsan", hits[exo.Key]);
        Assert.Contains("fleet-yuneec", hits[yuneec.Key]);
        Assert.DoesNotContain("fleet-yuneec", hits[typhoon.Key]);
        Assert.Contains("fleet-swellpro", hits[swell.Key]);
        Assert.Contains("fleet-crazyflie", hits[crazyName.Key]);
        Assert.DoesNotContain("fleet-crazyflie", hits[bitcraze.Key]);
        Assert.Contains("fleet-parrot", hits[sky.Key]);
        Assert.DoesNotContain("fleet-autel", hits[autelSsid.Key]);
        Assert.True(added.All(id => !hits[elrs.Key].Contains(id)));
        foreach (var id in added)
        {
            var quiet = Wifi("02:11:22:33:44:55", "Home");
            Assert.DoesNotContain(id, engine.Match([quiet], Stock)[quiet.Key]);
        }
    }

    private static HashSet<string> Ouis(Fleet fleet) =>
        fleet.Rules.Where(r => r.Kind == RuleKind.Oui).Select(r => r.Text.ToUpperInvariant()).ToHashSet();

    private static Sighting Tagged(string tail, string name, int? manufacturerId) =>
        Ble(name: name, manufacturerId: manufacturerId) with { Key = "BLE:" + tail, Mac = "AA:BB:CC:DD:EE:" + tail };

    private static Sighting Wifi(string mac, string name = "Home", string? vendorIe = null) =>
        Ble(name: name) with
        {
            Key = "WIFI:" + mac,
            Kind = RadioKind.Wifi,
            Mac = mac,
            Randomized = false,
            VendorIeOuis = vendorIe is null ? [] : [vendorIe],
        };

    private static Sighting Ble(
        string name = "",
        int? manufacturerId = null,
        string manufacturerDataHex = "",
        IReadOnlyList<string>? serviceUuids = null) => new()
    {
        Key = "BLE:AA:BB:CC:DD:EE:FF",
        Kind = RadioKind.Ble,
        Mac = "AA:BB:CC:DD:EE:FF",
        Name = name,
        Rssi = -50,
        RssiMin = -50,
        RssiMax = -50,
        Randomized = true,
        ServiceUuids = serviceUuids ?? [],
        ManufacturerId = manufacturerId,
        ManufacturerDataHex = manufacturerDataHex,
        FirstSeen = 1,
        LastSeen = 1,
        HitCount = 1,
        Facts = new RadioFacts
        {
            MfgRecords = manufacturerId is { } id ? [new MfgRecord(id, manufacturerDataHex)] : [],
        },
    };
}

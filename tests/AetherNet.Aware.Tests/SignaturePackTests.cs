// SPDX-License-Identifier: MIT
// Ported from Fieldwatch (cf6562d):
//   app/src/test/java/app/fieldwatch/domain/StockCatalogFileTest.kt — the pack's contents and the Remote ID pin
//     (distPackMatchesDefaultCatalog's DefaultCatalog-equality half is not ported: the pack IS our stock catalog);
//   app/src/test/java/app/fieldwatch/domain/SignatureExchangeTest.kt — parsing (parse*), built from the stock pack's
//     own JSON where upstream encoded a pack first (export is not ported yet);
//   app/src/test/java/app/fieldwatch/domain/SignatureFieldDecoderTest.kt — liveFlagRoundTripsAndOlderPacksOmitIt.
// Copyright (c) 2026 Off Grid Pete LLC. See src/AetherNet.Aware/NOTICE.md.

using System.Text.Json.Nodes;
using Xunit;

namespace AetherNet.Aware.Tests;

public class SignaturePackTests
{
    private static readonly IReadOnlyList<Fleet> Stock = StockSignatures.Fleets;

    [Fact]
    public void StockPackIsFieldwatchCatalog90()
    {
        var pack = StockSignatures.Pack;
        Assert.Equal("fieldwatch-signatures", pack.Format);
        Assert.Equal(90, pack.CatalogVersion);
        Assert.Equal(252, pack.Fleets.Count);
        Assert.Equal(6981, pack.Fleets.Sum(f => f.Rules.Count));
        Assert.All(pack.Fleets, f => Assert.True(f.BuiltIn));
        Assert.Equal(pack.Fleets.Count, pack.Fleets.Select(f => f.Id).Distinct().Count());
    }

    [Fact]
    public void DistPackMatchesDefaultCatalog()
    {
        var ray = Stock.Single(f => f.Id == "fleet-rayneo");
        Assert.False(ray.MatchAny);
        Assert.Equal(0x0BC6, ray.Rules.Single(r => r.Kind == RuleKind.ManufacturerId).CompanyId);
        Assert.Equal("RayNeo*", ray.Rules.Single(r => r.Kind == RuleKind.NameGlob).Text);
        Assert.Equal(2, ray.Rules.Count);
        var even = Stock.Single(f => f.Id == "fleet-even-g1");
        Assert.Contains(even.Rules, r => r.Kind == RuleKind.ManufacturerId && r.CompanyId == 0x10F9);
        var lite = Stock.Single(f => f.Id == "fleet-liteon-camera-radio");
        var ouis = lite.Rules.Where(r => r.Kind == RuleKind.Oui).Select(r => r.Text.ToUpperInvariant()).ToHashSet();
        Assert.Contains("E0:0A:F6", ouis);
        Assert.Contains("14:B5:CD", ouis);
        Assert.True(string.IsNullOrWhiteSpace(lite.AttentionNote));
        var remote = Stock.Single(f => f.Id == "fleet-remote-id");
        Assert.Contains(remote.Rules, r => r.Kind == RuleKind.VendorIeOui && r.Text.Equals("6A:5C:35", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(remote.Rules, r => r.Kind == RuleKind.VendorIeOui && r.Text.Equals("FA:0B:BC", StringComparison.OrdinalIgnoreCase));
        var tello = Stock.Single(f => f.Id == "fleet-tello");
        Assert.Equal(new HashSet<string> { "TELLO*", "RMTT*" }, tello.Rules.Select(r => r.Text).ToHashSet());
        var crazy = Stock.Single(f => f.Id == "fleet-crazyflie");
        Assert.DoesNotContain(crazy.Rules, r => r.Kind == RuleKind.ManufacturerId);
        Assert.Contains(Stock.Single(f => f.Id == "fleet-parrot").Rules, r => r.Kind == RuleKind.NameGlob && r.Text == "Skycontroller*");
    }

    [Fact]
    public void RemoteIdPinFieldsMatchWhenLiveKeysAreDropped()
    {
        var raw = StockSignatures.Json;
        var full = SignatureExchange.Parse(raw).Fleets.Single(f => f.Id == "fleet-remote-id");
        var older = SignatureExchange.Parse(DropLiveKeys(raw)).Fleets.Single(f => f.Id == "fleet-remote-id");
        Assert.Equal(
            full.Decode!.Fields.Select(f => f with { Live = false, LiveEmphasis = [], EnumNotes = null }).Select(Describe),
            older.Decode!.Fields.Select(Describe));

        const string location = "0D0012200000000084D717007FE4D3000098083408000000000000";
        var ble = RidBle(location);
        var pinned = PayloadLocation.ApplySticky(ble with { FleetIds = [full.Id] }, [full]);
        Assert.Equal(40.0, pinned.PayloadLat!.Value, 1e-6);
        Assert.Equal(-74.0, pinned.PayloadLon!.Value, 1e-6);
        Assert.Equal(0.0, pinned.PayloadHeading!.Value, 1e-6);
        Assert.Equal(0.0, pinned.PayloadSpeed!.Value, 1e-6);
        var fullRows = SignatureFieldDecoder.DecodeSighting(ble with { FleetIds = [full.Id] }, [full]);
        var oldRows = SignatureFieldDecoder.DecodeSighting(ble with { FleetIds = [older.Id] }, [older]);
        foreach (var id in new[] { "latitude", "longitude", "heading", "hspeed", "alt_geo", "status" })
        {
            Assert.Equal(Row(fullRows, id).Display, Row(oldRows, id).Display);
            Assert.Equal(Row(fullRows, id).Number, Row(oldRows, id).Number);
        }
        const string pack = "D9F2190302123135383146335954444A3144303033315A353330000000" +
            "1220820A00864228110CFF80CF0000F508B2083A022E310A00" +
            "420176E42711B5FF81CF010000000000000005088B02900E00";
        var wifiPin = PayloadLocation.ApplySticky(RidWifi(pack) with { FleetIds = [full.Id] }, [full]);
        Assert.Equal("1581F3YTDJ1D0031Z530", wifiPin.PayloadUasId);
        Assert.Equal(28.7851142, wifiPin.PayloadLat!.Value, 1e-6);
        Assert.Equal(-81.3629684, wifiPin.PayloadLon!.Value, 1e-6);
        Assert.Equal(130.0, wifiPin.PayloadHeading!.Value, 1e-6);
        Assert.Equal(2.5, wifiPin.PayloadSpeed!.Value, 1e-6);
        Assert.Equal(28.7827062, wifiPin.PayloadOpLat!.Value, 1e-6);
        Assert.Equal(-81.3563979, wifiPin.PayloadOpLon!.Value, 1e-6);
    }

    [Fact]
    public void ParsePackDropsUnknownDecodeSourceKeepsSignature()
    {
        var json = PackOf("fleet-govee", fleet => fleet["decode"]!["source"] = "vendorIe");
        var parsed = SignatureExchange.ParsePack(json);
        Assert.Equal(1, parsed.SkippedDecode);
        var row = Assert.Single(parsed.Pack.Fleets);
        Assert.Equal("fleet-govee", row.Id);
        Assert.NotEmpty(row.Rules);
        Assert.Null(row.Decode);
    }

    [Fact]
    public void ParseAcceptsLegacySpectreFormat()
    {
        var json = PackOf(Stock[0].Id, _ => { }).Replace("fieldwatch-signatures", "spectre-signatures");
        var pack = SignatureExchange.Parse(json);
        Assert.Equal("spectre-signatures", pack.Format);
        Assert.Single(pack.Fleets);
    }

    [Fact]
    public void ParseRejectsLogsAndEmpty()
    {
        const string log = "timestamp,iso,kind,mac,name\n1,x,WIFI,AA:BB:CC:DD:EE:FF,test\n";
        var notPack = Assert.Throws<ArgumentException>(() => SignatureExchange.Parse(log));
        Assert.Contains("Not a Fieldwatch signature pack", notPack.Message);
        var empty = Assert.Throws<ArgumentException>(() => SignatureExchange.Parse(""));
        Assert.Contains("empty", empty.Message);
    }

    [Fact]
    public void LiveFlagIsInTheStockPackAndOlderPacksOmitIt()
    {
        var mode = Stock.Single(f => f.Id == "fleet-dult").Decode!.Fields.Single(f => f.Id == "mode");
        Assert.True(mode.Live);
        Assert.Equal(["0"], mode.LiveEmphasis);
        Assert.Contains("about a day", mode.EnumNotes!["0"]);
        Assert.Contains("own tag", mode.EnumNotes!["1"]);
        const string older = """
            {"format":"fieldwatch-signatures","formatVersion":1,"catalogVersion":1,"fleets":[
              {"id":"fleet-x","name":"X","rules":[{"kind":"SERVICE_DATA","text":"FCB2"}],
               "decode":{"source":"serviceData","serviceUuid":"FCB2","fields":[
                 {"id":"mode","label":"Mode","offset":1,"type":"bits","bitOffset":0,"bitWidth":1,
                  "enum":{"0":"separated","1":"near owner"}}
               ]}}
            ]}
            """;
        var parsed = Assert.Single(Assert.Single(SignatureExchange.Parse(older).Fleets).Decode!.Fields);
        Assert.False(parsed.Live);
        Assert.Empty(parsed.LiveEmphasis);
        Assert.Null(parsed.EnumNotes);
    }

    /// <summary>The stock pack's JSON with just one row, optionally changed — what upstream got by encoding that row.</summary>
    private static string PackOf(string fleetId, Action<JsonNode> change)
    {
        var root = JsonNode.Parse(StockSignatures.Json)!.AsObject();
        var fleet = root["fleets"]!.AsArray().Single(f => (string?)f!["id"] == fleetId)!.DeepClone();
        change(fleet);
        root["fleets"] = new JsonArray(fleet);
        return root.ToJsonString();
    }

    private static string DropLiveKeys(string raw)
    {
        static JsonNode? Strip(JsonNode? node) => node switch
        {
            JsonObject obj => new JsonObject(obj
                .Where(kv => kv.Key is not ("live" or "liveEmphasis" or "enumNotes"))
                .Select(kv => KeyValuePair.Create(kv.Key, Strip(kv.Value)))),
            JsonArray arr => new JsonArray(arr.Select(Strip).ToArray()),
            null => null,
            _ => node.DeepClone(),
        };
        return Strip(JsonNode.Parse(raw))!.ToJsonString();
    }

    /// <summary>A field's contents, collections included (records compare lists by reference).</summary>
    private static string Describe(DecodeField f) =>
        $"{f.Id}|{f.Label}|{f.Offset}|{f.Length}|{f.Type}|{f.Endian}|{f.BitOffset}|{f.BitWidth}|{f.Scale}|{f.OffsetAdd}|" +
        $"{f.Modulo}|{f.Unit}|{Map(f.EnumLabels)}|{f.Live}|{string.Join(",", f.LiveEmphasis)}|{Map(f.EnumNotes)}|{Gate(f.Gate)}";

    private static string Map(IReadOnlyDictionary<string, string>? map) =>
        map is null ? "null" : string.Join(",", map.Select(kv => kv.Key + "=" + kv.Value));

    private static string Gate(DecodeWhen? g) =>
        g is null ? "null" : $"{g.Offset}:{g.Length}:{g.Op}:{g.ValueHex}:{Gate(g.And)}";

    private static DecodedFieldValue Row(IReadOnlyList<DecodedFieldValue> rows, string id) => rows.First(r => r.Id == id);

    private static Sighting RidBle(string dataHex) => new()
    {
        Key = "BLE:AA:BB:CC:DD:EE:01",
        Kind = RadioKind.Ble,
        Mac = "AA:BB:CC:DD:EE:01",
        Rssi = -50,
        RssiMin = -50,
        RssiMax = -50,
        FrequencyMhz = 2402,
        Randomized = true,
        ServiceUuids = ["FFFA"],
        FirstSeen = 1,
        LastSeen = 1,
        HitCount = 1,
        FleetIds = ["fleet-remote-id"],
        Facts = new RadioFacts { ServiceData = [new("FFFA", dataHex)] },
    };

    private static Sighting RidWifi(string dataHex) => new()
    {
        Key = "WIFI:60:60:1F:06:31:08",
        Kind = RadioKind.Wifi,
        Mac = "60:60:1F:06:31:08",
        Name = "RID",
        Rssi = -80,
        RssiMin = -80,
        RssiMax = -80,
        Channel = 6,
        FrequencyMhz = 2437,
        FirstSeen = 1,
        LastSeen = 1,
        HitCount = 1,
        FleetIds = ["fleet-remote-id"],
        Facts = new RadioFacts { VendorIes = [new("FA:0B:BC", 0x0D, dataHex)] },
    };
}

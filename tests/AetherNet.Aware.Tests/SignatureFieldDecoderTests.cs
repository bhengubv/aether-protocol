// SPDX-License-Identifier: MIT
// Ported from Fieldwatch app/src/test/java/app/fieldwatch/domain/SignatureFieldDecoderTest.kt (cf6562d).
// Not ported: packRoundTripKeepsIncludeCompanyId / packRoundTripKeepsDecode (export) and
// importBackupAppliesIncomingDecodeOnSameId (import) — they come with signature packs over the mesh.
// liveFlagRoundTripsAndOlderPacksOmitIt is in SignaturePackTests.
// Copyright (c) 2026 Off Grid Pete LLC. See src/AetherNet.Aware/NOTICE.md.

using System.Globalization;
using System.Text;
using Xunit;

namespace AetherNet.Aware.Tests;

public class SignatureFieldDecoderTests
{
    private static Fleet Stock(string id) => StockSignatures.Fleets.Single(f => f.Id == id);

    [Fact]
    public void RuuviRawV2TemperatureHumidity()
    {
        // Data Format 5 after company ID 0x0499. Example from Ruuvi docs.
        const string payload = "0512FC5394C37C0004FFFC040CAC364200CDCBB8334C884F";
        var fleet = new Fleet
        {
            Id = "fleet-ruuvi",
            Name = "Ruuvi",
            Decode = new FleetDecode
            {
                Source = DecodeSource.ManufacturerData,
                CompanyId = 0x0499,
                Fields =
                [
                    new DecodeField
                    {
                        Id = "format", Label = "Format", Offset = 0, Type = DecodeType.U8,
                        Gate = new DecodeWhen { Offset = 0, Op = DecodeWhenOp.Eq, ValueHex = "05" },
                    },
                    new DecodeField
                    {
                        Id = "temperature", Label = "Temperature", Offset = 1, Type = DecodeType.I16,
                        Endian = DecodeEndian.Be, Scale = 0.005, Unit = "°C",
                    },
                    new DecodeField
                    {
                        Id = "humidity", Label = "Humidity", Offset = 3, Type = DecodeType.U16,
                        Endian = DecodeEndian.Be, Scale = 0.0025, Unit = "%",
                    },
                ],
            },
        };
        var rows = SignatureFieldDecoder.DecodeSighting(Ble(0x0499, payload, fleet.Id), [fleet]);
        Assert.Equal("5", rows.Display("format"));
        Assert.Equal("24.3 °C", rows.Display("temperature"));
        Assert.Equal("53.49 %", rows.Display("humidity"));
    }

    [Fact]
    public void ScaleThenOffsetAdd()
    {
        var fleet = Manufacturer(new DecodeField
        {
            Id = "pressure", Label = "Pressure", Offset = 0, Type = DecodeType.U16, Endian = DecodeEndian.Be,
            Scale = 1.0, OffsetAdd = 50000.0, Unit = "Pa",
        });
        var rows = SignatureFieldDecoder.DecodeSighting(Ble(1, "C37C", fleet.Id), [fleet]);
        // 0xC37C = 50044; + 50000 = 100044
        Assert.Equal("100044 Pa", Assert.Single(rows).Display);
    }

    [Fact]
    public void WhenMismatchSkipsField()
    {
        var fleet = Manufacturer(new DecodeField
        {
            Id = "temp", Label = "Temperature", Offset = 1, Type = DecodeType.U8,
            Gate = new DecodeWhen { Offset = 0, Op = DecodeWhenOp.Eq, ValueHex = "05" },
        });
        Assert.Empty(SignatureFieldDecoder.DecodeSighting(Ble(1, "0312", fleet.Id), [fleet]));
    }

    [Fact]
    public void ShortPayloadSkipsWithoutThrowing()
    {
        var fleet = Manufacturer(new DecodeField { Id = "wide", Label = "Wide", Offset = 0, Type = DecodeType.U32 });
        Assert.Empty(SignatureFieldDecoder.DecodeSighting(Ble(1, "01", fleet.Id), [fleet]));
    }

    [Fact]
    public void ServiceDataUuidMatch()
    {
        var fleet = new Fleet
        {
            Id = "f",
            Name = "S",
            Decode = new FleetDecode
            {
                Source = DecodeSource.ServiceData,
                ServiceUuid = "FEAA",
                Fields = [new DecodeField { Id = "frame", Label = "Frame", Offset = 0, Type = DecodeType.U8 }],
            },
        };
        var device = BleService("FEAA", "20AB", fleet.Id);
        Assert.Equal("32", Assert.Single(SignatureFieldDecoder.DecodeSighting(device, [fleet])).Display);
    }

    [Fact]
    public void WifiIsIgnored()
    {
        var fleet = Manufacturer(new DecodeField { Id = "x", Label = "X", Offset = 0, Type = DecodeType.U8 });
        var ap = Ble(1, "01", fleet.Id) with { Kind = RadioKind.Wifi };
        Assert.Empty(SignatureFieldDecoder.DecodeSighting(ap, [fleet]));
    }

    [Fact]
    public void WifiOnlySignatureHasNoDecodeEditor()
    {
        var ap = new Fleet
        {
            Id = "fleet-unifi",
            Name = "UniFi AP",
            Rules = [new MatchRule { Kind = RuleKind.NameContains, Text = "UniFi", Radio = RadioKind.Wifi }],
        };
        Assert.False(ap.CanHaveBleDecode());
        var ble = new Fleet
        {
            Id = "fleet-fitbit",
            Name = "Fitbit",
            Rules = [new MatchRule { Kind = RuleKind.ManufacturerId, CompanyId = 0x018E }],
        };
        Assert.True(ble.CanHaveBleDecode());
    }

    [Fact]
    public void NormalizeEnumKeysTreatHexAndDecimalAsSame()
    {
        Assert.Equal("5", DecodeNormalize.NormalizeEnumKey("0x05"));
        Assert.Equal("5", DecodeNormalize.NormalizeEnumKey("05"));
        Assert.Equal("5", DecodeNormalize.NormalizeEnumKey("5"));
        Assert.Equal("10", DecodeNormalize.NormalizeEnumKey("10"));
        var labels = DecodeNormalize.NormalizeEnumLabels(new Dictionary<string, string> { ["0x02"] = "Active", ["1"] = "Idle" });
        Assert.Equal("Active", labels!["2"]);
        Assert.Equal("Idle", labels["1"]);
    }

    [Fact]
    public void NormalizedWhenKeepsEvenHex()
    {
        var gate = DecodeNormalize.NormalizeGate(new DecodeWhen { Offset = 0, Op = DecodeWhenOp.Eq, ValueHex = "0x5" });
        Assert.Equal("05", gate!.ValueHex);
        Assert.Equal(1, gate.Length);
    }

    [Fact]
    public void NormalizedWhenTwoByteHexSetsLength()
    {
        var gate = DecodeNormalize.NormalizeGate(new DecodeWhen { Offset = 10, Op = DecodeWhenOp.Eq, ValueHex = "544E" });
        Assert.Equal("544E", gate!.ValueHex);
        Assert.Equal(2, gate.Length);
        Assert.Equal(10, gate.Offset);
    }

    [Fact]
    public void EnumMapsRawValue()
    {
        var fleet = Manufacturer(new DecodeField
        {
            Id = "mode", Label = "Mode", Offset = 0, Type = DecodeType.U8,
            EnumLabels = new Dictionary<string, string> { ["1"] = "idle", ["2"] = "active" },
        });
        Assert.Equal("active", Assert.Single(SignatureFieldDecoder.DecodeSighting(Ble(1, "02", fleet.Id), [fleet])).Display);
    }

    [Fact]
    public void CatalogRuuviRawV2()
    {
        var fleet = Stock("fleet-ruuvi");
        var rows = SignatureFieldDecoder.DecodeSighting(Ble(0x0499, "0512FC5394C37C0004FFFC040CAC364200CDCBB8334C884F", fleet.Id), [fleet]);
        Assert.Equal("5", rows.Display("format"));
        Assert.Equal("24.3 °C", rows.Display("temperature"));
        Assert.Equal("53.49 %", rows.Display("humidity"));
        Assert.Equal("1000.44 hPa", rows.Display("pressure"));
        Assert.Equal("0.004 g", rows.Display("acc_x"));
        Assert.Equal("-0.004 g", rows.Display("acc_y"));
        Assert.Equal("1.036 g", rows.Display("acc_z"));
        Assert.Equal("2977 mV", rows.Display("battery"));
        Assert.Equal("4 dBm", rows.Display("tx_power"));
        Assert.Equal("CB:B8:33:4C:88:4F", rows.Display("mac"));
    }

    [Fact]
    public void CatalogRemoteIdBasicId()
    {
        var fleet = Stock("fleet-remote-id");
        const string payload = "0D000212" + "5445535453455249414C31323334353637383930" + "000000";
        var rows = SignatureFieldDecoder.DecodeSighting(BleService("FFFA", payload, fleet.Id), [fleet]);
        Assert.Equal("Open Drone ID", rows.Display("app"));
        Assert.Equal("Basic ID", rows.Display("msg_type"));
        Assert.Equal("Serial (CTA-2063)", rows.Display("id_type"));
        Assert.Equal("Helicopter / multirotor", rows.Display("ua_type"));
        Assert.Equal("TESTSERIAL1234567890", rows.Display("uas_id"));
    }

    [Fact]
    public void CatalogPenguinTnSerial()
    {
        var fleet = Stock("fleet-penguin");
        // Ryan O'Horo SCAN_RSP payload after company 0x09C8.
        const string payload = "D8A0D89F4A5E2030502A544E3732303233303232303030373731";
        var rows = SignatureFieldDecoder.DecodeSighting(Ble(0x09C8, payload, fleet.Id), [fleet]);
        Assert.Equal("D8:A0:D8:9F:4A:5E", rows.Display("adv_mac"));
        Assert.Equal("TN72023022000771", rows.Display("serial"));
    }

    [Fact]
    public void CatalogPenguinWithoutTnSkipsDecode()
    {
        var fleet = Stock("fleet-penguin");
        const string payload = "D8A0D89F4A5E2030502A00003732303233303232303030373731";
        Assert.Empty(SignatureFieldDecoder.DecodeSighting(Ble(0x09C8, payload, fleet.Id), [fleet]));
    }

    [Fact]
    public void EqGateUsesHexByteLength()
    {
        var fleet = Manufacturer(new DecodeField
        {
            Id = "tag", Label = "Tag", Offset = 0, Length = 2, Type = DecodeType.Utf8,
            Gate = new DecodeWhen { Offset = 0, Op = DecodeWhenOp.Eq, ValueHex = "544E" },
        });
        Assert.Equal("TN", SignatureFieldDecoder.DecodeSighting(Ble(1, "544E3132", fleet.Id), [fleet]).Display("tag"));
    }

    [Fact]
    public void CatalogRemoteIdLocation()
    {
        var fleet = Stock("fleet-remote-id");
        // 40° N, 74° W, HAE 100 m, height 50 m (OpenDroneID packed, proto v2).
        const string payload = "0D0012200000000084D717007FE4D3000098083408000000000000";
        var rows = SignatureFieldDecoder.DecodeSighting(BleService("FFFA", payload, fleet.Id), [fleet]);
        Assert.Equal("Location", rows.Display("msg_type"));
        Assert.Equal("Airborne", rows.Display("status"));
        var airborne = Assert.Single(SignatureFieldDecoder.LiveChips(BleService("FFFA", payload, fleet.Id), [fleet]));
        Assert.Equal("Airborne", airborne.Text);
        Assert.False(airborne.Emphasis);
        var emergency = "0D001230" + payload["0D001220".Length..];
        var strong = Assert.Single(SignatureFieldDecoder.LiveChips(BleService("FFFA", emergency, fleet.Id), [fleet]));
        Assert.Equal("Emergency", strong.Text);
        Assert.True(strong.Emphasis);
        var basicId = "0D000220" + payload["0D001220".Length..];
        Assert.Empty(SignatureFieldDecoder.LiveChips(BleService("FFFA", basicId, fleet.Id), [fleet]));
        Assert.Equal("40 °", rows.Display("latitude"));
        Assert.Equal("-74 °", rows.Display("longitude"));
        Assert.Equal("100 m", rows.Display("alt_geo"));
        Assert.Equal("50 m", rows.Display("height"));
        Assert.Equal("0 °", rows.Display("heading"));
        Assert.Equal("0 m/s", rows.Display("hspeed"));
        Assert.Equal(40.0, rows.Number("latitude")!.Value, 1e-6);
        Assert.Equal(-74.0, rows.Number("longitude")!.Value, 1e-6);
        Assert.Equal(100.0, rows.Number("alt_geo")!.Value, 1e-6);
        Assert.Equal(0.0, rows.Number("heading")!.Value, 1e-6);
        Assert.Equal(0.0, rows.Number("hspeed")!.Value, 1e-6);
    }

    [Fact]
    public void CatalogRemoteIdWifiMessagePackUsesBleMap()
    {
        var fleet = Stock("fleet-remote-id");
        const string hex = "D9F2190302123135383146335954444A3144303033315A353330000000" +
            "1220820A00864228110CFF80CF0000F508B2083A022E310A00" +
            "420176E42711B5FF81CF010000000000000005088B02900E00";
        var rows = SignatureFieldDecoder.DecodeSighting(WifiRid(hex, fleet.Id), [fleet]);
        Assert.Equal("1581F3YTDJ1D0031Z530", rows.Display("uas_id"));
        Assert.Equal("Helicopter / multirotor", rows.Display("ua_type"));
        Assert.Equal("Airborne", rows.Display("status"));
        Assert.Equal(28.7851142, rows.Number("latitude")!.Value, 1e-6);
        Assert.Equal(-81.3629684, rows.Number("longitude")!.Value, 1e-6);
        Assert.Equal(130.0, rows.Number("heading")!.Value, 1e-6);
        Assert.Equal(2.5, rows.Number("hspeed")!.Value, 1e-6);
        Assert.Equal(28.7827062, rows.Number("op_lat")!.Value, 1e-6);
        Assert.Equal(-81.3563979, rows.Number("op_lon")!.Value, 1e-6);
    }

    [Fact]
    public void CatalogRemoteIdLocationWestHeadingAndSpeed()
    {
        var fleet = Stock("fleet-remote-id");
        // Airborne, EWDirection, direction 90 → 270°. Speed 40 × 0.25 = 10 m/s.
        const string payload = "0D0012225A28000084D717007FE4D3000098083408000000000000";
        var rows = SignatureFieldDecoder.DecodeSighting(BleService("FFFA", payload, fleet.Id), [fleet]);
        Assert.Equal("Location", rows.Display("msg_type"));
        Assert.Equal("270 °", rows.Display("heading"));
        Assert.Equal("10 m/s", rows.Display("hspeed"));
        Assert.Equal(270.0, rows.Number("heading")!.Value, 1e-6);
        Assert.Equal(10.0, rows.Number("hspeed")!.Value, 1e-6);
        Assert.Single(rows, r => r.Id == "heading");
        Assert.Single(rows, r => r.Id == "hspeed");
    }

    [Fact]
    public void CatalogRemoteIdLocationHighSpeedMultiplier()
    {
        var fleet = Stock("fleet-remote-id");
        // SpeedMult set, SpeedHorizontal 4 → 4×0.75 + 255×0.25 = 66.75 m/s.
        const string payload = "0D0012210004000084D717007FE4D3000098083408000000000000";
        var rows = SignatureFieldDecoder.DecodeSighting(BleService("FFFA", payload, fleet.Id), [fleet]);
        Assert.Equal(66.75, rows.Number("hspeed")!.Value, 1e-6);
        Assert.Equal(0.0, rows.Number("heading")!.Value, 1e-6);
    }

    [Fact]
    public void CatalogFindHubSeparatedMode()
    {
        var fleet = Stock("fleet-find-hub");
        var eid = string.Concat(Enumerable.Repeat("11", 20));
        var rows = SignatureFieldDecoder.DecodeSighting(BleService("FEAA", "41" + eid + "00", fleet.Id), [fleet]);
        Assert.Equal("separated", rows.Display("mode"));
        Assert.Equal("11 11 11 11 11 11 11 11 11 11 11 11 11 11 11 11 11 11 11 11", rows.Display("eid"));
        var strong = Assert.Single(SignatureFieldDecoder.LiveChips(BleService("FEAA", "41" + eid + "00", fleet.Id), [fleet]));
        Assert.Equal("separated", strong.Text);
        Assert.True(strong.Emphasis);
        Assert.Contains("about a day", strong.Note);
        var quiet = Assert.Single(SignatureFieldDecoder.LiveChips(BleService("FEAA", "40" + eid + "00", fleet.Id), [fleet]));
        Assert.Equal("nearby", quiet.Text);
        Assert.False(quiet.Emphasis);
        Assert.Contains("own tag", quiet.Note);
        Assert.Contains("joined", quiet.Note);
    }

    [Fact]
    public void CatalogDultSeparatedAndNearOwner()
    {
        var fleet = Stock("fleet-dult");
        var separated = SignatureFieldDecoder.DecodeSighting(BleService("FCB2", "0100", fleet.Id), [fleet]);
        Assert.Equal("1", separated.Display("network_id"));
        Assert.Equal("separated", separated.Display("mode"));
        var strong = Assert.Single(SignatureFieldDecoder.LiveChips(BleService("FCB2", "0100", fleet.Id), [fleet]));
        Assert.Equal("separated", strong.Text);
        Assert.True(strong.Emphasis);
        Assert.Contains("about a day", strong.Note);
        var near = SignatureFieldDecoder.DecodeSighting(BleService("FCB2", "0201", fleet.Id), [fleet]);
        Assert.Equal("2", near.Display("network_id"));
        Assert.Equal("near owner", near.Display("mode"));
        var quiet = Assert.Single(SignatureFieldDecoder.LiveChips(BleService("FCB2", "0201", fleet.Id), [fleet]));
        Assert.Equal("near owner", quiet.Text);
        Assert.False(quiet.Emphasis);
        Assert.Contains("own tag", quiet.Note);
        Assert.Contains("joined", quiet.Note);
    }

    [Fact]
    public void CatalogBlueMaestroV23()
    {
        var fleet = Stock("fleet-bluemaestro");
        var rows = SignatureFieldDecoder.DecodeSighting(Ble(0x0133, "1764000A000100E001F4", fleet.Id), [fleet]);
        Assert.Equal("23", rows.Display("version"));
        Assert.Equal("100 %", rows.Display("battery"));
        Assert.Equal("22.4 °C", rows.Display("temperature"));
        Assert.Equal("50 %", rows.Display("humidity"));
    }

    [Fact]
    public void CatalogGoproHero12()
    {
        var fleet = Stock("fleet-gopro");
        var rows = SignatureFieldDecoder.DecodeSighting(Ble(0xF202, "02073E000000000000000001", fleet.Id), [fleet]);
        Assert.Equal("2", rows.Display("schema"));
        Assert.Equal("awake", rows.Display("awake"));
        Assert.Equal("on", rows.Display("wifi_ap"));
        Assert.Equal("yes", rows.Display("pairing"));
        Assert.Equal("HERO12 Black", rows.Display("model"));
    }

    [Fact]
    public void CatalogOsmoAction3Model()
    {
        var fleet = Stock("fleet-osmo");
        Assert.Equal("Osmo Action 3", SignatureFieldDecoder.DecodeSighting(Ble(0x08AA, "1200", fleet.Id), [fleet]).Display("model"));
    }

    [Fact]
    public void CatalogRuuviRawV1()
    {
        var fleet = Stock("fleet-ruuvi");
        var rows = SignatureFieldDecoder.DecodeSighting(Ble(0x0499, "03291A1ECE1EFC18F94202CA0B53", fleet.Id), [fleet]);
        Assert.Equal("3", rows.Display("format"));
        Assert.Equal("20.5 %", rows.Display("humidity"));
        Assert.Equal("1027.66 hPa", rows.Display("pressure"));
        Assert.Equal("-1 g", rows.Display("acc_x"));
        Assert.Equal("2899 mV", rows.Display("battery"));
    }

    [Fact]
    public void CatalogGoveeH5075Packed()
    {
        var fleet = Stock("fleet-govee");
        var rows = SignatureFieldDecoder.DecodeSighting(Ble(0xEC88, "0003215D64", fleet.Id), [fleet]);
        Assert.Equal("20.5149 °C", rows.Display("temperature"));
        Assert.Equal("14.9 %", rows.Display("humidity"));
        Assert.Equal("100 %", rows.Display("battery"));
    }

    [Fact]
    public void CatalogGoveeH5074()
    {
        var fleet = Stock("fleet-govee");
        var rows = SignatureFieldDecoder.DecodeSighting(Ble(0xEC88, "00580AE6116402", fleet.Id), [fleet]);
        Assert.Equal("26.48 °C", rows.Display("temperature"));
        Assert.Equal("45.82 %", rows.Display("humidity"));
        Assert.Equal("100 %", rows.Display("battery"));
    }

    [Fact]
    public void DecodedFractionsStayPeriodOnFrenchLocale()
    {
        var prev = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
        try
        {
            CatalogGoveeH5074();
        }
        finally
        {
            CultureInfo.CurrentCulture = prev;
        }
    }

    [Fact]
    public void CatalogGoveeH5102Packed()
    {
        var fleet = Stock("fleet-govee");
        var rows = SignatureFieldDecoder.DecodeSighting(Ble(0x0001, "010103215D64", fleet.Id), [fleet]);
        Assert.Equal("20.5149 °C", rows.Display("temperature"));
        Assert.Equal("14.9 %", rows.Display("humidity"));
        Assert.Equal("100 %", rows.Display("battery"));
    }

    [Fact]
    public void CatalogGoveeH5102EightByte()
    {
        var fleet = Stock("fleet-govee");
        var rows = SignatureFieldDecoder.DecodeSighting(Ble(0x0001, "010103215D640000", fleet.Id), [fleet]);
        Assert.Equal("20.5149 °C", rows.Display("temperature"));
        Assert.Equal("14.9 %", rows.Display("humidity"));
        Assert.Equal("100 %", rows.Display("battery"));
    }

    [Fact]
    public void CatalogGoveeStripsIntelliRocksSuffix()
    {
        var fleet = Stock("fleet-govee");
        var rocks = Convert.ToHexString(Encoding.ASCII.GetBytes("INTELLI_ROCKS"));
        var rows = SignatureFieldDecoder.DecodeSighting(Ble(0xEC88, "0003215D64" + rocks, fleet.Id), [fleet]);
        Assert.Equal("20.5149 °C", rows.Display("temperature"));
        Assert.Equal("100 %", rows.Display("battery"));
    }

    [Fact]
    public void CatalogGoveeSkipsUnrelatedMakerRecord()
    {
        var fleet = Stock("fleet-govee");
        var rows = SignatureFieldDecoder.DecodeSighting(Ble(0x004C, "0215AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA01020304C5", fleet.Id), [fleet]);
        Assert.Empty(rows);
    }

    [Fact]
    public void CatalogGoveePicksHygrometerAmongMakerRecords()
    {
        var fleet = Stock("fleet-govee");
        var device = Ble(0x004C, "0215AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA01020304C5", fleet.Id) with
        {
            Facts = new RadioFacts
            {
                MfgRecords = [new(0x004C, "0215AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA01020304C5"), new(0xEC88, "0003215D64")],
            },
        };
        Assert.Equal("20.5149 °C", SignatureFieldDecoder.DecodeSighting(device, [fleet]).Display("temperature"));
    }

    [Fact]
    public void CatalogKontaktLocation()
    {
        var fleet = Stock("fleet-kontakt");
        var rows = SignatureFieldDecoder.DecodeSighting(BleService("FE6A", "0764F4250A00", fleet.Id), [fleet]);
        Assert.Equal("Location", rows.Display("kind"));
        Assert.Equal("100 %", rows.Display("battery"));
        Assert.Equal("still", rows.Display("moving"));
    }

    [Fact]
    public void CatalogNestWeaveProtect2()
    {
        var fleet = Stock("fleet-nest-weave");
        Assert.Equal("Nest Protect (2nd gen)", SignatureFieldDecoder.DecodeSighting(BleService("FEAF", "0900", fleet.Id), [fleet]).Display("product"));
    }

    [Fact]
    public void CatalogNestWeaveIdentificationBlock()
    {
        var fleet = Stock("fleet-nest-weave");
        // OpenWeave WeaveBLEDeviceIdentificationInfo: len 0x10, type 0x01,
        // v0.1, vendor 0x235A, product 0x0009 Protect 2nd gen, device id, paired.
        const string payload = "100100015A230900010203040506070801";
        var rows = SignatureFieldDecoder.DecodeSighting(BleService("FEAF", payload, fleet.Id), [fleet]);
        Assert.Equal("Nest Labs", rows.Display("vendor"));
        Assert.Equal("Nest Protect (2nd gen)", rows.Display("product"));
        Assert.Equal("01 02 03 04 05 06 07 08", rows.Display("device_id"));
        Assert.Equal("paired", rows.Display("pairing"));
        Assert.DoesNotContain(rows, r => r.Id == "product" && r.Display.Contains("272"));
    }

    [Fact]
    public void CatalogTuyaBound()
    {
        var fleet = Stock("fleet-tuya");
        var rows = SignatureFieldDecoder.DecodeSighting(Ble(0x07D0, "8004", fleet.Id), [fleet]);
        Assert.Equal("bound", rows.Display("bound"));
        Assert.Equal("4", rows.Display("protocol"));
    }

    [Fact]
    public void CatalogTilePrivateId()
    {
        var fleet = Stock("fleet-tile");
        var rows = SignatureFieldDecoder.DecodeSighting(BleService("FEED", "0102030405060708", fleet.Id), [fleet]);
        Assert.Equal("01 02 03 04 05 06 07 08", rows.Display("private_id"));
    }

    [Fact]
    public void CatalogEstimoteTelemetryFrame()
    {
        var fleet = Stock("fleet-estimote");
        Assert.Equal("Telemetry", SignatureFieldDecoder.DecodeSighting(Ble(0x015D, "02ABCD", fleet.Id), [fleet]).Display("frame"));
    }

    [Fact]
    public void CatalogHasDecodeOnPublishedLayoutsOnly()
    {
        var byId = StockSignatures.Fleets.ToDictionary(f => f.Id);
        foreach (var id in new[]
                 {
                     "fleet-ruuvi", "fleet-remote-id", "fleet-bluemaestro", "fleet-gopro", "fleet-osmo", "fleet-dji",
                     "fleet-govee", "fleet-kontakt", "fleet-estimote", "fleet-nest-weave", "fleet-tuya", "fleet-tile",
                     "fleet-tpms-ble", "fleet-sytpms", "fleet-tesla-tstpms",
                 })
        {
            Assert.NotNull(byId[id].Decode);
        }
        foreach (var id in new[] { "fleet-fitbit", "fleet-airtag", "fleet-goodyear", "fleet-fobo", "fleet-tirecheck" })
        {
            Assert.Null(byId[id].Decode);
        }
    }

    [Fact]
    public void CatalogAftermarketTpmsPressureTempBattery()
    {
        var fleet = Stock("fleet-tpms-ble");
        var rows = SignatureFieldDecoder.DecodeSighting(Ble(0x0001, "80EACA108A78E36D0000E60A00005B00", fleet.Id), [fleet]);
        Assert.Equal("1", rows.Display("wheel"));
        Assert.Equal("EA CA 10 8A 78", rows.Display("sensor_id"));
        Assert.Equal("28.131 kPa", rows.Display("pressure"));
        Assert.Equal("27.9 °C", rows.Display("temperature"));
        Assert.Equal("91 %", rows.Display("battery"));
        Assert.Equal("ok", rows.Display("alarm"));
    }

    [Fact]
    public void CatalogSytpmsIncludesCompanyIdBytes()
    {
        var fleet = Stock("fleet-sytpms");
        var rows = SignatureFieldDecoder.DecodeSighting(Ble(0x1E28, "1401558536", fleet.Id), [fleet]);
        Assert.Equal("ok", rows.Display("alarm"));
        Assert.Equal("no", rows.Display("rotating"));
        Assert.Equal("yes", rows.Display("still"));
        Assert.Equal("3 V", rows.Display("battery"));
        Assert.Equal("20 °C", rows.Display("temperature"));
        Assert.Equal("19.6 psi", rows.Display("pressure"));
    }

    [Fact]
    public void CatalogTeslaTstpmsAwakePressure()
    {
        var fleet = Stock("fleet-tesla-tstpms");
        var rows = SignatureFieldDecoder.DecodeSighting(Ble(0x022B, "0000058A0147B80B", fleet.Id), [fleet]);
        Assert.Equal("5", rows.Display("mode"));
        Assert.Equal("42 psi", rows.Display("pressure"));
        Assert.Equal("70 °F", rows.Display("temperature"));
        Assert.Equal("3000 mV", rows.Display("battery"));
    }

    [Fact]
    public void CatalogTeslaTstpmsSleepSkipsSensors()
    {
        var fleet = Stock("fleet-tesla-tstpms");
        var rows = SignatureFieldDecoder.DecodeSighting(Ble(0x022B, "0000008A0147B80B", fleet.Id), [fleet]);
        Assert.Equal("sleep", rows.Display("mode"));
        Assert.DoesNotContain(rows, r => r.Id == "pressure");
        Assert.DoesNotContain(rows, r => r.Id == "temperature");
        Assert.DoesNotContain(rows, r => r.Id == "battery");
    }

    private static Fleet Manufacturer(DecodeField field) => new()
    {
        Id = "f",
        Name = "P",
        Decode = new FleetDecode { Source = DecodeSource.ManufacturerData, Fields = [field] },
    };

    private static Sighting WifiRid(string dataHex, string fleetId) => new()
    {
        Key = "WIFI:60:60:1F:06:31:08",
        Kind = RadioKind.Wifi,
        Mac = "60:60:1F:06:31:08",
        Name = "RID-1581F3YTDJ1D0031Z530",
        Rssi = -80,
        RssiMin = -80,
        RssiMax = -80,
        Channel = 6,
        FrequencyMhz = 2437,
        FirstSeen = 1,
        LastSeen = 1,
        HitCount = 1,
        FleetIds = [fleetId],
        Facts = new RadioFacts { VendorIes = [new("FA:0B:BC", 0x0D, dataHex)] },
    };

    private static Sighting BleService(string uuid, string dataHex, string fleetId) => new()
    {
        Key = "BLE:AA:BB:CC:DD:EE:01",
        Kind = RadioKind.Ble,
        Mac = "AA:BB:CC:DD:EE:01",
        Rssi = -50,
        RssiMin = -50,
        RssiMax = -50,
        FrequencyMhz = 2402,
        Randomized = true,
        ServiceUuids = [uuid],
        FirstSeen = 1,
        LastSeen = 1,
        HitCount = 1,
        FleetIds = [fleetId],
        Facts = new RadioFacts { ServiceData = [new(uuid, dataHex)] },
    };

    private static Sighting Ble(int companyId, string dataHex, string fleetId) => new()
    {
        Key = "BLE:AA:BB:CC:DD:EE:01",
        Kind = RadioKind.Ble,
        Mac = "AA:BB:CC:DD:EE:01",
        Rssi = -50,
        RssiMin = -50,
        RssiMax = -50,
        FrequencyMhz = 2402,
        Randomized = true,
        ManufacturerId = companyId,
        ManufacturerDataHex = dataHex,
        FirstSeen = 1,
        LastSeen = 1,
        HitCount = 1,
        FleetIds = [fleetId],
        Facts = new RadioFacts { MfgRecords = [new(companyId, dataHex)] },
    };
}

internal static class DecodedRows
{
    public static string Display(this IReadOnlyList<DecodedFieldValue> rows, string id) => rows.First(r => r.Id == id).Display;

    public static double? Number(this IReadOnlyList<DecodedFieldValue> rows, string id) => rows.First(r => r.Id == id).Number;
}

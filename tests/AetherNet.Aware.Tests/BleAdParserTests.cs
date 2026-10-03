// SPDX-License-Identifier: MIT
// Ported from Fieldwatch app/src/test/java/app/fieldwatch/radio/BleAdParserTest.kt (cf6562d).
// Copyright (c) 2026 Off Grid Pete LLC. See src/AetherNet.Aware/NOTICE.md.

using System.Text;
using Xunit;

namespace AetherNet.Aware.Tests;

public class BleAdParserTests
{
    /// <summary>AD structure: [len][type][data]. len covers type + data.</summary>
    private static byte[] Field(int type, params int[] data) =>
        [(byte)(data.Length + 1), (byte)type, .. data.Select(b => (byte)b)];

    private static byte[] NameField(int type, string s) =>
        [(byte)(s.Length + 1), (byte)type, .. Encoding.UTF8.GetBytes(s)];

    private static byte[] Join(params byte[][] parts) => parts.SelectMany(p => p).ToArray();

    [Fact]
    public void NullAndEmptyGiveEmptyParsed()
    {
        var none = BleAdParser.Parse(null);
        Assert.Null(none.LocalName);
        Assert.Empty(none.Mfg);
        Assert.Empty(BleAdParser.Parse([]).Uuids);
    }

    [Fact]
    public void FlagsNameTxAppearanceInterval()
    {
        var bytes = Join(
            Field(0x01, 0x06),
            NameField(0x09, "Pixel Buds"),
            Field(0x0A, 0xF8),
            Field(0x19, 0x41, 0x03),
            Field(0x1A, 0xA0, 0x00));
        var p = BleAdParser.Parse(bytes);
        Assert.Equal(0x06, p.Flags);
        Assert.Equal("Pixel Buds", p.LocalName);
        Assert.Equal(-8, p.TxPower);
        Assert.Equal(0x0341, p.Appearance);
        Assert.Equal(100.0, p.AdvertisingIntervalMs!.Value, 0.001);
    }

    [Fact]
    public void CompleteNameBeatsShortenedEitherOrder()
    {
        var shortName = NameField(0x08, "FW");
        var full = NameField(0x09, "Fieldwatch");
        Assert.Equal("Fieldwatch", BleAdParser.Parse(Join(shortName, full)).LocalName);
        Assert.Equal("Fieldwatch", BleAdParser.Parse(Join(full, shortName)).LocalName);
        Assert.Equal("FW", BleAdParser.Parse(shortName).LocalName);
    }

    [Fact]
    public void NameControlBytesBecomeSpaces()
    {
        // 0x07 (BEL) inside a local name must not inject control chars into the UI.
        byte[] raw = [4, 0x09, (byte)'A', 0x07, (byte)'B'];
        Assert.Equal("A B", BleAdParser.Parse(raw).LocalName);
    }

    [Fact]
    public void Uuid16ListIsBigEndian()
    {
        // On-air order is little-endian; parsed form is the SIG-assigned hex.
        var p = BleAdParser.Parse(Field(0x03, 0xAA, 0xFE, 0x0D, 0x18));
        Assert.Equal(["FEAA", "180D"], p.Uuids);
    }

    [Fact]
    public void Uuid128IsByteReversed()
    {
        var le = Enumerable.Range(0x00, 16).Select(i => (byte)i).ToArray();
        var p = BleAdParser.Parse([17, 0x07, .. le]);
        Assert.Equal(["0F0E0D0C-0B0A-0908-0706-050403020100"], p.Uuids);
    }

    [Fact]
    public void ServiceData16AndManufacturerData()
    {
        var bytes = Join(
            Field(0x16, 0xAA, 0xFE, 0x01, 0x02),
            Field(0xFF, 0x4C, 0x00, 0x02, 0x15, 0xAB));
        var p = BleAdParser.Parse(bytes);
        Assert.Single(p.ServiceData);
        Assert.Equal("FEAA", p.ServiceData[0].Uuid);
        Assert.Equal("0102", p.ServiceData[0].DataHex);
        Assert.Single(p.Mfg);
        Assert.Equal(0x004C, p.Mfg[0].CompanyId);
        Assert.Equal("0215AB", p.Mfg[0].DataHex);
    }

    [Fact]
    public void DeviceClassIsLittleEndian24()
    {
        var p = BleAdParser.Parse(Field(0x0D, 0x0C, 0x05, 0x1F));
        Assert.Equal(0x1F050C, p.DeviceClass);
    }

    [Fact]
    public void TruncatedFieldStopsButKeepsEarlier()
    {
        var good = Field(0x01, 0x06);
        byte[] bad = [0x40, 0x09, (byte)'X']; // len says 64, only 2 bytes follow
        var p = BleAdParser.Parse(Join(good, bad));
        Assert.Equal(0x06, p.Flags);
        Assert.Null(p.LocalName);
    }

    [Fact]
    public void ZeroLengthFieldTerminates()
    {
        var p = BleAdParser.Parse(Join(Field(0x01, 0x1E), [0x00], NameField(0x09, "late")));
        Assert.Equal(0x1E, p.Flags);
        Assert.Null(p.LocalName);
    }

    [Fact]
    public void FlagsLabelText()
    {
        Assert.Equal("LE General Discoverable, BR/EDR not supported", BleAdParser.FlagsLabel(0x06));
        Assert.Equal("0x00", BleAdParser.FlagsLabel(0x00));
    }
}

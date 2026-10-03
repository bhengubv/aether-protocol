// SPDX-License-Identifier: MIT
// Ported from Fieldwatch app/src/test/java/app/fieldwatch/domain/OpenDroneIdTest.kt (cf6562d).
// Copyright (c) 2026 Off Grid Pete LLC. See src/AetherNet.Aware/NOTICE.md.

using Xunit;

namespace AetherNet.Aware.Tests;

public class OpenDroneIdTests
{
    [Fact]
    public void BleLocationHeadingSpeedAndEw()
    {
        var loc = OpenDroneId.FromFacts(new RadioFacts
        {
            ServiceData = [new("FFFA", BleWrap(LocationMsg(dir: 90, ew: false, speed: 40)))],
        });
        Assert.Equal(40.0, loc.Lat!.Value, 1e-6);
        Assert.Equal(-74.0, loc.Lon!.Value, 1e-6);
        Assert.Equal(90.0, loc.HeadingDeg!.Value, 1e-6);
        Assert.Equal(10.0, loc.SpeedMps!.Value, 1e-6);
        Assert.Equal(2.0, loc.VspeedMps!.Value, 1e-6);
        var west = OpenDroneId.FromFacts(new RadioFacts
        {
            ServiceData = [new("FFFA", BleWrap(LocationMsg(dir: 90, ew: true, speed: 40)))],
        });
        Assert.Equal(270.0, west.HeadingDeg!.Value, 1e-6);
    }

    [Fact]
    public void WifiVendorIeLocation()
    {
        var ie = new VendorIeRecord("FA:0B:BC", 0x0D, "00" + Convert.ToHexString(LocationMsg(dir: 45, ew: false, speed: 8)));
        var loc = OpenDroneId.FromFacts(new RadioFacts { VendorIes = [ie] });
        Assert.Equal(40.0, loc.Lat!.Value, 1e-6);
        Assert.Equal(45.0, loc.HeadingDeg!.Value, 1e-6);
        Assert.Equal(2.0, loc.SpeedMps!.Value, 1e-6);
    }

    [Fact]
    public void UnknownDirectionIsNull()
    {
        var loc = OpenDroneId.ParseMessages([LocationMsg(dir: 255, ew: false, speed: 40)]);
        Assert.Null(loc.HeadingDeg);
    }

    [Fact]
    public void WifiMessagePackDecodesIdLocationAndOperator()
    {
        // DJI RID-1581F3… FA:0B:BC type 13 pack from A54 28 Sep 2026.
        const string hex = "D9F2190302123135383146335954444A3144303033315A353330000000" +
            "1220820A00864228110CFF80CF0000F508B2083A022E310A00" +
            "420176E42711B5FF81CF010000000000000005088B02900E00";
        var loc = OpenDroneId.FromFacts(new RadioFacts { VendorIes = [new("FA:0B:BC", 0x0D, hex)] });
        Assert.Equal("1581F3YTDJ1D0031Z530", loc.UasId);
        Assert.Equal(28.7851142, loc.Lat!.Value, 1e-6);
        Assert.Equal(-81.3629684, loc.Lon!.Value, 1e-6);
        Assert.Equal(130.0, loc.HeadingDeg!.Value, 1e-6);
        Assert.Equal(2.5, loc.SpeedMps!.Value, 1e-6);
        Assert.Equal(28.7827062, loc.OpLat!.Value, 1e-6);
        Assert.Equal(-81.3563979, loc.OpLon!.Value, 1e-6);
    }

    private static string BleWrap(byte[] msg) => "0D00" + Convert.ToHexString(msg);

    internal static byte[] LocationMsg(int dir, bool ew, int speed)
    {
        var flags = 0x20 | (ew ? 0x02 : 0);
        return
        [
            0x12, (byte)flags, (byte)dir, (byte)speed, 4,
            .. Le32(400_000_000), .. Le32(-740_000_000), .. Le16(0), .. Le16(2200), .. Le16(2100), .. new byte[6],
        ];
    }

    internal static byte[] Le32(int n) => [(byte)(n & 0xFF), (byte)((n >> 8) & 0xFF), (byte)((n >> 16) & 0xFF), (byte)((n >> 24) & 0xFF)];

    internal static byte[] Le16(int n) => [(byte)(n & 0xFF), (byte)((n >> 8) & 0xFF)];
}

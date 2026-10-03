// SPDX-License-Identifier: MIT
// Ported from Fieldwatch app/src/test/java/app/fieldwatch/radio/WifiIeParserTest.kt (cf6562d).
// Copyright (c) 2026 Off Grid Pete LLC. See src/AetherNet.Aware/NOTICE.md.

using Xunit;

namespace AetherNet.Aware.Tests;

public class WifiIeParserTests
{
    private static WifiIeParser.Ie Ie(int id, params int[] data) =>
        new(id, data.Select(b => (byte)b).ToArray());

    private static readonly WifiIeParser.Ie Wpa2CcmpPsk = Ie(
        48,
        0x01, 0x00, // version 1
        0x00, 0x0F, 0xAC, 0x04, // group CCMP
        0x01, 0x00, // 1 pairwise
        0x00, 0x0F, 0xAC, 0x04, // CCMP
        0x01, 0x00, // 1 AKM
        0x00, 0x0F, 0xAC, 0x02); // PSK

    [Fact]
    public void RsnWpa2PskCcmp()
    {
        var p = WifiIeParser.ParseIes([Wpa2CcmpPsk], "[WPA2-PSK-CCMP][ESS]");
        Assert.Equal("RSN PSK CCMP (group CCMP)", p.Security);
    }

    [Fact]
    public void RsnWpa3Sae()
    {
        var ie = Ie(
            48,
            0x01, 0x00,
            0x00, 0x0F, 0xAC, 0x04,
            0x01, 0x00,
            0x00, 0x0F, 0xAC, 0x04,
            0x01, 0x00,
            0x00, 0x0F, 0xAC, 0x08); // SAE
        Assert.Equal("RSN SAE CCMP (group CCMP)", WifiIeParser.ParseIes([ie], "").Security);
    }

    [Fact]
    public void RsnTransitionModeListsBothAkms()
    {
        var ie = Ie(
            48,
            0x01, 0x00,
            0x00, 0x0F, 0xAC, 0x04,
            0x01, 0x00,
            0x00, 0x0F, 0xAC, 0x04,
            0x02, 0x00,
            0x00, 0x0F, 0xAC, 0x02, // PSK
            0x00, 0x0F, 0xAC, 0x08); // SAE
        Assert.Equal("RSN PSK/SAE CCMP (group CCMP)", WifiIeParser.ParseIes([ie], "").Security);
    }

    [Fact]
    public void Wpa1VendorIeSummaryAndOui()
    {
        var wpa = Ie(
            221,
            0x00, 0x50, 0xF2, 0x01, // Microsoft OUI, WPA type
            0x01, 0x00,
            0x00, 0x50, 0xF2, 0x02, // group TKIP
            0x01, 0x00,
            0x00, 0x50, 0xF2, 0x02, // pairwise TKIP
            0x01, 0x00,
            0x00, 0x50, 0xF2, 0x02); // akm
        var p = WifiIeParser.ParseIes([wpa], "");
        Assert.Equal("WPA TKIP", p.Security);
        Assert.Contains(p.VendorIes, v => v.Oui == "00:50:F2" && v.Type == 1);
    }

    [Fact]
    public void VendorIeExtractsOuiTypeAndPayload()
    {
        var p = WifiIeParser.ParseIes([Ie(221, 0x00, 0x17, 0xF2, 0x0A, 0x01, 0x02)], "");
        Assert.Single(p.VendorIes);
        Assert.Equal("00:17:F2", p.VendorIes[0].Oui);
        Assert.Equal(0x0A, p.VendorIes[0].Type);
        Assert.Equal("0102", p.VendorIes[0].DataHex);
    }

    [Fact]
    public void RatesDecodeBasicFlagAndHalfMbps()
    {
        var p = WifiIeParser.ParseIes([Ie(1, 0x82, 0x84, 0x8B, 0x96, 0x0C, 0x12, 0x18, 0x24)], "");
        Assert.Equal("1* 2* 5.5* 11* 6 9 12 18", p.Rates);
    }

    [Fact]
    public void DsParameterSetsChannel()
    {
        var p = WifiIeParser.ParseIes([Ie(3, 0x06)], "");
        Assert.Equal(6, p.ChannelFromDs);
    }

    [Fact]
    public void CapabilitiesFallbackWhenNoSecurityIe()
    {
        Assert.Equal("[WPA2-PSK-CCMP][ESS]", WifiIeParser.ParseIes([], "[WPA2-PSK-CCMP][ESS]").Security);
        // A truncated RSN body must not shadow the capability string.
        var shortRsn = Ie(48, 0x01, 0x00, 0x00, 0x0F);
        Assert.Equal("[WPA2-PSK-CCMP][ESS]", WifiIeParser.ParseIes([shortRsn], "[WPA2-PSK-CCMP][ESS]").Security);
    }

    [Fact]
    public void VendorIeKeepsOpenDroneIdSizedPayload()
    {
        int[] body = [0xFA, 0x0B, 0xBC, 0x0D, .. Enumerable.Range(0, 30)];
        var p = WifiIeParser.ParseIes([Ie(221, body)], "");
        var ie = Assert.Single(p.VendorIes);
        Assert.Equal("FA:0B:BC", ie.Oui);
        Assert.Equal(0x0D, ie.Type);
        Assert.Equal(60, ie.DataHex.Length);
    }

    [Fact]
    public void NoIesNoCapabilitiesGivesNullSecurity()
    {
        Assert.Null(WifiIeParser.ParseIes([], null).Security);
        Assert.Null(WifiIeParser.ParseIes([], "").Security);
    }
}

// SPDX-License-Identifier: MIT
// Ported from Fieldwatch app/src/main/java/app/fieldwatch/radio/WifiIeParser.kt (github.com/offgridpete/fieldwatch, cf6562d).
// The platform-neutral part: reading a beacon's information elements. Pulling them out of an Android ScanResult
// belongs to the host that owns the radio.
// Copyright (c) 2026 Off Grid Pete LLC. See NOTICE.md.

using System.Globalization;

namespace AetherNet.Aware;

/// <summary>Reads a Wi-Fi access point's information elements: rates, security, vendor elements, channel.</summary>
public static class WifiIeParser
{
    private static readonly HashSet<string> CipherOuis = ["000FAC", "0050F2"];

    /// <summary>What one beacon said.</summary>
    public sealed record Parsed
    {
        /// <summary>Supported rates in Mb/s, basic rates marked with *: "1* 2* 5.5* 11* 6 9 12 18".</summary>
        public string? Rates { get; init; }

        /// <summary>"RSN PSK CCMP (group CCMP)", "WPA TKIP", or the platform's capability string when no element says.</summary>
        public string? Security { get; init; }

        public IReadOnlyList<VendorIeRecord> VendorIes { get; init; } = [];

        public int? ChannelFromDs { get; init; }
    }

    /// <summary>One raw information element: its id and payload.</summary>
    public sealed record Ie(int Id, byte[] Bytes);

    public static Parsed ParseIes(IReadOnlyList<Ie> ies, string? capabilities)
    {
        ArgumentNullException.ThrowIfNull(ies);
        var rates = new List<string>(16);
        var vendor = new List<VendorIeRecord>(4);
        var security = new List<string>(4);
        int? ds = null;
        foreach (var ie in ies)
        {
            var bytes = ie.Bytes;
            switch (ie.Id)
            {
                case 1 or 50:
                    DecodeRates(bytes, rates);
                    break;
                case 3:
                    if (bytes.Length > 0)
                    {
                        ds = bytes[0];
                    }
                    break;
                case 48:
                    if (RsnSummary(bytes) is { } rsn)
                    {
                        security.Add(rsn);
                    }
                    break;
                case 221:
                    if (bytes.Length >= 3)
                    {
                        var oui = $"{bytes[0]:X2}:{bytes[1]:X2}:{bytes[2]:X2}";
                        var type = bytes.Length > 3 ? bytes[3] : 0;
                        var payload = bytes.Length > 4 ? bytes.AsSpan(4, Math.Min(bytes.Length, 4 + 200) - 4) : [];
                        vendor.Add(new VendorIeRecord(oui, type, Strings.Hex(payload)));
                        if (oui == "00:50:F2" && type == 1 && WpaSummary(bytes) is { } wpa)
                        {
                            security.Add(wpa);
                        }
                    }
                    break;
            }
        }
        var cap = capabilities ?? "";
        if (security.Count == 0 && !Strings.IsBlank(cap))
        {
            security.Add(cap);
        }
        var rateText = string.Join(' ', rates.Distinct());
        var securityText = string.Join(" · ", security.Distinct());
        return new Parsed
        {
            Rates = Strings.IsBlank(rateText) ? null : rateText,
            Security = !Strings.IsBlank(securityText) ? securityText : Strings.IsBlank(cap) ? null : cap,
            VendorIes = vendor.DistinctBy(v => (v.Oui, v.Type)).Take(12).ToList(),
            ChannelFromDs = ds,
        };
    }

    private static void DecodeRates(byte[] bytes, List<string> into)
    {
        foreach (var b in bytes)
        {
            var basic = (b & 0x80) != 0;
            var mbps = (b & 0x7F) / 2.0;
            var label = mbps == Math.Floor(mbps)
                ? ((int)mbps).ToString(CultureInfo.InvariantCulture)
                : mbps.ToString(CultureInfo.InvariantCulture);
            into.Add(basic ? label + "*" : label);
        }
    }

    private static string? RsnSummary(byte[] bytes)
    {
        if (bytes.Length < 8)
        {
            return null;
        }
        var o = 0;
        var version = U16(bytes, o);
        o += 2;
        if (version != 1)
        {
            return Inv($"RSN v{version}");
        }
        var group = CipherSuite(bytes, o);
        o += 4;
        if (o + 2 > bytes.Length)
        {
            return "RSN " + group;
        }
        var pairwiseCount = U16(bytes, o);
        o += 2;
        var pairwise = new List<string>();
        for (var n = 0; n < pairwiseCount && o + 4 <= bytes.Length; n++, o += 4)
        {
            pairwise.Add(CipherSuite(bytes, o));
        }
        if (o + 2 > bytes.Length)
        {
            return $"RSN {group} / {string.Join(',', pairwise)}";
        }
        var akmCount = U16(bytes, o);
        o += 2;
        var akm = new List<string>();
        for (var n = 0; n < akmCount && o + 4 <= bytes.Length; n++, o += 4)
        {
            akm.Add(AkmSuite(bytes, o));
        }
        var akmText = string.Join('/', akm);
        var pairwiseText = string.Join('/', pairwise);
        var summary = "RSN " + (Strings.IsBlank(akmText) ? "AKM?" : akmText) + " " + (Strings.IsBlank(pairwiseText) ? group : pairwiseText);
        return Strings.IsBlank(group) ? summary : $"{summary} (group {group})";
    }

    private static string? WpaSummary(byte[] bytes)
    {
        if (bytes.Length < 10)
        {
            return "WPA";
        }
        var o = 4;
        var version = U16(bytes, o);
        o += 2;
        if (version != 1)
        {
            return Inv($"WPA v{version}");
        }
        var group = CipherSuite(bytes, o);
        o += 4;
        if (o + 2 > bytes.Length)
        {
            return "WPA " + group;
        }
        var pairwiseCount = U16(bytes, o);
        o += 2;
        var pairwise = new List<string>();
        for (var n = 0; n < pairwiseCount && o + 4 <= bytes.Length; n++, o += 4)
        {
            pairwise.Add(CipherSuite(bytes, o));
        }
        var pairwiseText = string.Join('/', pairwise);
        return "WPA " + (Strings.IsBlank(pairwiseText) ? group : pairwiseText);
    }

    private static string CipherSuite(byte[] bytes, int offset)
    {
        if (OuiType(bytes, offset) is not { } suite)
        {
            return "?";
        }
        var (oui, type) = suite;
        if (CipherOuis.Contains(oui))
        {
            return type switch
            {
                0 => "Group",
                1 => "WEP-40",
                2 => "TKIP",
                4 => "CCMP",
                5 => "WEP-104",
                6 => "BIP",
                8 => "GCMP",
                9 => "GCMP-256",
                10 => "CCMP-256",
                _ => Inv($"cipher {type}"),
            };
        }
        return Inv($"{oui}/{type}");
    }

    private static string AkmSuite(byte[] bytes, int offset)
    {
        if (OuiType(bytes, offset) is not { } suite)
        {
            return "?";
        }
        var (oui, type) = suite;
        if (oui == "000FAC")
        {
            return type switch
            {
                1 => "802.1X",
                2 => "PSK",
                3 => "FT-802.1X",
                4 => "FT-PSK",
                5 => "802.1X-SHA256",
                6 => "PSK-SHA256",
                8 => "SAE",
                9 => "FT-SAE",
                11 => "SUITE-B",
                12 => "SUITE-B-192",
                18 => "OWE",
                _ => Inv($"AKM {type}"),
            };
        }
        return Inv($"{oui}/{type}");
    }

    private static (string Oui, int Type)? OuiType(byte[] bytes, int offset)
    {
        if (offset + 4 > bytes.Length)
        {
            return null;
        }
        return ($"{bytes[offset]:X2}{bytes[offset + 1]:X2}{bytes[offset + 2]:X2}", bytes[offset + 3]);
    }

    private static int U16(byte[] bytes, int offset) =>
        offset + 1 >= bytes.Length ? 0 : bytes[offset] | (bytes[offset + 1] << 8);

    private static string Inv(FormattableString s) => FormattableString.Invariant(s);
}

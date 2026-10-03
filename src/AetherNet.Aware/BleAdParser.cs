// SPDX-License-Identifier: MIT
// Ported from Fieldwatch app/src/main/java/app/fieldwatch/radio/BleAdParser.kt (github.com/offgridpete/fieldwatch, cf6562d).
// The platform-neutral part: reading the advert's bytes. Turning an Android ScanResult into RadioFacts belongs to
// the host that owns the radio.
// Copyright (c) 2026 Off Grid Pete LLC. See NOTICE.md.

using System.Globalization;
using System.Text;

namespace AetherNet.Aware;

/// <summary>Reads a Bluetooth Low Energy advert (its AD structures) into fields.</summary>
public static class BleAdParser
{
    /// <summary>What one advert said.</summary>
    public sealed record Parsed
    {
        public int? Flags { get; init; }

        public int? TxPower { get; init; }

        public int? Appearance { get; init; }

        public double? AdvertisingIntervalMs { get; init; }

        public int? DeviceClass { get; init; }

        public string? LocalName { get; init; }

        public IReadOnlyList<MfgRecord> Mfg { get; init; } = [];

        public IReadOnlyList<ServiceDataRecord> ServiceData { get; init; } = [];

        public IReadOnlyList<string> Uuids { get; init; } = [];
    }

    /// <summary>
    /// Reads AD structures ([length][type][data], length covering type and data) until a zero length or the end. A
    /// field that runs past the end stops the read and keeps what came before it.
    /// </summary>
    public static Parsed Parse(byte[]? bytes)
    {
        if (bytes is null || bytes.Length == 0)
        {
            return new Parsed();
        }
        int? flags = null;
        int? txPower = null;
        int? appearance = null;
        double? interval = null;
        int? deviceClass = null;
        string? localName = null;
        var mfg = new List<MfgRecord>(2);
        var serviceData = new List<ServiceDataRecord>(2);
        var uuids = new List<string>(4);
        var i = 0;
        while (i < bytes.Length)
        {
            var len = bytes[i];
            if (len == 0 || i + len >= bytes.Length)
            {
                break;
            }
            var type = bytes[i + 1];
            var data = bytes.AsSpan((i + 2)..(i + 1 + len));
            switch (type)
            {
                case 0x01:
                    if (data.Length > 0)
                    {
                        flags = data[0];
                    }
                    break;
                case 0x02 or 0x03 or 0x14:
                    Uuid16List(data, uuids);
                    break;
                case 0x04 or 0x05 or 0x1F:
                    Uuid32List(data, uuids);
                    break;
                case 0x06 or 0x07 or 0x15:
                    Uuid128List(data, uuids);
                    break;
                case 0x08 or 0x09:
                    var name = Utf8Name(data);
                    if (name.Length > 0 && (type == 0x09 || localName is null))
                    {
                        localName = name;
                    }
                    break;
                case 0x0A:
                    if (data.Length > 0)
                    {
                        txPower = (sbyte)data[0];
                    }
                    break;
                case 0x0D:
                    if (data.Length >= 3)
                    {
                        deviceClass = data[0] | (data[1] << 8) | (data[2] << 16);
                    }
                    break;
                case 0x16:
                    if (data.Length >= 2)
                    {
                        serviceData.Add(new ServiceDataRecord(Hex2(data[1], data[0]), Strings.Hex(data[2..])));
                    }
                    break;
                case 0x19:
                    if (data.Length >= 2)
                    {
                        appearance = data[0] | (data[1] << 8);
                    }
                    break;
                case 0x1A:
                    if (data.Length >= 2)
                    {
                        interval = (data[0] | (data[1] << 8)) * 0.625;
                    }
                    break;
                case 0x20:
                    if (data.Length >= 4)
                    {
                        var uuid = Hex2(data[3], data[2]) + Hex2(data[1], data[0]);
                        serviceData.Add(new ServiceDataRecord(uuid, Strings.Hex(data[4..])));
                    }
                    break;
                case 0x21:
                    if (data.Length >= 16)
                    {
                        serviceData.Add(new ServiceDataRecord(UuidFromLe(data[..16]), Strings.Hex(data[16..])));
                    }
                    break;
                case 0xFF:
                    if (data.Length >= 2)
                    {
                        mfg.Add(new MfgRecord(data[0] | (data[1] << 8), Strings.Hex(data[2..])));
                    }
                    break;
            }
            i += len + 1;
        }
        return new Parsed
        {
            Flags = flags,
            TxPower = txPower,
            Appearance = appearance,
            AdvertisingIntervalMs = interval,
            DeviceClass = deviceClass,
            LocalName = localName,
            Mfg = mfg,
            ServiceData = serviceData,
            Uuids = uuids.Distinct().ToList(),
        };
    }

    /// <summary>The advert flags in words: "LE General Discoverable, BR/EDR not supported".</summary>
    public static string FlagsLabel(int flags)
    {
        var parts = new List<string>(5);
        if ((flags & 0x01) != 0)
        {
            parts.Add("LE Limited Discoverable");
        }
        if ((flags & 0x02) != 0)
        {
            parts.Add("LE General Discoverable");
        }
        if ((flags & 0x04) != 0)
        {
            parts.Add("BR/EDR not supported");
        }
        if ((flags & 0x08) != 0)
        {
            parts.Add("Simultaneous LE + BR/EDR (Controller)");
        }
        if ((flags & 0x10) != 0)
        {
            parts.Add("Simultaneous LE + BR/EDR (Host)");
        }
        if (parts.Count == 0)
        {
            parts.Add("0x" + flags.ToString("X2", CultureInfo.InvariantCulture));
        }
        return string.Join(", ", parts);
    }

    private static string Hex2(byte a, byte b) => Strings.Hex([a, b]);

    private static string Utf8Name(ReadOnlySpan<byte> data)
    {
        if (data.Length == 0)
        {
            return "";
        }
        var text = Encoding.UTF8.GetString(data).Trim().Trim('\u0000');
        var sb = new StringBuilder(text.Length);
        foreach (var ch in text)
        {
            sb.Append(char.IsControl(ch) ? ' ' : ch);
        }
        return Strings.Take(sb.ToString().Trim(), 48);
    }

    private static void Uuid16List(ReadOnlySpan<byte> data, List<string> into)
    {
        for (var i = 0; i + 2 <= data.Length; i += 2)
        {
            into.Add(Hex2(data[i + 1], data[i]));
        }
    }

    private static void Uuid32List(ReadOnlySpan<byte> data, List<string> into)
    {
        for (var i = 0; i + 4 <= data.Length; i += 4)
        {
            into.Add(Hex2(data[i + 3], data[i + 2]) + Hex2(data[i + 1], data[i]));
        }
    }

    private static void Uuid128List(ReadOnlySpan<byte> data, List<string> into)
    {
        for (var i = 0; i + 16 <= data.Length; i += 16)
        {
            into.Add(UuidFromLe(data.Slice(i, 16)));
        }
    }

    /// <summary>A 128-bit UUID sent little-endian, written the usual way: "0F0E0D0C-0B0A-0908-0706-050403020100".</summary>
    private static string UuidFromLe(ReadOnlySpan<byte> le)
    {
        Span<byte> be = stackalloc byte[16];
        for (var i = 0; i < 16; i++)
        {
            be[i] = le[15 - i];
        }
        var hex = Strings.Hex(be);
        return $"{hex[..8]}-{hex[8..12]}-{hex[12..16]}-{hex[16..20]}-{hex[20..]}";
    }
}

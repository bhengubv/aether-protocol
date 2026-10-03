// SPDX-License-Identifier: MIT
// Ported from Fieldwatch app/src/main/java/app/fieldwatch/domain/OpenDroneId.kt (github.com/offgridpete/fieldwatch, cf6562d).
// Copyright (c) 2026 Off Grid Pete LLC. See NOTICE.md.

namespace AetherNet.Aware;

/// <summary>
/// Drone Remote ID (ASTM F3411 / OpenDroneID) from Bluetooth service data FFFA or the Wi-Fi vendor element
/// FA:0B:BC type 0x0D: Location, Basic ID, System (the pilot's position) and Self ID. Heading follows
/// opendroneid.c (direction byte plus the east/west flag).
/// </summary>
public static class OpenDroneId
{
    public const string BleUuid = "FFFA";

    public const string WifiOui = "FA:0B:BC";

    public const int WifiType = 0x0D;

    private const int Msg = 25;
    private const int InvalidDirection = 255;
    private const int InvalidSpeed = 255;

    /// <summary>What a radio's Remote ID packets say, merged (the newest valid value of each wins).</summary>
    public static PayloadLocation FromFacts(RadioFacts facts)
    {
        ArgumentNullException.ThrowIfNull(facts);
        var acc = new PayloadLocation();
        foreach (var sd in facts.ServiceData)
        {
            if (Uuid16(sd.Uuid) == 0xFFFA)
            {
                acc = ParseMessages(MessagesBle(sd.DataHex)).MergeSticky(acc);
            }
        }
        foreach (var ie in facts.VendorIes)
        {
            if (ie.Oui.Equals(WifiOui, StringComparison.OrdinalIgnoreCase) && ie.Type == WifiType)
            {
                acc = ParseMessages(MessagesWifi(ie.DataHex)).MergeSticky(acc);
            }
        }
        return acc;
    }

    /// <summary>
    /// Wi-Fi Remote ID payloads framed like Bluetooth FFFA service data (0x0D, counter, one 25-byte message), so the
    /// stock Remote ID decode map reads them too.
    /// </summary>
    public static IReadOnlyList<string> WifiFffaPayloads(RadioFacts facts)
    {
        ArgumentNullException.ThrowIfNull(facts);
        var output = new List<string>();
        foreach (var ie in facts.VendorIes)
        {
            if (ie.Oui.Equals(WifiOui, StringComparison.OrdinalIgnoreCase) && ie.Type == WifiType)
            {
                output.AddRange(WrapWifiAsFffa(ie.DataHex));
            }
        }
        return output;
    }

    internal static IReadOnlyList<string> WrapWifiAsFffa(string dataHex)
    {
        if (Strings.HexToBytes(dataHex) is not { Length: > 0 } b)
        {
            return [];
        }
        var bleShaped = b.Length >= 2 + Msg && b[0] == 0x0D;
        var start = bleShaped ? 2 : 1;
        if (start > b.Length)
        {
            return [];
        }
        var counter = bleShaped ? b[1] : b[0];
        return FramedMessages(b, start).Select(msg => "0D" + Strings.Hex([counter]) + Strings.Hex(msg)).ToList();
    }

    internal static IReadOnlyList<byte[]> MessagesBle(string dataHex)
    {
        if (Strings.HexToBytes(dataHex) is not { Length: > 0 } b)
        {
            return [];
        }
        var start = b.Length >= 2 + Msg && b[0] == 0x0D ? 2 : 0;
        return FramedMessages(b, start);
    }

    internal static IReadOnlyList<byte[]> MessagesWifi(string dataHex)
    {
        if (Strings.HexToBytes(dataHex) is not { Length: > 0 } b)
        {
            return [];
        }
        // BLE-shaped [0x0D][counter][messages], else ASTM [counter][messages].
        var start = b.Length >= 2 + Msg && b[0] == 0x0D ? 2 : 1;
        return FramedMessages(b, start);
    }

    /// <summary>Single 25-byte messages, or an ASTM message pack (type nibble 0xF).</summary>
    private static List<byte[]> FramedMessages(byte[] b, int start)
    {
        if (start >= b.Length)
        {
            return [];
        }
        var head = b[start];
        if (head >> 4 == 0xF && start + 3 <= b.Length)
        {
            var size = Math.Max((int)b[start + 1], Msg);
            var count = b[start + 2];
            var output = new List<byte[]>(count);
            var i = start + 3;
            for (var n = 0; n < count; n++)
            {
                if (i + Msg > b.Length)
                {
                    continue;
                }
                output.Add(b[i..(i + Msg)]);
                i += size;
            }
            return output;
        }
        return Chunks(b, start);
    }

    private static List<byte[]> Chunks(byte[] b, int start)
    {
        var output = new List<byte[]>();
        for (var i = start; i + Msg <= b.Length; i += Msg)
        {
            output.Add(b[i..(i + Msg)]);
        }
        return output;
    }

    internal static PayloadLocation ParseMessages(IReadOnlyList<byte[]> messages)
    {
        var acc = new PayloadLocation();
        foreach (var m in messages)
        {
            if (m.Length >= Msg)
            {
                acc = ParseOne(m).MergeSticky(acc);
            }
        }
        return acc;
    }

    private static PayloadLocation ParseOne(byte[] m) => (m[0] >> 4) switch
    {
        0 => BasicId(m),
        1 => Location(m),
        3 => SelfId(m),
        4 => SystemMessage(m),
        _ => new PayloadLocation(),
    };

    private static PayloadLocation Location(byte[] m)
    {
        var flags = m[1];
        var eastWest = (flags >> 1) & 1;
        var speedMultiplier = flags & 1;
        var directionRaw = m[2];
        double? heading = directionRaw == InvalidDirection ? null : (directionRaw + (eastWest == 1 ? 180 : 0)) % 360;
        var speedRaw = m[3];
        double? speed = speedRaw == InvalidSpeed
            ? null
            : speedMultiplier == 0 ? speedRaw * 0.25 : (speedRaw * 0.75) + (255 * 0.25);
        var vspeed = (sbyte)m[4] * 0.5;
        return new PayloadLocation
        {
            Lat = I32Le(m, 5) * 1e-7,
            Lon = I32Le(m, 9) * 1e-7,
            Alt = (U16Le(m, 15) * 0.5) - 1000.0,
            HeadingDeg = heading,
            SpeedMps = speed,
            VspeedMps = vspeed,
        };
    }

    private static PayloadLocation BasicId(byte[] m)
    {
        var id = Ascii(m.AsSpan(2, 20)).Trim('\u0000', ' ');
        return new PayloadLocation { UasId = id.Length > 0 ? id : null };
    }

    private static PayloadLocation SelfId(byte[] m)
    {
        var text = Ascii(m.AsSpan(2, 23)).Trim('\u0000', ' ');
        return new PayloadLocation { SelfId = text.Length > 0 ? text : null };
    }

    private static PayloadLocation SystemMessage(byte[] m) =>
        new() { OpLat = I32Le(m, 2) * 1e-7, OpLon = I32Le(m, 6) * 1e-7 };

    /// <summary>US-ASCII as Java decodes it: bytes above 0x7F become U+FFFD.</summary>
    private static string Ascii(ReadOnlySpan<byte> bytes)
    {
        var chars = new char[bytes.Length];
        for (var i = 0; i < bytes.Length; i++)
        {
            chars[i] = bytes[i] < 0x80 ? (char)bytes[i] : '�';
        }
        return new string(chars);
    }

    private static int I32Le(byte[] m, int at) => m[at] | (m[at + 1] << 8) | (m[at + 2] << 16) | (m[at + 3] << 24);

    private static int U16Le(byte[] m, int at) => m[at] | (m[at + 1] << 8);

    private static int? Uuid16(string uuid)
    {
        var hex = Strings.LettersAndDigits(uuid);
        if (hex.Length == 4)
        {
            return Strings.ToIntOrNull(hex, 16);
        }
        if (hex.Length >= 8 && hex.StartsWith("0000", StringComparison.OrdinalIgnoreCase))
        {
            return Strings.ToIntOrNull(hex.Substring(4, 4), 16);
        }
        return Strings.ToIntOrNull(Strings.Take(hex, 4), 16);
    }
}

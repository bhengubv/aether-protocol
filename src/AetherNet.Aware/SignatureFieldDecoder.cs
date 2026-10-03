// SPDX-License-Identifier: MIT
// Ported from Fieldwatch app/src/main/java/app/fieldwatch/domain/SignatureFieldDecoder.kt
// (github.com/offgridpete/fieldwatch, cf6562d). The parse cache is bounded here; upstream's grows for the session.
// Copyright (c) 2026 Off Grid Pete LLC. See NOTICE.md.

using System.Collections.Concurrent;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;

namespace AetherNet.Aware;

/// <summary>One value read from an advert by a signature's decode map.</summary>
public sealed record DecodedFieldValue
{
    public required string FleetId { get; init; }

    public required string FleetName { get; init; }

    public required string Id { get; init; }

    public required string Label { get; init; }

    /// <summary>The value as shown, unit included: "24.3 °C", "separated".</summary>
    public required string Display { get; init; }

    public int Offset { get; init; }

    public int Length { get; init; }

    /// <summary>The scaled number, for numeric fields.</summary>
    public double? Number { get; init; }

    /// <summary>The signature's sentence for this value; empty when none.</summary>
    public string Note { get; init; } = "";

    public bool Live { get; init; }

    public bool Emphasis { get; init; }
}

/// <summary>
/// Reads a signature's decode map from a Bluetooth advert, or from Wi-Fi Remote ID framed like Bluetooth FFFA.
/// Not a matcher: <see cref="SignatureEngine"/> decides the label; this reads the fields of labelled radios.
/// </summary>
public static class SignatureFieldDecoder
{
    private const int CacheLimit = 1024;
    private static readonly byte[] IntelliRocks = Encoding.ASCII.GetBytes("INTELLI_ROCKS");

    // Keyed by the decode map itself, so a changed map never reads a stale parse.
    private static readonly ConditionalWeakTable<FleetDecode, ConcurrentDictionary<string, IReadOnlyList<DecodedFieldValue>>> Cache = new();

    public static IReadOnlyList<DecodedFieldValue> DecodeSighting(Sighting device, IReadOnlyList<Fleet> fleets)
    {
        ArgumentNullException.ThrowIfNull(device);
        ArgumentNullException.ThrowIfNull(fleets);
        if (device.FleetIds.Count == 0)
        {
            return [];
        }
        var byId = new Dictionary<string, Fleet>();
        foreach (var fleet in fleets)
        {
            byId[fleet.Id] = fleet;
        }
        var output = new List<DecodedFieldValue>();
        foreach (var id in device.FleetIds)
        {
            if (byId.TryGetValue(id, out var fleet) && fleet.Decode is { Fields.Count: > 0 } decode)
            {
                output.AddRange(DecodeFleet(fleet, decode, device));
            }
        }
        return output;
    }

    /// <summary>The values the radio's signatures asked to show beside their names, without repeats.</summary>
    public static IReadOnlyList<LiveDecodeChip> LiveChips(Sighting device, IReadOnlyList<Fleet> fleets)
    {
        ArgumentNullException.ThrowIfNull(device);
        if (device.FleetIds.Count == 0)
        {
            return [];
        }
        var output = new List<LiveDecodeChip>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var row in DecodeSighting(device, fleets))
        {
            if (!row.Live)
            {
                continue;
            }
            var text = row.Display.Trim();
            if (text.Length == 0 || !seen.Add(text.ToLowerInvariant()))
            {
                continue;
            }
            output.Add(new LiveDecodeChip(text, row.Emphasis, row.Note.Trim()));
        }
        return output;
    }

    public static IReadOnlyList<DecodedFieldValue> DecodeFleet(Fleet fleet, FleetDecode decode, Sighting device)
    {
        ArgumentNullException.ThrowIfNull(fleet);
        ArgumentNullException.ThrowIfNull(decode);
        ArgumentNullException.ThrowIfNull(device);
        var candidates = Payloads(decode, device);
        if (candidates.Count == 0)
        {
            return [];
        }
        var cache = Cache.GetValue(decode, _ => new ConcurrentDictionary<string, IReadOnlyList<DecodedFieldValue>>(StringComparer.Ordinal));
        var output = new List<DecodedFieldValue>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (bytes, hex) in candidates)
        {
            var key = fleet.Id + "\u0001" + fleet.Name + "\u0001" + hex;
            if (!cache.TryGetValue(key, out var parsed))
            {
                parsed = Parse(fleet, decode, bytes);
                if (cache.Count >= CacheLimit)
                {
                    cache.Clear();
                }
                cache[key] = parsed;
            }
            foreach (var row in parsed)
            {
                if (seen.Add(row.Id))
                {
                    output.Add(row);
                }
            }
        }
        return output;
    }

    private static List<(byte[] Bytes, string Hex)> Payloads(FleetDecode decode, Sighting device)
    {
        IEnumerable<string> hexes;
        switch (decode.Source)
        {
            case DecodeSource.ManufacturerData:
                if (device.Kind != RadioKind.Ble)
                {
                    return [];
                }
                var records = SignatureEngine.MfgRecords(device);
                var want = decode.CompanyId is { } c && c != 0 ? c : (int?)null;
                hexes = records
                    .Where(r => want is null || r.CompanyId == want)
                    .Select(r => decode.IncludeCompanyId ? CompanyIdPrefix(r.CompanyId) + r.DataHex : r.DataHex)
                    .ToList();
                break;
            case DecodeSource.ServiceData:
                if (decode.ServiceUuid is null)
                {
                    return [];
                }
                var uuid = UuidKey(decode.ServiceUuid);
                var ads = device.Facts.ServiceData.Where(s => UuidKey(s.Uuid) == uuid).Select(s => s.DataHex).ToList();
                hexes = uuid == "FFFA" ? ads.Concat(OpenDroneId.WifiFffaPayloads(device.Facts)).ToList() : ads;
                break;
            default:
                return [];
        }
        var output = new List<(byte[], string)>();
        foreach (var hex in hexes)
        {
            if (Strings.HexToBytes(hex) is not { } raw)
            {
                continue;
            }
            var bytes = StripIntelliRocks(raw);
            if (bytes.Length > 0)
            {
                output.Add((bytes, Strings.Hex(bytes)));
            }
        }
        return output;
    }

    private static List<DecodedFieldValue> Parse(Fleet fleet, FleetDecode decode, byte[] payload)
    {
        var output = new List<DecodedFieldValue>(decode.Fields.Count);
        foreach (var field in decode.Fields)
        {
            if (!GateOk(field.Gate, payload))
            {
                continue;
            }
            var len = field.ResolvedLength();
            if (field.Offset < 0 || len < 1 || field.Offset + len > payload.Length)
            {
                continue;
            }
            ParsedField? parsed;
            try
            {
                parsed = FormatField(field, payload);
            }
            catch (Exception e) when (e is ArgumentException or OverflowException or IndexOutOfRangeException)
            {
                parsed = null;
            }
            if (parsed is null || parsed.Display.Length == 0)
            {
                continue;
            }
            output.Add(new DecodedFieldValue
            {
                FleetId = fleet.Id,
                FleetName = fleet.Name,
                Id = field.Id,
                Label = field.Label,
                Display = parsed.Display,
                Offset = field.Offset,
                Length = len,
                Number = parsed.Number,
                Note = EnumNote(field, parsed.RawKey),
                Live = field.Live,
                Emphasis = field.Live && Emphasized(field, parsed.RawKey),
            });
        }
        return output;
    }

    private static bool GateOk(DecodeWhen? gate, byte[] payload)
    {
        for (var g = gate; g is not null; g = g.And)
        {
            if (!GateOkOnce(g, payload))
            {
                return false;
            }
        }
        return true;
    }

    private static bool GateOkOnce(DecodeWhen gate, byte[] payload)
    {
        if (gate.Op == DecodeWhenOp.Len)
        {
            return payload.Length == Math.Max(gate.Length, 1);
        }
        if (Strings.HexToBytes(gate.ValueHex) is not { } want)
        {
            return false;
        }
        var len = Math.Max(want.Length, 1);
        if (gate.Offset < 0 || gate.Offset + len > payload.Length)
        {
            return false;
        }
        var got = payload.AsSpan(gate.Offset, len);
        switch (gate.Op)
        {
            case DecodeWhenOp.Eq:
                return got.SequenceEqual(want);
            case DecodeWhenOp.Neq:
                return !got.SequenceEqual(want);
            case DecodeWhenOp.Mask:
                for (var i = 0; i < got.Length; i++)
                {
                    if ((got[i] & want[i]) != want[i])
                    {
                        return false;
                    }
                }
                return true;
            case DecodeWhenOp.Nmask:
                for (var i = 0; i < got.Length; i++)
                {
                    if ((got[i] & want[i]) != 0)
                    {
                        return false;
                    }
                }
                return true;
            default:
                return false;
        }
    }

    private sealed record ParsedField(string Display, double? Number, string RawKey);

    private static bool Emphasized(DecodeField field, string rawKey)
    {
        if (field.LiveEmphasis.Count == 0 || rawKey.Length == 0)
        {
            return false;
        }
        var key = DecodeNormalize.NormalizeEnumKey(rawKey);
        return field.LiveEmphasis.Any(e => DecodeNormalize.NormalizeEnumKey(e) == key) || field.LiveEmphasis.Contains(rawKey);
    }

    private static string EnumNote(DecodeField field, string rawKey)
    {
        if (field.EnumNotes is not { } notes || rawKey.Length == 0)
        {
            return "";
        }
        if (notes.TryGetValue(rawKey, out var direct))
        {
            return direct.Trim();
        }
        var key = DecodeNormalize.NormalizeEnumKey(rawKey);
        if (notes.TryGetValue(key, out var normalized))
        {
            return normalized.Trim();
        }
        foreach (var (k, v) in notes)
        {
            if (DecodeNormalize.NormalizeEnumKey(k) == key)
            {
                return v.Trim();
            }
        }
        return "";
    }

    private static ParsedField? FormatField(DecodeField field, byte[] payload)
    {
        var len = field.ResolvedLength();
        var slice = payload.AsSpan(field.Offset, len);
        var le = field.Endian != DecodeEndian.Be;
        var unit = field.Unit?.Trim() ?? "";
        double? rawNum = null;
        string rawKey;
        string? text;
        switch (field.Type)
        {
            case DecodeType.Utf8:
            {
                var s = TrimLowAndNul(Encoding.UTF8.GetString(slice));
                rawKey = s;
                text = s.Length == 0 ? null : s;
                break;
            }
            case DecodeType.Hex:
            {
                var h = Strings.Hex(slice);
                rawKey = h;
                text = Strings.Spaced(h);
                break;
            }
            case DecodeType.Mac:
            {
                if (slice.Length < 6)
                {
                    return null;
                }
                var mac = string.Join(':', slice[..6].ToArray().Select(b => b.ToString("X2", CultureInfo.InvariantCulture)));
                rawKey = mac;
                text = mac;
                break;
            }
            case DecodeType.Bool:
            {
                var on = slice.ContainsAnyExcept((byte)0);
                rawNum = on ? 1.0 : 0.0;
                rawKey = on ? "1" : "0";
                text = on ? "yes" : "no";
                break;
            }
            case DecodeType.Bits:
            {
                var width = Math.Clamp(field.BitWidth ?? 1, 1, 32);
                var bitStart = field.BitOffset ?? 0;
                var word = ReadU(slice, le);
                var value = (word >> bitStart) & ((1L << width) - 1);
                rawNum = value;
                rawKey = Inv(value);
                text = rawKey;
                break;
            }
            case DecodeType.F32:
            {
                if (slice.Length < 4)
                {
                    return null;
                }
                var f = (double)BitConverter.Int32BitsToSingle(unchecked((int)ReadU(slice[..4], le)));
                rawNum = f;
                rawKey = f.ToString("R", CultureInfo.InvariantCulture);
                text = FormatNumber(f);
                break;
            }
            case DecodeType.U8 or DecodeType.U16 or DecodeType.U24 or DecodeType.U32:
            {
                var v = ReadU(slice, le);
                rawNum = v;
                rawKey = Inv(v);
                text = rawKey;
                break;
            }
            case DecodeType.I8 or DecodeType.I16 or DecodeType.I32:
            {
                var v = SignExtend(ReadU(slice, le), slice.Length * 8);
                rawNum = v;
                rawKey = Inv(v);
                text = rawKey;
                break;
            }
            default:
                return null;
        }
        if (text is null)
        {
            return null;
        }
        var scaling = field.Scale is not null || field.OffsetAdd is not null || field.Modulo is not null;
        double? scaledNum = rawNum;
        if (rawNum is { } raw && scaling)
        {
            var n = raw;
            if (field.Modulo is { } m && m != 0.0)
            {
                n %= m;
            }
            if (field.Scale is { } scale)
            {
                n *= scale;
            }
            if (field.OffsetAdd is { } add)
            {
                n += add;
            }
            scaledNum = n;
        }
        var scaled = rawNum is not null && scaling ? FormatNumber(scaledNum!.Value) : text;
        string? mapped = null;
        if (field.EnumLabels is { } labels)
        {
            mapped = labels.GetValueOrDefault(rawKey) ?? labels.GetValueOrDefault("0x" + rawKey.ToUpperInvariant());
            if (mapped is null && rawNum is { } number)
            {
                var whole = (long)number;
                mapped = labels.GetValueOrDefault(Inv(whole)) ?? labels.GetValueOrDefault("0x" + whole.ToString("X", CultureInfo.InvariantCulture));
            }
        }
        var shown = mapped ?? scaled;
        var display = unit.Length == 0 ? shown : shown + " " + unit;
        return new ParsedField(display, scaledNum is { } sn && double.IsFinite(sn) ? sn : null, rawKey);
    }

    private static long ReadU(ReadOnlySpan<byte> bytes, bool le)
    {
        long v = 0;
        if (le)
        {
            for (var i = 0; i < bytes.Length; i++)
            {
                v |= (long)bytes[i] << (8 * i);
            }
        }
        else
        {
            foreach (var b in bytes)
            {
                v = (v << 8) | b;
            }
        }
        return v;
    }

    private static long SignExtend(long v, int bits)
    {
        var shift = 64 - bits;
        return (v << shift) >> shift;
    }

    /// <summary>Whole numbers without a point; others to six places with the trailing zeros cut. Always a '.'.</summary>
    private static string FormatNumber(double n)
    {
        if (!double.IsFinite(n))
        {
            return n.ToString(CultureInfo.InvariantCulture);
        }
        if (n == (long)n)
        {
            return Inv((long)n);
        }
        return n.ToString("F6", CultureInfo.InvariantCulture).TrimEnd('0').TrimEnd('.');
    }

    private static string Inv(long v) => v.ToString(CultureInfo.InvariantCulture);

    private static string TrimLowAndNul(string s)
    {
        var start = 0;
        var end = s.Length;
        while (start < end && s[start] <= ' ')
        {
            start++;
        }
        while (end > start && s[end - 1] <= ' ')
        {
            end--;
        }
        return s[start..end];
    }

    private static string UuidKey(string uuid)
    {
        var hex = Strings.HexOnly(uuid);
        return hex.Length switch
        {
            4 => hex,
            32 when hex.StartsWith("0000", StringComparison.Ordinal) && hex.EndsWith("00001000800000805F9B34FB", StringComparison.Ordinal) =>
                hex.Substring(4, 4),
            _ => hex,
        };
    }

    private static string CompanyIdPrefix(int companyId) => Strings.Hex([(byte)(companyId & 0xFF), (byte)((companyId >> 8) & 0xFF)]);

    /// <summary>Govee sometimes glues ASCII "INTELLI_ROCKS" onto the sensor payload; cut it so the length gates fit.</summary>
    private static byte[] StripIntelliRocks(byte[] bytes)
    {
        if (bytes.Length <= IntelliRocks.Length)
        {
            return bytes;
        }
        var idx = bytes.AsSpan().IndexOf(IntelliRocks);
        if (idx < 0)
        {
            return bytes;
        }
        return idx == 0 ? [] : bytes[..idx];
    }
}

/// <summary>Tidies decode-map values as a person types them.</summary>
public static class DecodeNormalize
{
    /// <summary>Enum keys as decimal, so "0x05", "05" and "5" all match a u8 of 5.</summary>
    public static string NormalizeEnumKey(string raw)
    {
        ArgumentNullException.ThrowIfNull(raw);
        var t = raw.Trim();
        if (t.Length == 0)
        {
            return t;
        }
        if (t.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            return Strings.ToLongOrNull(t[2..], 16) is { } fromHex ? fromHex.ToString(CultureInfo.InvariantCulture) : t;
        }
        if (Strings.ToLongOrNull(t) is { } plain)
        {
            return plain.ToString(CultureInfo.InvariantCulture);
        }
        var hex = Strings.LettersAndDigits(t);
        if (hex.Any(c => c is >= 'A' and <= 'F' or >= 'a' and <= 'f') && Strings.ToLongOrNull(hex, 16) is { } parsed)
        {
            return parsed.ToString(CultureInfo.InvariantCulture);
        }
        return t;
    }

    public static IReadOnlyDictionary<string, string>? NormalizeEnumLabels(IReadOnlyDictionary<string, string>? map)
    {
        if (map is null || map.Count == 0)
        {
            return null;
        }
        var output = new OrderedDictionary<string, string>();
        foreach (var (k, v) in map)
        {
            var key = NormalizeEnumKey(k);
            var label = v.Trim();
            if (key.Length > 0 && label.Length > 0)
            {
                output[key] = label;
            }
        }
        return output.Count == 0 ? null : output;
    }

    /// <summary>"0x5" becomes "05": no prefix, letters and digits only, upper case, even length.</summary>
    public static string NormalizeValueHex(string raw)
    {
        ArgumentNullException.ThrowIfNull(raw);
        var h = raw.Trim();
        if (h.StartsWith("0x", StringComparison.Ordinal))
        {
            h = h[2..];
        }
        if (h.StartsWith("0X", StringComparison.Ordinal))
        {
            h = h[2..];
        }
        h = Strings.HexOnly(h);
        return h.Length % 2 == 1 ? "0" + h : h;
    }

    /// <summary>A gate with its hex tidied and its length taken from the hex; a gate with no hex is dropped.</summary>
    public static DecodeWhen? NormalizeGate(DecodeWhen? gate)
    {
        if (gate is null)
        {
            return null;
        }
        var rest = NormalizeGate(gate.And);
        if (gate.Op == DecodeWhenOp.Len)
        {
            return new DecodeWhen { Offset = 0, Length = Math.Max(gate.Length, 1), Op = DecodeWhenOp.Len, ValueHex = "", And = rest };
        }
        var hex = NormalizeValueHex(gate.ValueHex);
        if (hex.Length == 0)
        {
            return rest;
        }
        return new DecodeWhen
        {
            Offset = Math.Max(gate.Offset, 0),
            Length = Math.Max(hex.Length / 2, 1),
            Op = gate.Op,
            ValueHex = hex,
            And = rest,
        };
    }

    public static DecodeField Normalized(this DecodeField field)
    {
        ArgumentNullException.ThrowIfNull(field);
        return field with { EnumLabels = NormalizeEnumLabels(field.EnumLabels), Gate = NormalizeGate(field.Gate) };
    }
}

public static class SightingDecode
{
    /// <summary>The radio with its live decode values brought up to date; the same instance when nothing changed.</summary>
    public static Sighting WithLiveDecode(this Sighting device, IReadOnlyList<Fleet> fleets)
    {
        var chips = SignatureFieldDecoder.LiveChips(device, fleets);
        return chips.SequenceEqual(device.LiveDecode) ? device : device with { LiveDecode = chips };
    }
}

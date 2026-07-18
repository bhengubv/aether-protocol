// SPDX-License-Identifier: MIT

using System.Buffers;
using System.Globalization;
using System.Text;

namespace AetherNet.BitTorrent.Bencoding;

/// <summary>
/// Bencode encoder/decoder (BEP-3).
///
/// <para>Decoding is strict: integers reject leading zeros and negative zero; string lengths reject
/// leading zeros; dictionaries reject duplicate keys and non-string keys; <see cref="Decode(ReadOnlySpan{byte})"/>
/// rejects trailing data.</para>
///
/// <para>Encoding is canonical: dictionary keys are emitted sorted by raw byte order, so a
/// decode → encode round-trip of canonical input (which every real <c>.torrent</c> is) is byte-exact.</para>
/// </summary>
public static class Bencode
{
    // ── Decode ────────────────────────────────────────────────────────────────

    /// <summary>Decode exactly one bencode value from <paramref name="data"/>; trailing bytes are an error.</summary>
    public static BencodeValue Decode(ReadOnlySpan<byte> data)
    {
        int pos = 0;
        var value = DecodeValue(data, ref pos);
        if (pos != data.Length)
            throw new BencodeException($"trailing data after bencode value at offset {pos} of {data.Length}");
        return value;
    }

    /// <summary>Decode one bencode value and report how many bytes it consumed; trailing bytes are allowed.</summary>
    public static BencodeValue Decode(ReadOnlySpan<byte> data, out int consumed)
    {
        int pos = 0;
        var value = DecodeValue(data, ref pos);
        consumed = pos;
        return value;
    }

    public static BencodeValue Decode(byte[] data) => Decode(data.AsSpan());

    private static BencodeValue DecodeValue(ReadOnlySpan<byte> data, ref int pos)
    {
        if (pos >= data.Length) throw new BencodeException("unexpected end of input");
        byte c = data[pos];
        if (c == (byte)'i') return DecodeInteger(data, ref pos);
        if (c == (byte)'l') return DecodeList(data, ref pos);
        if (c == (byte)'d') return DecodeDictionary(data, ref pos);
        if (c is >= (byte)'0' and <= (byte)'9') return DecodeString(data, ref pos);
        throw new BencodeException($"unexpected byte 0x{c:x2} ('{(char)c}') at offset {pos}");
    }

    private static BencodeInteger DecodeInteger(ReadOnlySpan<byte> data, ref int pos)
    {
        int start = pos + 1; // skip 'i'
        int rel = data[start..].IndexOf((byte)'e');
        if (rel < 0) throw new BencodeException($"unterminated integer at offset {pos}");
        var digits = data.Slice(start, rel);
        pos = start + rel + 1; // past the 'e'

        if (digits.Length == 0) throw new BencodeException("empty integer 'ie'");
        int i = 0;
        bool negative = digits[0] == (byte)'-';
        if (negative)
        {
            i = 1;
            if (digits.Length == 1) throw new BencodeException("integer is a lone '-'");
        }
        for (int k = i; k < digits.Length; k++)
            if (digits[k] is < (byte)'0' or > (byte)'9')
                throw new BencodeException($"non-digit in integer at offset {start + k}");
        if (digits[i] == (byte)'0' && digits.Length - i > 1)
            throw new BencodeException("integer has a leading zero");
        if (negative && digits[i] == (byte)'0')
            throw new BencodeException("negative zero is not a valid integer");

        if (!long.TryParse(Encoding.ASCII.GetString(digits), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out long value))
            throw new BencodeException("integer out of 64-bit range");
        return new BencodeInteger(value);
    }

    private static BencodeString DecodeString(ReadOnlySpan<byte> data, ref int pos)
    {
        int rel = data[pos..].IndexOf((byte)':');
        if (rel < 0) throw new BencodeException($"string length at offset {pos} not terminated by ':'");
        var lenSpan = data.Slice(pos, rel);

        if (lenSpan.Length == 0) throw new BencodeException($"empty string length at offset {pos}");
        for (int k = 0; k < lenSpan.Length; k++)
            if (lenSpan[k] is < (byte)'0' or > (byte)'9')
                throw new BencodeException($"non-digit in string length at offset {pos + k}");
        if (lenSpan.Length > 1 && lenSpan[0] == (byte)'0')
            throw new BencodeException("string length has a leading zero");

        if (!long.TryParse(Encoding.ASCII.GetString(lenSpan), NumberStyles.None, CultureInfo.InvariantCulture, out long len))
            throw new BencodeException("string length out of range");

        int contentStart = pos + rel + 1;
        if (len < 0 || contentStart + len > data.Length)
            throw new BencodeException($"string length {len} exceeds available input at offset {pos}");

        var bytes = data.Slice(contentStart, (int)len).ToArray();
        pos = contentStart + (int)len;
        return new BencodeString(bytes);
    }

    private static BencodeList DecodeList(ReadOnlySpan<byte> data, ref int pos)
    {
        pos++; // skip 'l'
        var list = new BencodeList();
        while (true)
        {
            if (pos >= data.Length) throw new BencodeException("unterminated list");
            if (data[pos] == (byte)'e') { pos++; break; }
            list.Items.Add(DecodeValue(data, ref pos));
        }
        return list;
    }

    private static BencodeDictionary DecodeDictionary(ReadOnlySpan<byte> data, ref int pos)
    {
        pos++; // skip 'd'
        var dict = new BencodeDictionary();
        while (true)
        {
            if (pos >= data.Length) throw new BencodeException("unterminated dictionary");
            if (data[pos] == (byte)'e') { pos++; break; }
            if (data[pos] is < (byte)'0' or > (byte)'9')
                throw new BencodeException($"dictionary key must be a byte string at offset {pos}");
            var key = DecodeString(data, ref pos).Value;
            var value = DecodeValue(data, ref pos);
            dict.Add(key, value); // throws on duplicate
        }
        return dict;
    }

    // ── Encode ────────────────────────────────────────────────────────────────

    /// <summary>Canonical bencode encoding (dictionary keys sorted by raw byte order).</summary>
    public static byte[] Encode(BencodeValue value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var buffer = new ArrayBufferWriter<byte>();
        EncodeValue(value, buffer);
        return buffer.WrittenSpan.ToArray();
    }

    private static void EncodeValue(BencodeValue value, ArrayBufferWriter<byte> w)
    {
        switch (value)
        {
            case BencodeInteger i:
                WriteByte(w, (byte)'i');
                WriteAscii(w, i.Value.ToString(CultureInfo.InvariantCulture));
                WriteByte(w, (byte)'e');
                break;
            case BencodeString s:
                WriteAscii(w, s.Value.Length.ToString(CultureInfo.InvariantCulture));
                WriteByte(w, (byte)':');
                w.Write(s.Value);
                break;
            case BencodeList l:
                WriteByte(w, (byte)'l');
                foreach (var item in l.Items) EncodeValue(item, w);
                WriteByte(w, (byte)'e');
                break;
            case BencodeDictionary d:
                WriteByte(w, (byte)'d');
                foreach (var (key, val) in d.SortedEntries())
                {
                    WriteAscii(w, key.Length.ToString(CultureInfo.InvariantCulture));
                    WriteByte(w, (byte)':');
                    w.Write(key);
                    EncodeValue(val, w);
                }
                WriteByte(w, (byte)'e');
                break;
            default:
                throw new BencodeException($"unknown bencode value type {value.GetType().Name}");
        }
    }

    private static void WriteByte(ArrayBufferWriter<byte> w, byte b)
    {
        var span = w.GetSpan(1);
        span[0] = b;
        w.Advance(1);
    }

    private static void WriteAscii(ArrayBufferWriter<byte> w, string ascii)
    {
        var span = w.GetSpan(ascii.Length);
        for (int i = 0; i < ascii.Length; i++) span[i] = (byte)ascii[i];
        w.Advance(ascii.Length);
    }
}

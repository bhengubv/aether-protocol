// SPDX-License-Identifier: MIT

using System.Globalization;
using System.Text;

namespace AetherNet.Aware;

/// <summary>
/// Hex and text helpers. The port reads hex and numbers the way Fieldwatch's Kotlin does (letters and digits only,
/// upper-cased; Kotlin's <c>toIntOrNull</c>/<c>toLongOrNull</c> rules), except that malformed hex is skipped where
/// Kotlin would throw.
/// </summary>
internal static class Strings
{
    /// <summary>Upper-case hex of the bytes, no separators.</summary>
    public static string Hex(ReadOnlySpan<byte> bytes) => Convert.ToHexString(bytes);

    /// <summary>Only the letters and digits of <paramref name="raw"/>: "aa:bb-cc" becomes "aabbcc".</summary>
    public static string LettersAndDigits(string raw)
    {
        if (raw.Length == 0)
        {
            return raw;
        }
        var sb = new StringBuilder(raw.Length);
        foreach (var ch in raw)
        {
            if (char.IsLetterOrDigit(ch))
            {
                sb.Append(ch);
            }
        }
        return sb.Length == raw.Length ? raw : sb.ToString();
    }

    /// <summary>Letters and digits, upper-cased: "aa:bb-cc" becomes "AABBCC".</summary>
    public static string HexOnly(string raw) => LettersAndDigits(raw).ToUpperInvariant();

    /// <summary>Bytes of a hex string, separators ignored. Null when empty, of odd length, or not hex.</summary>
    public static byte[]? HexToBytes(string hex)
    {
        var h = LettersAndDigits(hex);
        if (h.Length == 0 || h.Length % 2 != 0)
        {
            return null;
        }
        var bytes = new byte[h.Length / 2];
        for (var i = 0; i < bytes.Length; i++)
        {
            var hi = Digit(h[2 * i], 16);
            var lo = Digit(h[(2 * i) + 1], 16);
            if (hi < 0 || lo < 0)
            {
                return null;
            }
            bytes[i] = (byte)((hi << 4) | lo);
        }
        return bytes;
    }

    /// <summary>"0102AB" becomes "01 02 AB".</summary>
    public static string Spaced(string hex)
    {
        if (hex.Length <= 2)
        {
            return hex;
        }
        var sb = new StringBuilder(hex.Length + (hex.Length / 2));
        for (var i = 0; i < hex.Length; i += 2)
        {
            if (i > 0)
            {
                sb.Append(' ');
            }
            sb.Append(hex, i, Math.Min(2, hex.Length - i));
        }
        return sb.ToString();
    }

    public static string Take(string s, int n) => s.Length <= n ? s : s[..n];

    public static string TakeLast(string s, int n) => s.Length <= n ? s : s[^n..];

    public static bool IsBlank(string? s) => string.IsNullOrWhiteSpace(s);

    /// <summary>Kotlin's <c>String.toLongOrNull(radix)</c>: an optional sign, then digits; null on anything else or overflow.</summary>
    public static long? ToLongOrNull(string s, int radix = 10)
    {
        if (s.Length == 0)
        {
            return null;
        }
        var i = 0;
        var negative = false;
        if (s[0] is '-' or '+')
        {
            if (s.Length == 1)
            {
                return null;
            }
            negative = s[0] == '-';
            i = 1;
        }
        var limit = negative ? 9_223_372_036_854_775_808UL : long.MaxValue;
        ulong acc = 0;
        for (; i < s.Length; i++)
        {
            var d = Digit(s[i], radix);
            if (d < 0 || acc > (limit - (ulong)d) / (ulong)radix)
            {
                return null;
            }
            acc = (acc * (ulong)radix) + (ulong)d;
        }
        return negative ? unchecked((long)(0UL - acc)) : (long)acc;
    }

    /// <summary>Kotlin's <c>String.toIntOrNull(radix)</c>.</summary>
    public static int? ToIntOrNull(string s, int radix = 10) =>
        ToLongOrNull(s, radix) is { } v && v is >= int.MinValue and <= int.MaxValue ? (int)v : null;

    private static int Digit(char c, int radix)
    {
        var v = c switch
        {
            >= '0' and <= '9' => c - '0',
            >= 'a' and <= 'z' => c - 'a' + 10,
            >= 'A' and <= 'Z' => c - 'A' + 10,
            _ => CharUnicodeInfo.GetDecimalDigitValue(c),
        };
        return v >= 0 && v < radix ? v : -1;
    }
}
